using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GameTranslateToolkit.ScriptTranslation;

// PSB layout reference: GARbro ArcPSB.cs, Copyright (C) 2016 morkt, MIT.
// This bounded reader only extracts structured scenario text; it never executes scripts.
public sealed partial class PsbScenarioReader
{
    public const int MaxBytes = 32 * 1024 * 1024;
    private readonly byte[] _data;
    private readonly byte[] _original;
    private readonly Dictionary<int, string> _names = [];
    private readonly Dictionary<int, object?> _objects = [];
    private readonly HashSet<int> _reading = [];
    private readonly int[] _strings;
    private readonly int _stringsData, _root;
    private int _nodes;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public PsbScenarioReader(byte[] input)
    {
        _original = input;
        if (input.Length > MaxBytes) throw new InvalidDataException("剧本资源超过 32 MB。");
        if (input.AsSpan().StartsWith("mdf\0"u8))
        {
            if (input.Length < 10) throw Bad();
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4, 4));
            if (expected is < 44 or > MaxBytes) throw Bad();
            using var source = new MemoryStream(input, 8, input.Length - 8);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            _data = new byte[(int)expected];
            zlib.ReadExactly(_data);
            if (zlib.ReadByte() != -1) throw Bad();
        }
        else _data = input;
        if (_data.Length < 44 || !_data.AsSpan().StartsWith("PSB\0"u8)) throw Bad();
        var version = U(4, 2);
        if (version is < 2 or > 4 || U(6, 2) != 0) throw new InvalidDataException("暂不支持这个 PSB 版本或内部加密方式。");
        var namesAt = U(12, 4);
        _stringsData = U(20, 4); _root = U(36, 4);
        var (bases, next) = Array(namesAt);
        var (parents, _) = Array(next);
        var queue = new Queue<(int Node, byte[] Prefix)>();
        var visited = new HashSet<int>();
        queue.Enqueue((0, []));
        while (queue.TryDequeue(out var item))
        {
            if (item.Prefix.Length > 512 || !visited.Add(item.Node) || visited.Count > 100_000 || item.Node >= bases.Length) throw Bad();
            var first = bases[item.Node];
            for (var symbol = 0; symbol < 256 && (long)first + symbol < parents.Length; symbol++)
            {
                var child = first + symbol;
                if (parents[child] != item.Node) continue;
                if (symbol == 0)
                {
                    if (child >= bases.Length) throw Bad();
                    _names[bases[child]] = Utf8.GetString(item.Prefix);
                }
                else
                {
                    var prefix = new byte[item.Prefix.Length + 1];
                    item.Prefix.CopyTo(prefix, 0); prefix[^1] = (byte)symbol;
                    queue.Enqueue((child, prefix));
                }
            }
        }
        (_strings, _) = Array(U(16, 4));
    }

    private static InvalidDataException Bad() => new("剧本资源结构不完整或超过解析限制。");
    private int U(int offset, int length)
    {
        if (length is < 0 or > 4 || offset < 0 || (long)offset + length > _data.Length) throw Bad();
        uint value = 0;
        for (var i = 0; i < length; i++) value |= (uint)_data[offset + i] << (8 * i);
        if (value > int.MaxValue) throw Bad();
        return (int)value;
    }
    private byte B(int offset) { if ((uint)offset >= _data.Length) throw Bad(); return _data[offset]; }
    private (int[] Values, int End) Array(int offset)
    {
        var countSize = B(offset) - 12;
        if (countSize is < 1 or > 4) throw Bad();
        var count = U(offset + 1, countSize);
        var width = B(offset + 1 + countSize) - 12;
        var start = checked(offset + 2 + countSize);
        if (count > 500_000 || width is < 1 or > 4 || (long)start + (long)count * width > _data.Length) throw Bad();
        var values = new int[count];
        for (var i = 0; i < count; i++) values[i] = U(start + i * width, width);
        return (values, start + count * width);
    }
    private object? Read(int offset, int depth = 0)
    {
        if (depth > 96 || ++_nodes > 500_000) throw Bad();
        if (_objects.TryGetValue(offset, out var cached)) return cached;
        if (!_reading.Add(offset)) throw Bad();
        try
        {
            var tag = B(offset);
            object? result;
            if (tag == 1) result = null;
            else if (tag is 2 or 3) result = tag == 2;
            else if (tag is >= 4 and <= 12)
            {
                var size = tag - 4;
                if ((long)offset + 1 + size > _data.Length) throw Bad();
                long value = 0;
                for (var i = 0; i < size; i++) value |= (long)_data[offset + 1 + i] << (i * 8);
                result = value;
            }
            else if (tag is >= 0x15 and <= 0x18)
            {
                var index = U(offset + 1, tag - 0x14);
                if ((uint)index >= _strings.Length) throw Bad();
                var start = checked(_stringsData + _strings[index]);
                if ((uint)start >= _data.Length) throw Bad();
                var end = System.Array.IndexOf(_data, (byte)0, start);
                if (end < start || end - start > 262144) throw Bad();
                result = Utf8.GetString(_data, start, end - start);
            }
            else if (tag == 0x20)
            {
                var (positions, end) = Array(offset + 1);
                result = positions.Select(p => Read(checked(end + p), depth + 1)).ToList();
            }
            else if (tag == 0x21)
            {
                var (keys, end) = Array(offset + 1);
                var (values, body) = Array(end);
                if (keys.Length != values.Length) throw Bad();
                var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < keys.Length; i++)
                {
                    if (!_names.TryGetValue(keys[i], out var name) || dictionary.ContainsKey(name)) throw Bad();
                    dictionary.Add(name, Read(checked(body + values[i]), depth + 1));
                }
                result = dictionary;
            }
            else if (tag == 0x1d) result = 0d;
            else if (tag is 0x1e or 0x1f)
            {
                var size = tag == 0x1e ? 4 : 8;
                if ((long)offset + 1 + size > _data.Length) throw Bad();
                result = tag == 0x1e ? (double)BitConverter.ToSingle(_data, offset + 1) : BitConverter.ToDouble(_data, offset + 1);
            }
            else if (tag is >= 0x19 and <= 0x1c || tag is >= 0x22 and <= 0x25) result = null;
            else throw Bad();
            _objects[offset] = result;
            return result;
        }
        finally { _reading.Remove(offset); }
    }

    public (List<ScriptSegment> Segments, int Scenes) Extract(string resourceId)
    {
        if (Read(_root) is not Dictionary<string, object?> root || root.GetValueOrDefault("scenes") is not List<object?> scenes) return ([], 0);
        var filename = root.GetValueOrDefault("name") as string ?? resourceId;
        if (filename.Length > 512) throw Bad();
        var segments = new List<ScriptSegment>();
        var sceneCount = 0;
        for (var si = 0; si < scenes.Count; si++)
        {
            if (scenes[si] is not Dictionary<string, object?> scene) continue;
            sceneCount++;
            if (scene.GetValueOrDefault("texts") is not List<object?> texts) continue;
            var label = scene.GetValueOrDefault("label") as string ?? $"场景 {si + 1}";
            if (label.Length > 512) throw Bad();
            for (var ti = 0; ti < texts.Count; ti++)
            {
                if (texts[ti] is not List<object?> text || text.Count < 2 || text[1] is not List<object?> pieces) continue;
                var speaker = text[0] as string ?? "";
                if (speaker.Length > 256) throw Bad();
                for (var pi = 0; pi < pieces.Count; pi++)
                {
                    if (pieces[pi] is not List<object?> piece || piece.Count < 2 || piece[1] is not string source || string.IsNullOrWhiteSpace(source)) continue;
                    if (source.Length > 4096) throw new InvalidDataException("剧本包含超过 4096 字符的单条对白，请拆分后再翻译。");
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceId}:{si}:{ti}:{pi}:{source}"))).ToLowerInvariant();
                    segments.Add(new() { Id = id, File = filename, Scene = label, Index = ti + 1, Speaker = speaker,
                        Source = source, Status = source.Any(char.IsLetter) ? "pending" : "skipped" });
                    if (segments.Count > 100_000) throw Bad();
                }
            }
        }
        return (segments, sceneCount);
    }
}
