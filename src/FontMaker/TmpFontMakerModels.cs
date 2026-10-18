using System.Text;

namespace GameTranslateToolkit.FontMaking;

public sealed class TmpFontMakeRequest
{
    public string SourcePath { get; set; } = "";
    public string Profile { get; set; } = "2021";
    public string CharacterSet { get; set; } = "common";
    public string CustomCharacters { get; set; } = "";
    public int SamplingSize { get; set; } = 48;
    public int AtlasSize { get; set; } = 4096;
    public int Padding { get; set; } = 5;
}

public sealed class TmpFontTemplate
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public string TmpVersion { get; set; } = "";
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class TmpFontJob
{
    public string Id { get; set; } = "";
    public string Profile { get; set; } = "";
    public string CharacterSet { get; set; } = "";
    public string CustomCharacters { get; set; } = "";
    public string SourceFile { get; set; } = "source.ttf";
    public int SamplingSize { get; set; }
    public int AtlasSize { get; set; }
    public int Padding { get; set; }
}

public sealed class TmpFontMakeProgress
{
    public string Stage { get; set; } = "idle";
    public string Message { get; set; } = "选择字体文件，制作 TMP 字体包。";
    public int Current { get; set; }
    public int Total { get; set; }
}

public sealed class TmpFontMakeResult
{
    public string Id { get; set; } = "";
    public string FontName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Profile { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public string TmpVersion { get; set; } = "";
    public int RequestedCharacters { get; set; }
    public int Characters { get; set; }
    public int Glyphs { get; set; }
    public int MissingCount { get; set; }
    public string MissingSample { get; set; } = "";
    public int AtlasCount { get; set; }
    public int AtlasSize { get; set; }
    public int SamplingSize { get; set; }
    public int Padding { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long ElapsedMs { get; set; }
    public TmpFontReport? Inspection { get; set; }
    public string Note { get; set; } = "静态 SDF 字体包；只包含已生成的字符。游戏中的材质、排版和跨版本兼容仍需实测。";
}

public static class TmpFontCharacterSets
{
    public static uint[] Resolve(string preset, string custom)
    {
        if (preset is not ("common" or "cjk" or "custom")) throw new ArgumentException("请选择有效的字符集。");
        if (custom.Length > 65536) throw new ArgumentException("自定义字符最多 65536 个 UTF-16 字符。");
        var codes = new HashSet<uint>();
        void Range(uint start, uint end)
        {
            for (var c = start; c <= end; c++)
                if (Rune.GetUnicodeCategory(new Rune(c)) is not (System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)) codes.Add(c);
        }
        Range(0x20, 0x7e);
        if (preset != "custom")
        {
            Range(0x2000, 0x206f); Range(0x3000, 0x303f); Range(0x3040, 0x30ff); Range(0xff00, 0xffef);
            if (preset == "cjk") Range(0x4e00, 0x9fff);
            else
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var gb = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                for (var hi = 0xb0; hi <= 0xf7; hi++)
                    for (var lo = 0xa1; lo <= 0xfe; lo++)
                        try
                        {
                            var text = gb.GetString([(byte)hi, (byte)lo]);
                            foreach (var r in text.EnumerateRunes()) if (r.Value is >= 0x4e00 and <= 0x9fff) codes.Add((uint)r.Value);
                        }
                        catch (DecoderFallbackException) { }
            }
        }
        foreach (var rune in custom.EnumerateRunes()) if (!Rune.IsControl(rune)) codes.Add((uint)rune.Value);
        if (preset == "custom" && string.IsNullOrWhiteSpace(custom)) throw new ArgumentException("请输入需要制作的自定义字符。");
        if (codes.Count > 30000) throw new ArgumentException("一次最多生成 30000 个字符，请缩小字符集。");
        return codes.Order().ToArray();
    }
}

public sealed class TmpGlyphRaster
{
    public uint Index;
    public float Width, Height, BearingX, BearingY, Advance;
    public int BitmapWidth, BitmapHeight, X, Y, Atlas;
    public byte[] Bitmap = [];
}

public sealed class TmpRasterFont
{
    public string Name = "Rika Font";
    public float Ascent, Descent, LineHeight;
    public int SamplingSize, Padding, AtlasSize;
    public List<TmpGlyphRaster> Glyphs = [];
    public Dictionary<uint, uint> Characters = [];
    public List<uint> Missing = [];
    public List<byte[]> Atlases = [];
}
