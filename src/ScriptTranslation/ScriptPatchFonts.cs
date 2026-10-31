using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit.ScriptTranslation;

public sealed record ScriptPatchFontRequest(string SourcePath);
public sealed record ScriptPatchFont(string Id, string Family, string FileName, long Bytes);

public sealed partial class ScriptTranslationService
{
    private static string PatchFontFamily(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 31 || value.Any(c => char.IsControl(c) || ";=[]\"".Contains(c)))
            throw new InvalidDataException("字体族名称无效或超过 Windows 的 31 字符限制，请选择其他字体。");
        return value;
    }
    // Managed, bounded OpenType name-table reader; selecting a file does not execute a native font parser.
    // Layout: https://learn.microsoft.com/en-us/typography/opentype/spec/name
    private static string PatchFontFamily(byte[] bytes)
    {
        int U16(int at) { if (at < 0 || at > bytes.Length - 2) throw new InvalidDataException("字体名称表无效。"); return BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at)); }
        int U32(int at) { if (at < 0 || at > bytes.Length - 4) throw new InvalidDataException("字体名称表无效。"); return checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at))); }
        var names = new List<(int Rank, string Name)>();
        for (var i = 0; i < U16(4); i++)
        {
            var table = 12 + i * 16;
            if (!bytes.AsSpan(table, 4).SequenceEqual("name"u8)) continue;
            var start = U32(table + 8); var length = U32(table + 12);
            if (length < 6 || (long)start + length > bytes.Length || U16(start) > 1) throw new InvalidDataException("字体名称表无效。");
            var count = U16(start + 2); var storage = U16(start + 4);
            if (count > 4096 || 6L + count * 12 > length || storage < 6 + count * 12 || storage > length) throw new InvalidDataException("字体名称记录无效。");
            for (var n = 0; n < count; n++)
            {
                var row = start + 6 + n * 12; var platform = U16(row); var encoding = U16(row + 2); var language = U16(row + 4);
                if (U16(row + 6) != 1 || !(platform == 0 || platform == 3 && encoding is 0 or 1 or 10)) continue;
                var size = U16(row + 8); var offset = U16(row + 10);
                if (size is < 2 or > 2048 || size % 2 != 0 || (long)storage + offset + size > length) throw new InvalidDataException("字体名称字符串越界。");
                var name = new UnicodeEncoding(true, false, true).GetString(bytes, start + storage + offset, size);
                if (name.Length is < 1 or > 31 || name.Any(c => char.IsControl(c) || ";=[]\"".Contains(c))) continue;
                names.Add((platform == 3 && language == 0x409 ? 0 : platform == 3 && language == 0x804 ? 1 : 2, name));
            }
        }
        return names.Count == 0 ? throw new InvalidDataException("没有找到可用于游戏的字体族名称，请选择标准 TTF / OTF 字体。") : PatchFontFamily(names.OrderBy(n => n.Rank).First().Name);
    }
    private string PatchFontPath(string id, string fontId)
    {
        if (!Regex.IsMatch(fontId, "\\A[a-f0-9]{64}\\.(ttf|otf)\\z", RegexOptions.CultureInvariant)) throw new ArgumentException("无效的补丁字体编号。");
        var path = Path.Combine(Folder(id), "patch-fonts", fontId); UpdatePackage.RejectLinks(path); return path;
    }
    public ScriptPatchFont ImportScriptPatchFont(string id, ScriptPatchFontRequest request)
    {
        _begin(); try
        {
            lock (_gate)
            {
                _ = Job(id);
                var source = Path.GetFullPath(request.SourcePath ?? "");
                var bytes = FontAssets.ReadSourceFont(source); var family = PatchFontFamily(bytes);
                var fontId = PatchHash(bytes) + Path.GetExtension(source).ToLowerInvariant(); var target = PatchFontPath(id, fontId);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target))
                {
                    var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, target, false); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                if (PatchHash(PatchReadFile(target, 64L * 1024 * 1024)) != fontId[..64]) throw new InvalidDataException("已保存字体校验失败，请重新选择字体文件。");
                return new(fontId, family, Path.GetFileName(source), bytes.Length);
            }
        }
        finally { _end(); }
    }
}
