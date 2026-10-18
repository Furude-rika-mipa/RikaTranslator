using System.Diagnostics;

namespace GameTranslateToolkit.FontMaking;

public static class TmpFontMakerApi
{
    public static void Map(WebApplication app, TmpFontMakerService service)
    {
        app.MapGet("/api/font-maker/templates", service.Templates);
        app.MapGet("/api/font-maker/status", service.Status);
        app.MapGet("/api/font-maker/results", service.Results);
        app.MapPost("/api/font-maker/pick", async () => new { path = await ToolkitWindow.PickFontAsync(false) });
        app.MapPost("/api/font-maker/start", (TmpFontMakeRequest request) => service.Start(request));
        app.MapPost("/api/font-maker/cancel", service.Cancel);
        app.MapGet("/api/font-maker/{id}/download", (string id) => Results.File(service.OpenBundle(id), "application/octet-stream", service.Result(id).FileName));
        app.MapGet("/api/font-maker/{id}/selection", (string id) => new { path = service.VerifiedBundlePath(id), fileName = service.Result(id).FileName });
        app.MapPost("/api/font-maker/open-folder", () =>
        {
            Process.Start(new ProcessStartInfo(service.OutputDirectory()) { UseShellExecute = true });
            return new { opened = true };
        });
    }
}
