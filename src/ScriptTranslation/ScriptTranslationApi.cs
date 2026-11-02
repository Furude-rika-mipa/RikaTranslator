using System.Diagnostics;

namespace GameTranslateToolkit.ScriptTranslation;

public static class ScriptTranslationApi
{
    public static void Map(WebApplication app, ScriptTranslationService service, int port)
    {
        app.MapGet("/api/script-translation/mcp/config", () => new { mcpServers = new Dictionary<string, object> {
            ["rika_translator"] = new { command = Path.Combine(Updates.UpdatePackage.ApplicationDirectory, "RikaTranslator.exe"), args = new[] { "--mcp", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
        } });
        app.MapGet("/api/script-translation/mcp/settings", service.McpSettings);
        app.MapPut("/api/script-translation/mcp/settings", service.UpdateMcpSettingsAsync);
        app.MapPost("/api/script-translation/jobs/{id}/external/preview", service.PreviewExternal);
        app.MapPost("/api/script-translation/jobs/{id}/external/prepare", service.PrepareExternal);
        app.MapGet("/api/script-translation/settings", service.Settings);
        app.MapPut("/api/script-translation/settings", service.UpdateSettingsAsync);
        app.MapGet("/api/script-translation/jobs", service.List);
        app.MapGet("/api/script-translation/games/{gameId}", service.Inspect);
        app.MapGet("/api/script-translation/games/{gameId}/compatibility", service.ScriptCompatibility);
        app.MapPost("/api/script-translation/extract", (ScriptExtractionRequest request) => service.Extract(request));
        app.MapGet("/api/script-translation/jobs/{id}", service.Status);
        app.MapGet("/api/script-translation/jobs/{id}/review", service.ReviewScript);
        app.MapGet("/api/script-translation/jobs/{id}/identical-translations", service.FindIdenticalTranslations);
        app.MapGet("/api/script-translation/jobs/{id}/segments/{segmentId}/location", service.LocateScriptLine);
        app.MapGet("/api/script-translation/jobs/{id}/failed-selection", (string id, HttpRequest request) => service.FailedSelection(id,
            request.Query["file"], request.Query["scene"], request.Query["q"]));
        app.MapPut("/api/script-translation/jobs/{id}/segments/{segmentId}/translation", service.EditScriptLine);
        app.MapGet("/api/script-translation/jobs/{id}/segments", (string id, HttpRequest request) => service.Page(id,
            request.Query["file"], request.Query["scene"], request.Query["q"], int.TryParse(request.Query["page"], out var page) ? page : 1,
            request.Query["failedOnly"] == "true"));
        app.MapPost("/api/script-translation/jobs/{id}/translate", (string id, ScriptBatchRequest request) => service.Translate(id, request));
        app.MapPost("/api/script-translation/jobs/{id}/scope/preview", (string id, ScriptScopeRequest request) => service.PreviewScope(id, request));
        app.MapPost("/api/script-translation/jobs/{id}/scope/translate", (string id, ScriptScopeRequest request) => service.TranslateScope(id, request));
        app.MapPost("/api/script-translation/jobs/{id}/cancel", service.Cancel);
        app.MapPost("/api/script-translation/jobs/{id}/patch/preview", service.PreviewScriptPatch);
        app.MapPost("/api/script-translation/jobs/{id}/patch/generate", service.GenerateScriptPatch);
        app.MapGet("/api/script-translation/jobs/{id}/patches", service.ScriptPatches);
        app.MapDelete("/api/script-translation/jobs/{id}/patch/{patchId}", service.DeleteScriptPatch);
        app.MapPost("/api/script-translation/jobs/{id}/patch/font", service.ImportScriptPatchFont);
        app.MapPost("/api/script-translation/jobs/{id}/patch/{patchId}/open-folder", (string id, string patchId) =>
        {
            Process.Start(new ProcessStartInfo(service.ScriptPatchDirectory(id, patchId)) { UseShellExecute = true });
            return new { opened = true };
        });
        app.MapGet("/api/script-translation/jobs/{id}/export/{format}", (string id, string format) => Results.File(service.Export(id, format),
            format == "json" ? "application/json" : "text/plain; charset=utf-8", $"RikaTranslator-script-{id}.{(format == "json" ? "json" : "txt")}"));
        app.MapPost("/api/script-translation/jobs/{id}/open-folder", (string id) =>
        {
            Process.Start(new ProcessStartInfo(service.OutputDirectory(id)) { UseShellExecute = true });
            return new { opened = true };
        });
    }
}
