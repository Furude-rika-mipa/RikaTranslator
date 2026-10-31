using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GameTranslateToolkit.ScriptTranslation;

public sealed record PsbPatchResult(byte[] Bytes, string Name, int Changed, int Preserved);

public sealed partial class PsbScenarioReader
{
    // Lossless typed tree: scalar payloads, chunk references, names and binary resources are preserved.
    private sealed class PatchNode
    {
        public byte Tag;
        public byte[] Scalar = [];
        public int StringIndex = -1;
        public int[] Keys = [];
        public List<PatchNode> Items = [];
    }
    private string PatchString(int index)
    {
        if ((uint)index >= _strings.Length) throw Bad();
        var start = checked(_stringsData + _strings[index]);
        if ((uint)start >= _data.Length) throw Bad();
        var end = System.Array.IndexOf(_data, (byte)0, start);
        if (end < start || end - start > 262144) throw Bad();
        return Utf8.GetString(_data, start, end - start);
    }
    private PatchNode PatchRead(int offset, HashSet<int> visiting, ref int nodes, int depth = 0)
    {
        if (depth > 96 || ++nodes > 500_000 || !visiting.Add(offset)) throw Bad();
        try
        {
            var node = new PatchNode { Tag = B(offset) };
            if (node.Tag == 0x20)
            {
                var (positions, end) = Array(offset + 1);
                foreach (var position in positions) node.Items.Add(PatchRead(checked(end + position), visiting, ref nodes, depth + 1));
            }
            else if (node.Tag == 0x21)
            {
                var (keys, next) = Array(offset + 1); var (positions, end) = Array(next);
                if (keys.Length != positions.Length || keys.Distinct().Count() != keys.Length || keys.Any(k => !_names.ContainsKey(k))) throw Bad();
                node.Keys = keys;
                foreach (var position in positions) node.Items.Add(PatchRead(checked(end + position), visiting, ref nodes, depth + 1));
            }
            else
            {
                var size = node.Tag switch {
                    1 or 2 or 3 or 0x1d => 0, >= 4 and <= 12 => node.Tag - 4,
                    >= 0x15 and <= 0x18 => node.Tag - 0x14,
                    >= 0x19 and <= 0x1c => node.Tag - 0x18,
                    >= 0x22 and <= 0x25 => node.Tag - 0x21,
                    0x1e => 4, 0x1f => 8, _ => throw Bad() };
                if ((long)offset + 1 + size > _data.Length) throw Bad();
                node.Scalar = _data.AsSpan(offset + 1, size).ToArray();
                if (node.Tag is >= 0x15 and <= 0x18) { node.StringIndex = U(offset + 1, size); _ = PatchString(node.StringIndex); }
            }
            return node;
        }
        finally { visiting.Remove(offset); }
    }
    private PatchNode? PatchField(PatchNode node, string key)
    {
        if (node.Tag != 0x21) return null;
        for (var i = 0; i < node.Keys.Length; i++) if (_names[node.Keys[i]] == key) return node.Items[i];
        return null;
    }
    private static byte[] PatchArray(IReadOnlyList<int> values)
    {
        var result = new byte[checked(6 + values.Count * 4)]; result[0] = 0x10; result[5] = 0x10;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), values.Count);
        for (var i = 0; i < values.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(6 + i * 4), values[i]);
        return result;
    }
    private static byte[] PatchEncode(PatchNode node)
    {
        using var stream = new MemoryStream(); stream.WriteByte(node.Tag);
        if (node.Tag is 0x20 or 0x21)
        {
            var bodies = node.Items.Select(PatchEncode).ToArray();
            var offsets = new int[bodies.Length]; var length = 0;
            for (var i = 0; i < bodies.Length; i++) { offsets[i] = length; length = checked(length + bodies[i].Length); }
            if (length > MaxBytes) throw Bad();
            if (node.Tag == 0x21) stream.Write(PatchArray(node.Keys));
            stream.Write(PatchArray(offsets));
            foreach (var body in bodies) stream.Write(body);
        }
        else if (node.StringIndex >= 0)
        {
            // A new translation can require a larger string index than the original field width.
            stream.Position = 0; stream.WriteByte(0x18);
            Span<byte> index = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(index, node.StringIndex); stream.Write(index);
        }
        else stream.Write(node.Scalar);
        return stream.ToArray();
    }
    private static uint PatchAdler(ReadOnlySpan<byte> bytes)
    {
        uint a = 1, b = 0;
        foreach (var value in bytes) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return b << 16 | a;
    }

    public PsbPatchResult BuildPatch(string resourceId, IReadOnlyDictionary<string, ScriptSegment> translated)
    {
        if (U(4, 2) != 3 || U(8, 4) != 44 || U(12, 4) < 44 || BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(40, 4)) != PatchAdler(_data.AsSpan(8, 32)))
            throw new InvalidDataException("补丁生成暂支持校验有效的 PSB 3 场景资源，请重新提取或检查游戏版本。");
        var count = 0; var root = PatchRead(_root, [], ref count);
        var scenes = PatchField(root, "scenes"); var nameNode = PatchField(root, "name");
        if (scenes?.Tag != 0x20 || nameNode?.StringIndex is not >= 0) throw Bad();
        var name = PatchString(nameNode.StringIndex);
        var values = Enumerable.Range(0, _strings.Length).Select(PatchString).ToList();
        var appended = new Dictionary<string, int>(StringComparer.Ordinal);
        var changed = 0; var preserved = 0;
        for (var si = 0; si < scenes.Items.Count; si++)
        {
            var texts = PatchField(scenes.Items[si], "texts"); if (texts?.Tag != 0x20) continue;
            for (var ti = 0; ti < texts.Items.Count; ti++)
            {
                var text = texts.Items[ti]; if (text.Tag != 0x20 || text.Items.Count < 2 || text.Items[1].Tag != 0x20) continue;
                var pieces = text.Items[1].Items;
                for (var pi = 0; pi < pieces.Count; pi++)
                {
                    var piece = pieces[pi]; if (piece.Tag != 0x20 || piece.Items.Count < 2 || piece.Items[1].StringIndex < 0) continue;
                    var node = piece.Items[1]; var source = PatchString(node.StringIndex);
                    if (string.IsNullOrWhiteSpace(source)) continue;
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceId}:{si}:{ti}:{pi}:{source}"))).ToLowerInvariant();
                    if (!translated.TryGetValue(id, out var line)) { preserved++; continue; }
                    if (line.Status != "translated" || line.Source != source || string.IsNullOrWhiteSpace(line.Translation) || line.Translation.Length > 32768 ||
                        line.Translation.Any(c => c == '\0')) throw new InvalidDataException("译文与原始剧本不一致，请重新提取核对；没有生成补丁。");
                    var value = TranslationService.ValidateManualTranslation(source, line.Translation);
                    if (!appended.TryGetValue(value, out var index)) { index = values.Count; values.Add(value); appended.Add(value, index); }
                    node.StringIndex = index; changed++;
                }
            }
        }
        if (changed == 0) return new(_original, name, 0, preserved);
        using var strings = new MemoryStream(); var stringOffsets = new List<int>();
        foreach (var value in values) { stringOffsets.Add(checked((int)strings.Position)); strings.Write(Utf8.GetBytes(value)); strings.WriteByte(0); }
        var offsets = Enumerable.Range(0, 7).Select(i => (Field: 12 + i * 4, Offset: U(12 + i * 4, 4))).ToArray();
        if (offsets.Any(p => p.Offset < 44 || p.Offset > _data.Length)) throw Bad();
        using var output = new MemoryStream(); output.Write(_data, 0, 44);
        Span<byte> number = stackalloc byte[4];
        foreach (var section in offsets.OrderBy(p => p.Offset).ThenBy(p => p.Field))
        {
            var next = offsets.Where(p => p.Offset > section.Offset).Select(p => p.Offset).DefaultIfEmpty(_data.Length).Min();
            var position = checked((int)output.Position);
            var bytes = section.Field switch { 16 => PatchArray(stringOffsets), 20 => strings.ToArray(), 36 => PatchEncode(root),
                _ => _data.AsSpan(section.Offset, next - section.Offset).ToArray() };
            output.Write(bytes);
            var end = output.Position; output.Position = section.Field;
            BinaryPrimitives.WriteInt32LittleEndian(number, position); output.Write(number); output.Position = end;
            if (output.Length > MaxBytes) throw new InvalidDataException("重建的剧本超过 32 MB，未生成补丁。");
        }
        var psb = output.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(40), PatchAdler(psb.AsSpan(8, 32)));
        // Round-trip all dialogue: untouched rows must remain byte-for-byte text-identical.
        var before = Extract(resourceId).Segments; var after = new PsbScenarioReader(psb).Extract(resourceId).Segments;
        if (before.Count != after.Count) throw Bad();
        for (var i = 0; i < before.Count; i++)
        {
            var expected = translated.TryGetValue(before[i].Id, out var edited) ? edited.Translation : before[i].Source;
            if (after[i].Source != expected || after[i].Speaker != before[i].Speaker) throw new InvalidDataException("剧本回读校验失败，没有生成补丁。");
        }
        using var mdf = new MemoryStream(); mdf.Write("mdf\0"u8);
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, psb.Length); mdf.Write(size);
        using (var zlib = new ZLibStream(mdf, CompressionLevel.Optimal, true)) zlib.Write(psb);
        return new(mdf.ToArray(), name, changed, preserved);
    }
}
