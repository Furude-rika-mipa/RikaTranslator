using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

public sealed partial class EngineService
{
    private const string NwViewerAdapter = "package.nw/data/scripts/RikaTranslator.js";

    private static bool DetectNwjs(DetectionResult result, string root)
    {
        if (!File.Exists(Path.Combine(root, "nw.dll")) ||
            !File.Exists(Path.Combine(root, "package.nw", "package.json"))) return false;
        result.Directory = root;
        result.Engine = "NW.js";
        result.Backend = "JavaScript";
        try { result.EngineVersion = FileVersionInfo.GetVersionInfo(Path.Combine(root, "nw.dll")).ProductVersion ?? ""; }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception) { }
        var entry = NwViewerEntry(root);
        result.Capability = entry == null ? "detection-only" : "experimental-embedded";
        result.DetectionNotes = entry == null
            ? "发现 NW.js 与 package.nw；当前网页布局没有经过适配。"
            : "识别为 Flower Knight Girl Viewer；场景对白实时翻译，菜单与设置提供本地中文译名。";
        result.Warnings.Add(entry == null
            ? "NW.js 是网页运行环境；本版仅开放已核对布局的 Flower Knight Girl Viewer，其他网页游戏需单独验证。"
            : "支持场景对白实时翻译，以及菜单、按钮、设置、搜索标签和操作说明的本地中文显示。角色名、剧情选项和图片文字未接入。原始剧情脚本保持原样。");
        return true;
    }

    private static string? NwViewerEntry(string root)
    {
        try
        {
            var manifest = SafeTarget(root, "package.nw/package.json");
            using var package = JsonDocument.Parse(ReadFileBounded(manifest, 64 * 1024));
            if (!package.RootElement.TryGetProperty("name", out var name) ||
                name.GetString() != "flower_knight_girl_viewer" ||
                !package.RootElement.TryGetProperty("main", out var main) || main.ValueKind != JsonValueKind.String) return null;
            var filename = main.GetString()!;
            if (filename.IndexOfAny(['/', '\\', ':']) >= 0) return null;
            var relative = "package.nw/" + filename;
            if (!relative.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return null;
            var html = Encoding.UTF8.GetString(ReadFileBounded(SafeTarget(root, relative), 1024 * 1024));
            var scene = Encoding.UTF8.GetString(ReadFileBounded(SafeTarget(root, "package.nw/data/scripts/scene.js"), 2 * 1024 * 1024));
            if (!html.Contains("data/scripts/scene.js", StringComparison.Ordinal) ||
                !Regex.IsMatch(html, @"</head\s*>", RegexOptions.IgnoreCase) ||
                !scene.Contains("function addRPGXText(", StringComparison.Ordinal) ||
                !scene.Contains("scene.elements.textBoxText", StringComparison.Ordinal)) return null;
            return relative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    private byte[] NwViewerScript(GameRecord game, string endpoint, string token)
    {
        const string marker = "/*__RIKA_NW_SETTINGS__*/ null";
        var script = Encoding.UTF8.GetString(Asset("nwjs/ViewerTranslator.js"));
        if (script.Split(marker).Length != 2) throw new InvalidOperationException("NW.js 适配资源不完整。");
        var config = JsonSerializer.Serialize(new { endpoint, gameId = game.Id, token, uiLanguage = game.TargetLanguage ?? "zh-CN" }, Json);
        var ui = Encoding.UTF8.GetString(Asset("nwjs/ViewerUi.js"));
        return Encoding.UTF8.GetBytes(ui + "\n" + script.Replace(marker, config, StringComparison.Ordinal));
    }

    private static byte[] NwViewerHtml(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("data/scripts/RikaTranslator.js", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("发现已有 Rika 网页适配脚本，请先通过原安装清单卸载。");
        var closing = Regex.Match(text, @"</head\s*>", RegexOptions.IgnoreCase);
        if (!closing.Success) throw new InvalidOperationException("Viewer 页面结构已改变，未修改页面。");
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return Encoding.UTF8.GetBytes(text.Insert(closing.Index,
            "    <script src=\"data/scripts/RikaTranslator.js\"></script>" + newline));
    }
}
