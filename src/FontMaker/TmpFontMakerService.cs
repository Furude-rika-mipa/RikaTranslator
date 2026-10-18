using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit.FontMaking;

public sealed class TmpFontMakerService : IDisposable
{
    private readonly string _root, _applicationDirectory;
    private readonly LogService _logs;
    private readonly Action _begin, _end;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _work;
    private string? _jobFolder;
    private bool _disposed;
    private TmpFontMakeProgress _progress = new();
    private TmpFontMakeResult? _result;
    private string? _error;
    public TmpFontMakerService(string dataDirectory, string applicationDirectory, LogService logs, Action? begin = null, Action? end = null)
    {
        _root = Path.Combine(Path.GetFullPath(dataDirectory), "font-maker"); _applicationDirectory = applicationDirectory;
        _logs = logs; _begin = begin ?? (() => { }); _end = end ?? (() => { });
    }
    public object Status()
    {
        lock (_gate)
        {
            if (_work is { IsCompleted: false } && _jobFolder != null)
            {
                try
                {
                    var file = Path.Combine(_jobFolder, "progress.json"); UpdatePackage.RejectLinks(file);
                    if (File.Exists(file) && new FileInfo(file).Length < 8192)
                    {
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        _progress = JsonSerializer.Deserialize<TmpFontMakeProgress>(reader.ReadToEnd(), UpdatePackage.Json) ?? _progress;
                    }
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { } catch (JsonException) { }
            }
            return new { busy = _work is { IsCompleted: false }, progress = _progress, result = _result, error = _error };
        }
    }
    public List<TmpFontTemplate> Templates() => TmpFontMakerWorker.Templates(_applicationDirectory);
    public object Start(TmpFontMakeRequest request)
    {
        TmpFontMakerWorker.Validate(request, _applicationDirectory);
        // Snapshot the original source, so cancellation or worker failure cannot change the user's font.
        var source = FontAssets.ReadSourceFont(request.SourcePath);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_work is { IsCompleted: false }) throw new InvalidOperationException("已有字体正在制作，请等待完成或先取消。");
            _begin();
            var id = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(_root, "jobs", id);
            try
            {
                UpdatePackage.RejectLinks(folder); Directory.CreateDirectory(folder); UpdatePackage.RejectLinks(folder);
                File.WriteAllBytes(Path.Combine(folder, "source.ttf"), source);
                UpdatePackage.WriteJson(Path.Combine(folder, "job.json"), new TmpFontJob { Id = id, Profile = request.Profile, CharacterSet = request.CharacterSet,
                    CustomCharacters = request.CustomCharacters, SamplingSize = request.SamplingSize, AtlasSize = request.AtlasSize, Padding = request.Padding });
                _stop?.Dispose(); _stop = new CancellationTokenSource();
                _jobFolder = folder; _result = null; _error = null;
                _progress = new() { Stage = "starting", Message = "正在启动字体制作…" };
                _work = Task.Run(() => RunAsync(id, folder, _stop.Token));
                _logs.Write("info", "font-maker", "开始制作 TMP 字体包。", details: new { request.Profile, request.CharacterSet, request.SamplingSize, request.AtlasSize });
                return new { started = true, id };
            }
            catch { Cleanup(folder); _end(); throw; }
        }
    }
    public object Cancel()
    {
        lock (_gate) { if (_work is { IsCompleted: false }) _stop?.Cancel(); return new { cancelling = _work is { IsCompleted: false } }; }
    }
    private async Task RunAsync(string id, string folder, CancellationToken ct)
    {
        Process? worker = null;
        try
        {
            var executable = Path.Combine(_applicationDirectory, "RikaTranslator.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _applicationDirectory };
            foreach (var argument in new[] { "--server", "--make-tmp-job", Path.Combine(folder, "job.json"), "--data-dir", Path.Combine(folder, "worker-data") }) start.ArgumentList.Add(argument);
            ct.ThrowIfCancellationRequested();
            worker = Process.Start(start) ?? throw new InvalidOperationException("无法启动字体制作进程。");
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (!worker.HasExited)
            {
                await Task.Delay(150, ct); worker.Refresh();
                if (!worker.HasExited && (DateTime.UtcNow > deadline || worker.WorkingSet64 > 1024L * 1024 * 1024))
                    throw new InvalidOperationException("字体制作超过时间或内存上限，请缩小字符集或图集。");
            }
            ct.ThrowIfCancellationRequested();
            var reportPath = Path.Combine(folder, "result.json"); UpdatePackage.RejectLinks(reportPath);
            if (worker.ExitCode != 0 || !File.Exists(reportPath))
            {
                var diagnosticPath = Path.Combine(folder, "worker-diagnostic.json");
                if (File.Exists(diagnosticPath) && new FileInfo(diagnosticPath).Length < 8192)
                {
                    using var diagnostic = JsonDocument.Parse(File.ReadAllText(diagnosticPath));
                    if (diagnostic.RootElement.TryGetProperty("errorType", out var type))
                        _logs.Write("debug", "font-maker", "字体制作进程返回错误。", details: new { errorType = type.GetString() });
                }
                var errorPath = Path.Combine(folder, "worker-error.json");
                var reason = File.Exists(errorPath) && new FileInfo(errorPath).Length < 8192 ? JsonSerializer.Deserialize<string>(File.ReadAllText(errorPath)) : null;
                throw new InvalidDataException(reason ?? "字体制作进程未能完成，请检查字体文件或减少字符数量。");
            }
            if (new FileInfo(reportPath).Length > 256 * 1024) throw new InvalidDataException("字体制作报告异常。");
            var result = JsonSerializer.Deserialize<TmpFontMakeResult>(File.ReadAllText(reportPath), UpdatePackage.Json) ?? throw new InvalidDataException("字体制作没有结果。");
            if (result.Id != id) throw new InvalidDataException("字体制作结果与任务不匹配。");
            var bundlePath = Path.Combine(folder, "font.bundle"); UpdatePackage.RejectLinks(bundlePath);
            using (var input = File.OpenRead(bundlePath))
                if (input.Length != result.Bytes || input.Length > 64 * 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(input)).Equals(result.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("生成字体的文件校验失败。");
            var saved = ResultDirectory(id); UpdatePackage.RejectLinks(saved); Directory.CreateDirectory(saved);
            try
            {
                ct.ThrowIfCancellationRequested();
                File.Move(bundlePath, Path.Combine(saved, "font.bundle"));
                UpdatePackage.WriteJson(Path.Combine(saved, "result.json"), result);
            }
            catch { Cleanup(saved); throw; }
            lock (_gate) { _result = result; _progress = new() { Stage = "complete", Current = result.RequestedCharacters, Total = result.RequestedCharacters, Message = "字体包已生成并通过文件检查。" }; }
            _logs.Write("info", "font-maker", "TMP 字体包制作完成。", details: new { result.Profile, result.Characters, result.MissingCount, result.AtlasCount });
        }
        catch (OperationCanceledException) { lock (_gate) _progress = new() { Stage = "cancelled", Message = "字体制作已取消。" }; }
        catch (Exception ex)
        {
            var error = ex is InvalidDataException or InvalidOperationException ? ex.Message : "字体制作失败，请重新选择完整的字体文件。";
            lock (_gate) { _error = error; _progress = new() { Stage = "error", Message = error }; }
            _logs.Write("warning", "font-maker", error);
        }
        finally
        {
            if (worker != null)
            {
                try { if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); } } catch (InvalidOperationException) { }
                worker.Dispose();
            }
            Cleanup(folder);
            lock (_gate) _jobFolder = null;
            _end();
        }
    }
    public List<TmpFontMakeResult> Results()
    {
        var directory = Path.Combine(_root, "generated"); UpdatePackage.RejectLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var results = new List<TmpFontMakeResult>();
        foreach (var folder in Directory.EnumerateDirectories(directory).OrderByDescending(Directory.GetCreationTimeUtc).Take(200))
        {
            var id = Path.GetFileName(folder); if (!ValidId(id)) continue;
            try { results.Add(Result(id)); } catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
        }
        return results.OrderByDescending(x => x.CreatedAt).ToList();
    }
    public TmpFontMakeResult Result(string id)
    {
        var folder = ResultDirectory(id); var path = Path.Combine(folder, "result.json"); UpdatePackage.RejectLinks(path);
        if (!File.Exists(path)) throw new KeyNotFoundException("找不到已生成的字体。");
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("字体报告异常。");
        var result = JsonSerializer.Deserialize<TmpFontMakeResult>(File.ReadAllText(path), UpdatePackage.Json) ?? throw new InvalidDataException("字体报告无效。");
        if (result.Id != id || result.FileName != Path.GetFileName(result.FileName)) throw new InvalidDataException("字体报告与文件不匹配。");
        return result;
    }
    public FileStream OpenBundle(string id)
    {
        var result = Result(id); var path = BundlePath(id); UpdatePackage.RejectLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != result.Bytes || stream.Length > 64 * 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(stream)).Equals(result.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("已生成字体的校验失败，请重新制作。");
            stream.Position = 0; return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    public string VerifiedBundlePath(string id) { using var stream = OpenBundle(id); return BundlePath(id); }
    public string OutputDirectory()
    {
        var path = Path.Combine(_root, "generated"); UpdatePackage.RejectLinks(path); Directory.CreateDirectory(path); return path;
    }
    private string BundlePath(string id) => Path.Combine(ResultDirectory(id), "font.bundle");
    private string ResultDirectory(string id) => ValidId(id) ? Path.Combine(_root, "generated", id) : throw new ArgumentException("字体标识无效。");
    private static bool ValidId(string id) => Regex.IsMatch(id, "^[a-f0-9]{32}$");
    private void Cleanup(string folder)
    {
        if (!UpdatePackage.Within(_root, folder)) return;
        try { UpdatePackage.RejectLinks(folder); if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        Task? work;
        lock (_gate) { if (_disposed) return; _disposed = true; _stop?.Cancel(); work = _work; }
        // Keep the native child from being orphaned when the desktop window closes.
        try { work?.Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
        if (work == null || work.IsCompleted) _stop?.Dispose();
    }
}
