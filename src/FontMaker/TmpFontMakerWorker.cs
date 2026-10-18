using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeTypeSharp;
using GameTranslateToolkit.Updates;
using static FreeTypeSharp.FT;

namespace GameTranslateToolkit.FontMaking;

/// <summary>Invoked before opening personal settings; native font parsing runs only in this worker.</summary>
public static class TmpFontMakerWorker
{
    public static List<TmpFontTemplate> Templates(string applicationDirectory)
    {
        var root = Path.Combine(applicationDirectory, "adapters", "font-maker");
        var path = Path.Combine(root, "templates.json"); UpdatePackage.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 32768) throw new InvalidDataException("字体制作模板缺失，请检查程序文件。");
        return JsonSerializer.Deserialize<List<TmpFontTemplate>>(File.ReadAllText(path), UpdatePackage.Json) ?? [];
    }
    public static string TemplatePath(string applicationDirectory, string profile)
    {
        var template = Templates(applicationDirectory).SingleOrDefault(x => x.Id == profile) ?? throw new ArgumentException("请选择支持的 Unity 模板版本。");
        if (template.File != Path.GetFileName(template.File) || !template.File.EndsWith(".bundle", StringComparison.Ordinal)) throw new InvalidDataException("字体模板清单无效。");
        var path = Path.Combine(applicationDirectory, "adapters", "font-maker", template.File); UpdatePackage.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length is < 32 or > 2 * 1024 * 1024 ||
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(template.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("字体制作模板校验失败，请恢复完整的程序文件。");
        return path;
    }
    public static void Validate(TmpFontMakeRequest request, string applicationDirectory)
    {
        if (request.SamplingSize is < 24 or > 96) throw new ArgumentException("字体采样大小应为 24–96。");
        if (request.AtlasSize is not (1024 or 2048 or 4096)) throw new ArgumentException("图集大小应为 1024、2048 或 4096。");
        if (request.Padding is < 2 or > 12) throw new ArgumentException("字形留白应为 2–12。");
        _ = TmpFontCharacterSets.Resolve(request.CharacterSet, request.CustomCharacters);
        _ = TemplatePath(applicationDirectory, request.Profile);
    }
    public static void Run(string jobPath, string applicationDirectory)
    {
        UpdatePackage.RejectLinks(jobPath);
        if (new FileInfo(jobPath).Length > 512 * 1024) throw new InvalidDataException("字体制作任务过大。");
        var job = JsonSerializer.Deserialize<TmpFontJob>(File.ReadAllText(jobPath), UpdatePackage.Json) ?? throw new InvalidDataException("字体制作任务无效。");
        if (!Guid.TryParseExact(job.Id, "N", out _) || job.SourceFile != "source.ttf") throw new InvalidDataException("字体制作任务标识无效。");
        var folder = Path.GetDirectoryName(Path.GetFullPath(jobPath))!;
        var request = new TmpFontMakeRequest { Profile = job.Profile, CharacterSet = job.CharacterSet, CustomCharacters = job.CustomCharacters, SamplingSize = job.SamplingSize, AtlasSize = job.AtlasSize, Padding = job.Padding };
        Validate(request, applicationDirectory);
        var watch = Stopwatch.StartNew();
        void Progress(string stage, int current, int total, string message)
        {
            // Progress is best effort; a viewer holding the old file must not abort a font build.
            try { UpdatePackage.WriteJson(Path.Combine(folder, "progress.json"), new TmpFontMakeProgress { Stage = stage, Current = current, Total = total, Message = message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Progress("rendering", 0, 1, "正在读取字体并生成字形…");
        var source = FontAssets.ReadSourceFont(Path.Combine(folder, job.SourceFile));
        var codes = TmpFontCharacterSets.Resolve(job.CharacterSet, job.CustomCharacters);
        var font = Rasterize(source, codes, job.SamplingSize, job.Padding, (current, total) => Progress("rendering", current, total, $"正在生成字形 {current:N0} / {total:N0}…"));
        font.AtlasSize = job.AtlasSize;
        Progress("packing", 0, 1, "正在排列字形并生成图集…");
        Pack(font);
        Progress("writing", 0, 1, "正在写入 TMP 字体包…");
        var output = Path.Combine(folder, "font.bundle");
        TmpFontBundleWriter.Write(TemplatePath(applicationDirectory, job.Profile), output, font, "rika-font-" + job.Id);
        Progress("checking", 0, 1, "正在检查生成的字体包…");
        var bytes = File.ReadAllBytes(output);
        if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("生成的字体包超过 64 MB，请缩小字符集或采样大小。");
        var inspection = TmpFontInspector.Inspect(bytes);
        if (inspection.Fonts.Count != 1 || inspection.Fonts[0].CharacterCount != font.Characters.Count || inspection.Fonts[0].AtlasCount != font.Atlases.Count || inspection.Fonts[0].Dynamic != false)
            throw new InvalidDataException("生成字体的字符或图集校验未通过。");
        var result = new TmpFontMakeResult
        {
            Id = job.Id, FontName = font.Name, FileName = "RikaFont-Unity" + job.Profile + ".bundle", Profile = job.Profile,
            UnityVersion = inspection.UnityVersion, TmpVersion = inspection.Fonts[0].Version,
            RequestedCharacters = codes.Length, Characters = font.Characters.Count, Glyphs = font.Glyphs.Count,
            MissingCount = font.Missing.Count, MissingSample = string.Concat(font.Missing.Take(100).Select(x => char.ConvertFromUtf32((int)x))),
            AtlasCount = font.Atlases.Count, AtlasSize = font.AtlasSize, SamplingSize = font.SamplingSize, Padding = font.Padding,
            Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ElapsedMs = watch.ElapsedMilliseconds, Inspection = inspection
        };
        UpdatePackage.WriteJson(Path.Combine(folder, "result.json"), result);
        Progress("complete", codes.Length, codes.Length, "字体包已生成并通过文件检查。");
    }

    public static unsafe TmpRasterFont Rasterize(byte[] source, uint[] codes, int sampling, int padding, Action<int, int>? progress = null)
    {
        FT_LibraryRec_* library;
        if (FT_Init_FreeType(&library) != FT_Error.FT_Err_Ok) throw new InvalidDataException("字体引擎初始化失败。");
        try
        {
            fixed (byte* input = source)
            {
                FT_FaceRec_* face;
                if (FT_New_Memory_Face(library, input, source.Length, 0, &face) != FT_Error.FT_Err_Ok) throw new InvalidDataException("无法解析这个字体文件，请选择完整的 TTF/OTF。");
                try
                {
                    if (FT_Set_Pixel_Sizes(face, 0, (uint)sampling) != FT_Error.FT_Err_Ok) throw new InvalidDataException("无法设置字体采样大小。");
                    var name = Marshal.PtrToStringUTF8((IntPtr)face->family_name) ?? "Rika Font";
                    name = string.Concat(name.Where(c => !char.IsControl(c)).Take(120));
                    var result = new TmpRasterFont { Name = string.IsNullOrWhiteSpace(name) ? "Rika Font" : name, SamplingSize = sampling, Padding = padding,
                        Ascent = (int)face->size->metrics.ascender / 64f, Descent = (int)face->size->metrics.descender / 64f, LineHeight = (int)face->size->metrics.height / 64f };
                    var glyphs = new Dictionary<uint, TmpGlyphRaster>();
                    for (var i = 0; i < codes.Length; i++)
                    {
                        var code = codes[i];
                        var index = FT_Get_Char_Index(face, code);
                        if (index == 0) result.Missing.Add(code);
                        else if (glyphs.ContainsKey(index)) result.Characters[code] = index;
                        else if (FT_Load_Glyph(face, index, FT_LOAD.FT_LOAD_NO_BITMAP | FT_LOAD.FT_LOAD_NO_HINTING) != FT_Error.FT_Err_Ok || FT_Render_Glyph(face->glyph, FT_Render_Mode_.FT_RENDER_MODE_NORMAL) != FT_Error.FT_Err_Ok) result.Missing.Add(code);
                        else
                        {
                            ref var bitmap = ref face->glyph->bitmap;
                            ref var metrics = ref face->glyph->metrics;
                            var glyph = new TmpGlyphRaster { Index = index, Width = (int)metrics.width / 64f, Height = (int)metrics.height / 64f,
                                BearingX = (int)metrics.horiBearingX / 64f, BearingY = (int)metrics.horiBearingY / 64f, Advance = (int)metrics.horiAdvance / 64f };
                            var width = checked((int)bitmap.width); var height = checked((int)bitmap.rows);
                            if (width is < 0 or > 512 || height is < 0 or > 512) throw new InvalidDataException("字体字形尺寸异常。");
                            if (width > 0 && height > 0 && bitmap.buffer != null)
                            {
                                if (Math.Abs((long)bitmap.pitch) < width || bitmap.pixel_mode != FT_Pixel_Mode_.FT_PIXEL_MODE_GRAY) throw new InvalidDataException("字体使用了不支持的字形位图格式。");
                                var pixels = new byte[checked(width * height)];
                                for (var y = 0; y < height; y++) new ReadOnlySpan<byte>(bitmap.buffer + y * bitmap.pitch, width).CopyTo(pixels.AsSpan(y * width, width));
                                glyph.Bitmap = DistanceFieldGenerator.GenerateSdf(pixels, width, height, padding, 1, out glyph.BitmapWidth, out glyph.BitmapHeight);
                            }
                            glyphs.Add(index, glyph); result.Characters[code] = index;
                        }
                        if (i % 64 == 0 || i + 1 == codes.Length) progress?.Invoke(i + 1, codes.Length);
                    }
                    result.Glyphs = glyphs.Values.OrderBy(x => x.Index).ToList();
                    if (result.Characters.Count == 0) throw new InvalidDataException("字体不包含所选字符，请调整字符集。");
                    return result;
                }
                finally { FT_Done_Face(face); }
            }
        }
        finally { FT_Done_FreeType(library); }
    }

    public static void Pack(TmpRasterFont font)
    {
        var size = font.AtlasSize;
        var x = 0; var y = 0; var rowHeight = 0; var page = 0;
        font.Atlases = [new byte[checked(size * size)]];
        foreach (var glyph in font.Glyphs.Where(g => g.Bitmap.Length > 0).OrderByDescending(g => g.BitmapHeight).ThenByDescending(g => g.BitmapWidth))
        {
            if (glyph.BitmapWidth > size || glyph.BitmapHeight > size) throw new InvalidDataException("字形超过图集尺寸，请降低采样大小。");
            if (x + glyph.BitmapWidth > size) { x = 0; y += rowHeight; rowHeight = 0; }
            if (y + glyph.BitmapHeight > size)
            {
                if (++page >= 4) throw new InvalidDataException("所选字符需要超过 4 页图集，请增大图集或降低采样大小、缩小字符集。");
                font.Atlases.Add(new byte[checked(size * size)]); x = y = rowHeight = 0;
            }
            glyph.X = x; glyph.Y = size - y - glyph.BitmapHeight; glyph.Atlas = page;
            for (var row = 0; row < glyph.BitmapHeight; row++)
                Buffer.BlockCopy(glyph.Bitmap, row * glyph.BitmapWidth, font.Atlases[page], (size - 1 - y - row) * size + x, glyph.BitmapWidth);
            x += glyph.BitmapWidth; rowHeight = Math.Max(rowHeight, glyph.BitmapHeight); glyph.Bitmap = [];
        }
    }
}
