using System.Text.Json;

namespace GameTranslateToolkit.ScriptTranslation;

public sealed partial class ScriptTranslationService
{
    public object ScriptCompatibility(string gameId)
    {
        var game = Game(gameId);
        string engine = game.Engine; string format = ""; string? detail = null;
        bool recognized = !string.IsNullOrWhiteSpace(engine) && engine != "Unknown";
        bool route = false, candidates = false; int candidateCount = 0;
        try
        {
            engine = MultiEngine(game) ?? game.Engine;
            route = engine is "KiriKiri" or "WillPlus" or "YU-RIS" or "ExHIBIT" or "Escu:de" or "SiglusEngine" or "BGI / Ethornell";
            format = engine switch {
                "KiriKiri" => "32 位 KiriKiri / Hxv4 · PSB 3 场景",
                "WillPlus" => "AdvHD 2.x UTF-16 · Scn.arc",
                "YU-RIS" => "YU-RIS 494 · Scripts.ypf / YSTB",
                "ExHIBIT" => "RLD 3 · rld 剧本",
                "Escu:de" => "ESC-ARC2 · @mess",
                "SiglusEngine" => "Scene.pck · 已适配场景格式",
                "BGI / Ethornell" => "ARC20 / DSC · BurikoCompiledScriptVer1.00",
                _ => "尚无此引擎的剧本提取路线"
            };
            if (route)
            {
                // Inspect only looks for compatible candidates; parsing and write-back validate later.
                var inspect = JsonSerializer.SerializeToElement(Inspect(gameId));
                candidateCount = inspect.TryGetProperty("archives", out var archives) && archives.ValueKind == JsonValueKind.Array ? archives.GetArrayLength() : 0;
                candidates = inspect.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True && candidateCount > 0;
                detail = inspect.TryGetProperty("message", out var message) ? message.GetString() : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            detail = "资源检查未完成：" + _logs.Sanitize(ex.Message);
        }
        ScriptJob[] jobs;
        lock (_gate) jobs = _jobs.Values.Where(j => j.GameId == gameId && j.Segments.Count > 0).ToArray();
        var extracted = jobs.Length > 0;
        var patchGenerated = jobs.Any(j => { try { return ScriptPatches(j.Id).Count > 0; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; } });
        return new {
            engine, game.EngineVersion, game.Architecture, recognized = recognized || !string.IsNullOrWhiteSpace(engine) && engine != "Unknown", route, format,
            candidates, candidateCount, extracted, patchGenerated, detail,
            extractionState = extracted ? "已有提取任务" : !route ? "尚未适配剧本提取" : candidates ? "发现候选资源，提取时验证格式" : "未找到可用候选资源",
            patchState = patchGenerated ? "已有生成记录，游戏内效果仍需验证" : extracted && jobs.Any(SupportsScriptPatch) ? "可尝试生成，写回时验证格式" : route ? "提取后才能判断" : "尚未适配补丁生成",
            note = "引擎识别与候选资源不代表完整兼容；同引擎的不同游戏和版本仍需实际验证。"
        };
    }
}
