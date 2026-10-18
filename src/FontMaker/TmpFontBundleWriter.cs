using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace GameTranslateToolkit.FontMaking;

/// <summary>Replaces font tables and every atlas, retaining the template's original Unity/TMP schema.</summary>
public static class TmpFontBundleWriter
{
    public static void Write(string templatePath, string destination, TmpRasterFont font, string bundleName)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(templatePath, true);
            AssetsFileInstance? file = null;
            AssetFileInfo? fontInfo = null;
            AssetTypeValueField? field = null;
            var directoryIndex = -1;
            for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                if (!bundle.file.IsAssetsFile(i)) continue;
                var candidate = manager.LoadAssetsFileFromBundle(bundle, i, false);
                foreach (var asset in candidate.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    var value = Read(manager, candidate, asset);
                    if (value["m_GlyphTable.Array"].IsDummy || value["m_CharacterTable.Array"].IsDummy) continue;
                    if (field != null) throw new InvalidDataException("字体模板包含多个 TMP 字体，无法确定目标。");
                    file = candidate; field = value; fontInfo = asset; directoryIndex = i;
                }
            }
            if (file == null || fontInfo == null || field == null) throw new InvalidDataException("模板中没有支持的 TMP 字体结构。");
            if (font.Atlases.Count is < 1 or > 4) throw new InvalidDataException("字体图集页数无效。");

            var glyphArray = field["m_GlyphTable.Array"];
            var glyphPrototype = Element(glyphArray);
            glyphArray.Children = font.Glyphs.Select(g =>
            {
                var e = ValueBuilder.DefaultValueFieldFromTemplate(glyphPrototype);
                e["m_Index"].AsUInt = g.Index;
                var m = e["m_Metrics"];
                m["m_Width"].AsFloat = g.Width; m["m_Height"].AsFloat = g.Height;
                m["m_HorizontalBearingX"].AsFloat = g.BearingX; m["m_HorizontalBearingY"].AsFloat = g.BearingY;
                m["m_HorizontalAdvance"].AsFloat = g.Advance;
                var rect = e["m_GlyphRect"];
                var visible = g.BitmapWidth > 0 && g.BitmapHeight > 0;
                rect["m_X"].AsInt = visible ? g.X + font.Padding : 0;
                rect["m_Y"].AsInt = visible ? g.Y + font.Padding : 0;
                rect["m_Width"].AsInt = visible ? g.BitmapWidth - font.Padding * 2 : 0;
                rect["m_Height"].AsInt = visible ? g.BitmapHeight - font.Padding * 2 : 0;
                e["m_Scale"].AsFloat = 1; e["m_AtlasIndex"].AsInt = g.Atlas;
                return e;
            }).ToList();
            var characterArray = field["m_CharacterTable.Array"];
            var characterPrototype = Element(characterArray);
            characterArray.Children = font.Characters.OrderBy(x => x.Key).Select(pair =>
            {
                var e = ValueBuilder.DefaultValueFieldFromTemplate(characterPrototype);
                e["m_ElementType"].AsInt = 1; e["m_Unicode"].AsUInt = pair.Key;
                e["m_GlyphIndex"].AsUInt = pair.Value; e["m_Scale"].AsFloat = 1;
                return e;
            }).ToList();

            SetString(field, "m_Name", font.Name);
            SetInt(field, "hashCode", 0); SetInt(field, "materialHashCode", 0);
            SetInt(field, "m_AtlasPopulationMode", 0);
            SetInt(field, "m_AtlasTextureIndex", font.Atlases.Count - 1);
            SetInt(field, "m_AtlasWidth", font.AtlasSize); SetInt(field, "m_AtlasHeight", font.AtlasSize);
            SetInt(field, "m_AtlasPadding", font.Padding); SetInt(field, "m_AtlasRenderMode", 4165);
            SetBool(field, "m_IsMultiAtlasTexturesEnabled", font.Atlases.Count > 1);
            SetBool(field, "m_ClearDynamicDataOnBuild", false); SetBool(field, "InternalDynamicOS", false);
            SetBool(field, "m_GetFontFeatures", false); SetBool(field, "m_ShouldReimportFontFeatures", false);
            SetString(field, "m_SourceFontFileGUID", ""); SetString(field, "m_SourceFontFilePath", "");
            ClearPointer(field["m_SourceFontFile"]);
            foreach (var name in new[] { "m_glyphInfoList.Array", "m_UsedGlyphRects.Array", "m_FreeGlyphRects.Array", "m_FallbackFontAssetTable.Array", "fallbackFontAssets.Array", "m_KerningTable.kerningPairs.Array", "m_FontFeatureTable.m_GlyphPairAdjustmentRecords.Array" })
                if (!field[name].IsDummy) field[name].Children = [];
            ClearArrays(field["m_FontFeatureTable"]);
            foreach (var name in new[] { "m_FontWeightTable.Array", "fontWeights.Array" })
                if (!field[name].IsDummy)
                    foreach (var weight in field[name].Children)
                        foreach (var pointer in weight.Children) if (!pointer["m_PathID"].IsDummy) ClearPointer(pointer);
            var face = field["m_FaceInfo"];
            SetString(face, "m_FamilyName", font.Name); SetString(face, "m_StyleName", "Regular");
            SetInt(face, "m_PointSize", font.SamplingSize); SetFloat(face, "m_Scale", 1);
            SetFloat(face, "m_LineHeight", font.LineHeight); SetFloat(face, "m_AscentLine", font.Ascent);
            SetFloat(face, "m_DescentLine", font.Descent); SetFloat(face, "m_Baseline", 0);
            SetFloat(face, "m_CapLine", font.Ascent * .8f); SetFloat(face, "m_MeanLine", font.Ascent * .5f);
            SetFloat(face, "m_UnderlineOffset", font.Descent * .5f); SetFloat(face, "m_StrikethroughOffset", font.Ascent * .4f);
            SetFloat(face, "m_UnderlineThickness", 1); SetFloat(face, "m_StrikethroughThickness", 1);
            SetFloat(face, "m_TabWidth", font.Glyphs.FirstOrDefault(g => font.Characters.GetValueOrDefault(32u) == g.Index)?.Advance ?? font.SamplingSize * .5f);
            var creation = field["m_CreationSettings"];
            foreach (var name in new[] { "sourceFontFileName", "sourceFontFileGUID", "referencedFontAssetGUID", "referencedTextAssetGUID", "characterSequence" }) SetString(creation, name, "");
            SetString(field["m_fontInfo"], "Name", font.Name);

            var atlasArray = field["m_AtlasTextures.Array"];
            var ptrPrototype = Element(atlasArray);
            if (atlasArray.Children.Count == 0) throw new InvalidDataException("模板缺少字体图集引用。");
            var originalIds = atlasArray.Children.Select(x => x["m_PathID"].AsLong).ToArray();
            var textureInfo = file.file.GetAssetInfo(originalIds[0]);
            if (textureInfo.TypeId != (int)AssetClassID.Texture2D) throw new InvalidDataException("模板图集引用无效。");
            var textureTemplate = Read(manager, file, textureInfo).TemplateField;
            var newPointers = new List<AssetTypeValueField>();
            for (var page = 0; page < font.Atlases.Count; page++)
            {
                var texture = page < originalIds.Length ? file.file.GetAssetInfo(originalIds[page]) :
                    AssetFileInfo.Create(file.file, checked(file.file.AssetInfos.Max(x => x.PathId) + 1), (int)AssetClassID.Texture2D, null);
                if (page >= originalIds.Length) file.file.Metadata.AssetInfos.Add(texture);
                texture.SetNewData(Texture(textureTemplate, font.Atlases[page], font.AtlasSize, font.Name + " Atlas " + page));
                var pointer = ValueBuilder.DefaultValueFieldFromTemplate(ptrPrototype);
                pointer["m_FileID"].AsInt = 0; pointer["m_PathID"].AsLong = texture.PathId; newPointers.Add(pointer);
            }
            // Unused template pages must not retain the original font's pixels.
            foreach (var info in file.file.GetAssetsOfType(AssetClassID.Texture2D).ToArray())
                if (!newPointers.Any(x => x["m_PathID"].AsLong == info.PathId)) info.SetNewData(Texture(textureTemplate, [0], 1, "Unused atlas"));
            atlasArray.Children = newPointers;
            if (!field["atlas"].IsDummy) field["atlas"].Children = newPointers[0].Children;

            var materialPointer = field["material"].IsDummy ? field["m_Material"] : field["material"];
            if (materialPointer.IsDummy || materialPointer["m_FileID"].AsInt != 0) throw new InvalidDataException("模板缺少内嵌字体材质。");
            var materialInfo = file.file.GetAssetInfo(materialPointer["m_PathID"].AsLong);
            var material = Read(manager, file, materialInfo);
            SetString(material, "m_Name", font.Name + " Material");
            foreach (var pair in material["m_SavedProperties.m_TexEnvs.Array"].Children)
                if (Key(pair["first"]) == "_MainTex")
                { pair["second.m_Texture.m_FileID"].AsInt = 0; pair["second.m_Texture.m_PathID"].AsLong = newPointers[0]["m_PathID"].AsLong; }
            var floats = material["m_SavedProperties.m_Floats.Array"];
            foreach (var (key, value) in new[] { ("_GradientScale", (float)font.Padding + 1), ("_TextureWidth", (float)font.AtlasSize), ("_TextureHeight", (float)font.AtlasSize), ("_WeightNormal", 0f), ("_WeightBold", .75f) })
            {
                var pair = floats.Children.FirstOrDefault(x => Key(x["first"]) == key);
                if (pair == null)
                {
                    pair = ValueBuilder.DefaultValueFieldFromTemplate(Element(floats));
                    if (pair["first.name"].IsDummy) pair["first"].AsString = key; else pair["first.name"].AsString = key;
                    floats.Children.Add(pair);
                }
                pair["second"].AsFloat = value;
            }
            materialInfo.SetNewData(material);
            fontInfo.SetNewData(field);
            foreach (var info in file.file.GetAssetsOfType(AssetClassID.AssetBundle))
            {
                var value = Read(manager, file, info); SetString(value, "m_Name", bundleName); SetString(value, "m_AssetBundleName", bundleName);
                foreach (var pair in value["m_Container.Array"].Children)
                    if (Key(pair["first"]).EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    { if (pair["first.name"].IsDummy) pair["first"].AsString = "assets/rika-font.asset"; else pair["first.name"].AsString = "assets/rika-font.asset"; }
                info.SetNewData(value);
            }
            var directory = bundle.file.BlockAndDirInfo.DirectoryInfos[directoryIndex];
            directory.Name = "CAB-" + Guid.NewGuid().ToString("N");
            directory.SetNewData(file.file);
            using var unpacked = new MemoryStream();
            bundle.file.Write(new AssetsFileWriter(unpacked));
            unpacked.Position = 0;
            var packed = new AssetBundleFile(); packed.Read(new AssetsFileReader(unpacked));
            try { using var output = new AssetsFileWriter(destination); packed.Pack(output, AssetBundleCompressionType.LZ4); }
            finally { packed.Close(); }
        }
        finally { manager.UnloadAll(); }
    }

    private static AssetTypeValueField Texture(AssetTypeTemplateField template, byte[] bytes, int size, string name)
    {
        var field = ValueBuilder.DefaultValueFieldFromTemplate(template);
        SetString(field, "m_Name", name); SetInt(field, "m_Width", size); SetInt(field, "m_Height", size);
        SetInt(field, "m_CompleteImageSize", bytes.Length); SetInt(field, "m_TextureFormat", 1);
        SetInt(field, "m_MipCount", 1); SetInt(field, "m_ImageCount", 1); SetInt(field, "m_TextureDimension", 2);
        SetBool(field, "m_IsReadable", true); SetBool(field, "m_MipMap", false); SetBool(field, "m_StreamingMipmaps", false);
        SetInt(field, "m_TextureSettings.m_FilterMode", 1); SetInt(field, "m_TextureSettings.m_Aniso", 1);
        foreach (var n in new[] { "m_TextureSettings.m_WrapMode", "m_TextureSettings.m_WrapU", "m_TextureSettings.m_WrapV", "m_TextureSettings.m_WrapW" }) SetInt(field, n, 1);
        var pixels = field["image data"]; if (pixels.IsDummy) throw new InvalidDataException("模板纹理缺少像素字段。");
        var pixelArray = pixels.TemplateField.IsArray ? pixels : pixels["Array"];
        if (pixelArray.IsDummy) throw new InvalidDataException("模板纹理像素数组无效。");
        pixelArray.Value = new AssetTypeValue(AssetValueType.ByteArray, bytes);
        SetString(field, "m_StreamData.path", "");
        return field;
    }

    public static AssetTypeValueField Read(AssetsManager manager, AssetsFileInstance file, AssetFileInfo info)
    {
        var template = manager.GetTemplateBaseField(file, info).Clone(); Compact(template);
        return template.MakeValue(file.file.Reader, info.GetAbsoluteByteOffset(file.file), manager.GetRefTypeManager(file));
    }
    private static void Compact(AssetTypeTemplateField field)
    {
        if (field.IsArray && field.Children.Count == 2 && field.Children[1].ValueType is AssetValueType.UInt8 or AssetValueType.Int8)
            field.ValueType = AssetValueType.ByteArray;
        foreach (var child in field.Children) Compact(child);
    }
    private static AssetTypeTemplateField Element(AssetTypeValueField array)
        => array.IsDummy || array.TemplateField.Children.Count != 2 ? throw new InvalidDataException("模板数组类型无效。") : array.TemplateField.Children[1];
    private static string Key(AssetTypeValueField field) => field["name"].IsDummy ? field.AsString : field["name"].AsString;
    private static void ClearArrays(AssetTypeValueField field)
    {
        if (field.IsDummy) return;
        if (field.TemplateField.IsArray) { field.Children = []; return; }
        foreach (var child in field.Children) ClearArrays(child);
    }
    private static void ClearPointer(AssetTypeValueField field) { if (!field["m_PathID"].IsDummy) { field["m_FileID"].AsInt = 0; field["m_PathID"].AsLong = 0; } }
    private static void SetInt(AssetTypeValueField field, string name, int value) { if (!field[name].IsDummy) field[name].AsInt = value; }
    private static void SetFloat(AssetTypeValueField field, string name, float value) { if (!field[name].IsDummy) field[name].AsFloat = value; }
    private static void SetBool(AssetTypeValueField field, string name, bool value) { if (!field[name].IsDummy) field[name].AsBool = value; }
    private static void SetString(AssetTypeValueField field, string name, string value) { if (!field[name].IsDummy) field[name].AsString = value; }
}
