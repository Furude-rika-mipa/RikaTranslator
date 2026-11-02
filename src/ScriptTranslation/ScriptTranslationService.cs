using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit.ScriptTranslation;

public sealed partial class ScriptTranslationService : IDisposable
{
    public const int SelectionLimit = 1000;
    public const int McpBatchLimit = 50;
    private readonly DataStore _store;
    private readonly TranslationService _translation;
    private readonly LogService _logs;
    private readonly string _root, _application;
    private readonly Action _begin, _end;
    private readonly object _gate = new();
    private readonly Dictionary<string, ScriptJob> _jobs = [];
    private CancellationTokenSource? _stop;
    private Task? _work;
    private Process? _worker;
    private string? _active;
    private bool _disposed;

    public ScriptTranslationService(DataStore store, TranslationService translation, LogService logs,
        string application, Action begin, Action end)
    {
        _store = store; _translation = translation; _logs = logs; _application = application;
        _root = Path.Combine(store.DirectoryPath, "script-translation", "jobs"); _begin = begin; _end = end;
        UpdatePackage.RejectLinks(_root);
        if (!Directory.Exists(_root)) return;
        foreach (var directory in Directory.EnumerateDirectories(_root).OrderDescending().Take(100))
        {
            var id = Path.GetFileName(directory);
            if (!ValidId(id)) continue;
            try
            {
                var file = Path.Combine(directory, "job.json"); UpdatePackage.RejectLinks(file);
                if (!File.Exists(file) || new FileInfo(file).Length > 64L * 1024 * 1024) continue;
                var job = JsonSerializer.Deserialize<ScriptJob>(File.ReadAllBytes(file), UpdatePackage.Json);
                if (job == null || job.Id != id || job.Segments == null || job.Segments.Count > 100_000 ||
                    job.Segments.Any(s => s == null || s.Source == null || s.Source.Length > 4096 || string.IsNullOrEmpty(s.Id) || s.File == null || s.Scene == null) ||
                    job.Segments.Select(s => s.Id).Distinct().Count() != job.Segments.Count) continue;
                job.Warnings ??= [];
                job.SelectionIds ??= [];
                job.Providers ??= [];
                if (job.SelectionIds.Count > 100_000) continue;
                job.Providers = job.Providers.Where(p => p != null && !string.IsNullOrEmpty(p.ProviderId)).Take(8).ToList();
                if (job.External is { } ext && (ext.Ids == null || ext.Leases == null || ext.Receipts == null ||
                    ext.Ids.Count > job.Segments.Count || ext.Ids.Distinct().Count() != ext.Ids.Count ||
                    ext.Ids.Except(job.Segments.Select(s => s.Id)).Any() || ext.Leases.Count > 4 || ext.Receipts.Count > 100_000 ||
                    ext.Leases.Any(l => l == null || l.Items == null || l.Items.Count is < 1 or > McpBatchLimit ||
                        l.Items.Any(i => i == null || i.Ids == null || !i.Ids.Contains(i.Id) || i.Ids.Except(ext.Ids).Any())) ||
                    ext.Receipts.Any(r => r == null))) job.External = null;
                foreach (var provider in job.Providers) { provider.Working = 0; provider.Status = "待命"; }
                if (job.Stage is "extracting" or "parsing" or "translating")
                { job.Stage = job.Segments.Count > 0 ? "paused" : "interrupted"; job.Message = "上次任务已中断，已保存的译文保留；请重新选择要翻译的条目。"; }
                foreach (var segment in job.Segments.Where(s => s.Status == "queued")) segment.Status = "pending";
                _jobs[id] = job;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    private bool Busy => _work is { IsCompleted: false };
    public bool HasActiveGame(string id) { lock (_gate) return Busy && _active != null && _jobs[_active].GameId == id || _jobs.Values.Any(j => j.GameId == id && j.External?.Enabled == true); }
    private static bool ValidId(string id) => Regex.IsMatch(id, "\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant);
    private string Folder(string id) => ValidId(id) ? Path.Combine(_root, id) : throw new ArgumentException("无效的剧本任务。");
    private ScriptJob Job(string id) => _jobs.GetValueOrDefault(id) ?? throw new KeyNotFoundException("找不到剧本任务。");
    private void Save(ScriptJob job)
    {
        var folder = Folder(job.Id); UpdatePackage.RejectLinks(folder); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "job.json"); UpdatePackage.RejectLinks(file);
        UpdatePackage.WriteJson(file, job);
    }
    private object Summary(ScriptJob job) => new {
        job.Id, job.GameId, job.GameName, job.Archive, job.Stage, job.Message, job.CreatedAt,
        job.Engine, job.Format, patchSupported = SupportsScriptPatch(job),
        job.Resources, job.ScriptFiles, job.Scenes, job.Selected, job.Finished, job.Failed,
        total = job.Segments.Count, translated = job.Segments.Count(s => s.Status == "translated"),
        pending = job.Segments.Count(s => s.Status == "pending"), queued = job.Segments.Count(s => s.Status == "queued"),
        failedLines = job.Segments.Count(s => s.Status == "failed"),
        skipped = job.Segments.Count(s => s.Status == "skipped"), busy = Busy && _active == job.Id,
        job.ProviderName, job.Model, job.Scope, job.ScopeLabel,
        warnings = SupportsScriptPatch(job) ? job.Warnings.Where(w => !w.Contains("独立汉化补丁尚未适配", StringComparison.Ordinal)).ToArray() : job.Warnings.ToArray(),
        external = ExternalSummary(job),
        resumePending = ResumeSegments(job).Count(s => s.Status is "pending" or "failed"),
        providers = job.Providers.Select(p => new { p.ProviderId, p.Name, p.Model, p.Concurrency,
            p.Working, p.Requests, p.CompletedLines, p.Failures, p.Status }).ToArray(),
        files = job.Segments.GroupBy(s => s.File).Select(g => new { name = g.Key, count = g.Count() }).OrderBy(g => g.name),
        selectionLimit = SelectionLimit
    };
    public object List() { lock (_gate) return _jobs.Values.OrderByDescending(j => j.CreatedAt).Select(Summary).ToArray(); }
    public object Status(string id) { lock (_gate) return Summary(Job(id)); }
    private static IEnumerable<ScriptSegment> MatchingRows(ScriptJob job, string? file, string? scene, string? query, bool failedOnly) =>
        job.Segments.Where(s => (!failedOnly || s.Status == "failed") && (string.IsNullOrEmpty(file) || s.File == file) &&
            (string.IsNullOrEmpty(scene) || s.Scene == scene) && (string.IsNullOrEmpty(query) ||
             (s.Source + s.Translation + s.Speaker).Contains(query, StringComparison.OrdinalIgnoreCase)));
    public object Page(string id, string? file, string? scene, string? query, int page, bool failedOnly = false)
    {
        lock (_gate)
        {
            var rows = MatchingRows(Job(id), file, scene, query, failedOnly).ToArray();
            page = Math.Clamp(page, 1, Math.Max(1, (rows.Length + 49) / 50));
            return new { page, pages = Math.Max(1, (rows.Length + 49) / 50), total = rows.Length,
                scenes = Job(id).Segments.Where(s => string.IsNullOrEmpty(file) || s.File == file).Select(s => s.Scene).Distinct().Order().ToArray(),
                items = rows.Skip((page - 1) * 50).Take(50).Select(s => new { s.Id, s.File, s.Scene, s.Index, s.Speaker,
                    s.Source, s.Translation, s.Status, s.Error, s.ProviderName, s.Model, s.Attempts }).ToArray() };
        }
    }
    public object FailedSelection(string id, string? file, string? scene, string? query)
    {
        lock (_gate)
        {
            var matches = MatchingRows(Job(id), file, scene, query, true).ToArray();
            return new { total = matches.Length, ids = matches.Take(SelectionLimit).Select(s => s.Id).ToArray() };
        }
    }
    private GameRecord Game(string id) => _store.Snapshot().Games.FirstOrDefault(g => g.Id == id) ?? throw new InvalidOperationException("请先在游戏库添加并选择游戏。");
    public object Inspect(string gameId)
    {
        var game = Game(gameId);
        if (MultiEngine(game) is { } engine) return InspectMultiEngine(game, engine);
        if (game.Engine != "KiriKiri") throw new InvalidOperationException("当前剧本提取支持 KiriKiri / Hxv4 的 PSB 场景结构。");
        UpdatePackage.RejectLinks(game.Directory); UpdatePackage.RejectLinks(game.ExecutablePath);
        var gamePrefix = Path.GetFullPath(game.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_application).StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(_root).StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请将梨花程序和数据目录放在游戏目录之外，再建立临时提取副本。");
        if (!File.Exists(game.ExecutablePath)) throw new InvalidOperationException("游戏启动程序不存在。");
        using var stream = File.OpenRead(game.ExecutablePath);
        Span<byte> header = stackalloc byte[64]; stream.ReadExactly(header);
        var pe = BitConverter.ToInt32(header[60..64]);
        if (pe < 64 || pe > stream.Length - 6) throw new InvalidDataException("游戏 EXE 格式无效。");
        stream.Position = pe; Span<byte> machine = stackalloc byte[6]; stream.ReadExactly(machine);
        var supported = machine[..4].SequenceEqual("PE\0\0"u8) && BitConverter.ToUInt16(machine[4..]) == 0x14c;
        var archives = Directory.EnumerateFiles(game.Directory, "*.xp3", SearchOption.TopDirectoryOnly).Take(100)
            .Select(p => new { name = Path.GetFileName(p), bytes = new FileInfo(p).Length }).OrderBy(p => p.name).ToArray();
        return new { supported, archives, message = supported ? "提取会建立临时游戏副本，不复制存档；需要足够磁盘空间。请先保存进度并退出游戏。" : "此提取组件暂只支持 32 位 KiriKiri 游戏。" };
    }

    public object Extract(ScriptExtractionRequest request)
    {
        var game = Game(request.GameId);
        if (MultiEngine(game) is { } engine) return ExtractMultiEngine(game, engine, request);
        if (!JsonSerializer.SerializeToElement(Inspect(game.Id)).GetProperty("supported").GetBoolean())
            throw new InvalidOperationException("此提取组件暂只支持 32 位 KiriKiri 游戏。");
        if (string.IsNullOrWhiteSpace(request.Archive) || Path.GetFileName(request.Archive) != request.Archive || !request.Archive.EndsWith(".xp3", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择游戏目录里的 XP3 封包。");
        var archive = Path.Combine(game.Directory, request.Archive); UpdatePackage.RejectLinks(archive);
        if (!File.Exists(archive)) throw new InvalidOperationException("找不到选中的 XP3 封包。");
        foreach (var exe in Directory.EnumerateFiles(game.Directory, "*.exe").Take(100))
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                using (process)
                    try { if (Path.GetFullPath(process.MainModule?.FileName ?? "").Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请保存进度并退出这个游戏后再提取。"); }
                    catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("无法确认游戏进程状态，请退出这个游戏后再提取。"); }
        VerifyComponents();
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptTranslationService));
            if (Busy) throw new InvalidOperationException("已有剧本任务正在运行，请先暂停或取消。");
            if (_jobs.Count >= 100) throw new InvalidOperationException("剧本任务已达到 100 个，请先整理输出目录。");
            var job = new ScriptJob { Id = Guid.NewGuid().ToString("N"), GameId = game.Id, GameName = game.Name,
                Archive = request.Archive, Message = "正在启动提取组件…" };
            _begin();
            try
            {
                _jobs[job.Id] = job; Save(job); _active = job.Id;
                _stop?.Dispose(); _stop = new CancellationTokenSource();
                _work = Task.Run(() => ExtractAsync(job, game, _stop.Token));
                return Summary(job);
            }
            catch { _end(); throw; }
        }
    }

    private void VerifyComponents()
    {
        var root = Path.Combine(_application, "adapters", "script-extraction", "native");
        var manifest = Path.Combine(root, "component-manifest.json"); UpdatePackage.RejectLinks(manifest);
        if (!File.Exists(manifest)) throw new InvalidDataException("剧本提取组件不完整，请重新解压完整程序包。");
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(manifest), UpdatePackage.Json) ?? throw new InvalidDataException("提取组件清单无效。");
        foreach (var name in new[] { "RikaCxdecWorker.exe", "RikaCxdecExtractor.dll" })
        {
            var file = Path.Combine(root, name); UpdatePackage.RejectLinks(file);
            if (!expected.TryGetValue(name, out var hash) || !File.Exists(file) || !UpdatePackage.Hash(file).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("剧本提取组件校验失败，请重新解压完整程序包。");
        }
    }

    private async Task ExtractAsync(ScriptJob job, GameRecord game, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            var folder = Folder(job.Id);
            lock (_gate) { job.Message = "正在建立临时游戏副本，不复制用户存档…"; Save(job); }
            var gameCopy = await CopyGameAsync(job, game, ct);
            var worker = Path.Combine(_application, "adapters", "script-extraction", "native", "RikaCxdecWorker.exe");
            var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(worker)! };
            start.ArgumentList.Add(gameCopy); start.ArgumentList.Add(folder); start.ArgumentList.Add(job.Archive);
            foreach (var key in start.Environment.Keys.Where(k => Regex.IsMatch(k, "API_KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL", RegexOptions.IgnoreCase)).ToArray()) start.Environment.Remove(key);
            process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("无法启动剧本提取组件。");
            lock (_gate) { _worker = process; job.Message = "正在读取游戏资源，请等待提取完成…"; Save(job); }
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout; _ = await stderr;
            if (process.ExitCode != 0 || !output.Contains("\"phase\":\"done\"", StringComparison.Ordinal)) throw new InvalidDataException("此游戏的 Hxv4 提取未完成，可能是运行时接口不匹配或游戏未能启动。未开始 AI 翻译。");
            lock (_gate) { job.Stage = "parsing"; job.Message = "正在识别 PSB 场景中的对白…"; Save(job); }
            var raw = Path.Combine(folder, "Extractor_Output", Path.GetFileNameWithoutExtension(job.Archive)); UpdatePackage.RejectLinks(raw);
            if (!Directory.Exists(raw)) throw new InvalidDataException("提取组件没有生成可读取的资源。");
            var segments = new List<ScriptSegment>(); var scripts = 0; var scenes = 0; var resources = 0; var warnings = new List<string>();
            long totalBytes = 0;
            foreach (var file in Directory.EnumerateFiles(raw, "*", SearchOption.AllDirectories).Take(10001))
            {
                ct.ThrowIfCancellationRequested(); UpdatePackage.RejectLinks(file);
                if (++resources > 10_000 || (totalBytes += new FileInfo(file).Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("提取资源超过解析范围，请只选择剧本封包。");
                if (new FileInfo(file).Length is < 44 or > PsbScenarioReader.MaxBytes) continue;
                using var stream = File.OpenRead(file); var header = new byte[4]; stream.ReadExactly(header);
                if (!header.AsSpan().SequenceEqual("PSB\0"u8) && !header.AsSpan().SequenceEqual("mdf\0"u8)) continue;
                try
                {
                    var result = new PsbScenarioReader(File.ReadAllBytes(file)).Extract(Path.GetRelativePath(raw, file));
                    if (result.Segments.Count == 0) continue;
                    segments.AddRange(result.Segments); scripts++; scenes += result.Scenes;
                    if (segments.Count > 100_000) throw new InvalidDataException("剧本对白超过十万条，请拆分资源包。");
                }
                catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or EndOfStreamException or OverflowException)
                { if (warnings.Count < 20) warnings.Add("有一个 PSB 资源不符合当前场景结构，已保留原文件并跳过。"); }
                if (segments.Count > 100_000) throw new InvalidDataException("剧本对白超过十万条，请拆分资源包。");
            }
            lock (_gate)
            {
                job.Resources = resources; job.ScriptFiles = scripts; job.Scenes = scenes; job.Segments = segments;
                job.Warnings = warnings; job.Stage = segments.Count > 0 ? "ready" : "unsupported";
                job.Message = segments.Count > 0 ? "提取完成。请预览并勾选要翻译的对白；当前未选择任何条目。" : "资源已提取，但未识别到当前支持的 PSB 对白结构，未开始 AI 翻译。";
                Save(job);
            }
            _logs.Write("info", "script-translation", "剧本提取与对白识别已结束。", game.Id, details: new { resources, scripts, scenes, lines = segments.Count });
        }
        catch (OperationCanceledException)
        { lock (_gate) { job.Stage = ct.IsCancellationRequested ? "cancelled" : "failed"; job.Message = ct.IsCancellationRequested ? "提取已取消，已生成的资源保留。" : "提取超时，未开始 AI 翻译。"; Save(job); } }
        catch (Exception ex)
        { lock (_gate) { job.Stage = "failed"; job.Message = ex is InvalidDataException ? ex.Message : "剧本提取失败，请检查游戏和输出目录。未开始 AI 翻译。"; Save(job); } _logs.Write("warning", "script-translation", job.Message, game.Id); }
        finally
        {
            if (process != null) { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch (InvalidOperationException) { } process.Dispose(); }
            lock (_gate) _worker = null;
            CleanupGameCopy(job.Id);
            _end();
        }
    }

    private async Task<string> CopyGameAsync(ScriptJob job, GameRecord game, CancellationToken ct)
    {
        var root = Path.GetFullPath(game.Directory);
        var copy = Path.Combine(Folder(job.Id), "game-copy"); UpdatePackage.RejectLinks(copy);
        var folders = new Queue<string>(); folders.Enqueue(root);
        var excluded = new HashSet<string>(["savedata", "save", "saves", "userdata", "backup", "backups", "logs", "Extractor_Output", "StringHashDumper_Output"], StringComparer.OrdinalIgnoreCase);
        var files = new List<(string Source, string Relative, long Bytes, DateTime Modified)>();
        long bytes = 0;
        while (folders.TryDequeue(out var directory))
        {
            ct.ThrowIfCancellationRequested(); UpdatePackage.RejectLinks(directory);
            foreach (var child in Directory.EnumerateDirectories(directory))
            { if (!excluded.Contains(Path.GetFileName(child))) { UpdatePackage.RejectLinks(child); folders.Enqueue(child); } }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is ".log" or ".bak" or ".ksd") continue;
                UpdatePackage.RejectLinks(file);
                var info = new FileInfo(file); bytes = checked(bytes + info.Length);
                if (bytes > 24L * 1024 * 1024 * 1024 || files.Count >= 10_000 || folders.Count > 1000) throw new InvalidDataException("临时副本范围超过 24 GB 或一万个文件，请只选择游戏主目录。");
                files.Add((file, Path.GetRelativePath(root, file), info.Length, info.LastWriteTimeUtc));
            }
        }
        var drive = new DriveInfo(Path.GetPathRoot(copy)!);
        if (drive.AvailableFreeSpace < bytes + 512L * 1024 * 1024) throw new InvalidDataException("临时副本所需磁盘空间不足，请先整理梨花所在磁盘。");
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = UpdatePackage.Target(copy, file.Relative.Replace('\\', '/')); UpdatePackage.RejectLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                await source.CopyToAsync(destination, ct);
            var now = new FileInfo(file.Source);
            if (now.Length != file.Bytes || now.LastWriteTimeUtc != file.Modified || new FileInfo(target).Length != file.Bytes)
                throw new InvalidDataException("游戏文件在复制期间发生变化，请退出游戏后重新提取。");
        }
        return UpdatePackage.Target(copy, Path.GetRelativePath(root, game.ExecutablePath).Replace('\\', '/'));
    }

    private void CleanupGameCopy(string id)
    {
        var folder = Path.GetFullPath(Folder(id));
        var copy = Path.GetFullPath(Path.Combine(folder, "game-copy"));
        try
        {
            if (Path.GetDirectoryName(copy) != folder || Path.GetFileName(copy) != "game-copy" || !Directory.Exists(copy)) return;
            UpdatePackage.RejectLinks(copy);
            var pending = new Queue<string>(); pending.Enqueue(copy);
            while (pending.TryDequeue(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Enqueue(entry);
                }
            // The final absolute target and every descendant have been checked inside this job.
            Directory.Delete(copy, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { _logs.Write("warning", "script-translation", "临时游戏副本暂未清除，可在任务文件夹中查看；原游戏未改动。"); }
    }

    public object Translate(string id, ScriptBatchRequest request)
    {
        if (!request.Confirmed || request.Ids == null || request.Ids.Count is < 1 or > SelectionLimit || request.Ids.Distinct().Count() != request.Ids.Count)
            throw new ArgumentException($"请明确选择 1–{SelectionLimit} 条对白后开始翻译。");
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptTranslationService));
            if (Busy) throw new InvalidOperationException("已有剧本任务正在运行，请先暂停。");
            var job = Job(id);
            var ids = request.Ids.ToHashSet(StringComparer.Ordinal);
            var selected = job.Segments.Where(s => ids.Contains(s.Id)).ToArray();
            if (selected.Length != ids.Count) throw new ArgumentException("选中的对白不属于此任务。");
            if (selected.Any(s => s.Status is "translated" or "skipped")) throw new InvalidOperationException("已完成或只有标点的对白不会重复提交，请重新选择待翻译项。");
            return StartBatch(job, selected, "selected", "选中的对白");
        }
    }

    private (ScriptScopePlan Plan, ScriptSegment[] Segments) ScopePlan(ScriptJob job, ScriptScopeRequest request)
    {
        if (request.Scope is not ("all" or "file" or "resume")) throw new ArgumentException("请选择当前剧本、全部待译或上次范围。");
        if (request.Scope == "file" && (string.IsNullOrWhiteSpace(request.File) || !job.Segments.Any(s => s.File == request.File)))
            throw new ArgumentException("请先选择一个剧本，未选择时不会自动改为全量翻译。");
        var range = request.Scope == "resume" ? ResumeSegments(job).ToArray() : job.Segments.Where(s => request.Scope == "all" || s.File == request.File).ToArray();
        var selected = range.Where(s => s.Status is "pending" or "failed").ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("这个范围没有待翻译或可重试的对白。");
        var data = _store.Snapshot(); var game = data.Games.FirstOrDefault(g => g.Id == job.GameId) ?? throw new InvalidOperationException("找不到此任务的游戏，请先将游戏重新添加到库中。");
        var providers = ScriptBatchPolicy.Resolve(data, game);
        var stamp = ScriptBatchPolicy.Stamp(data, game);
        var unique = selected.Select(s => s.Source).Distinct(StringComparer.Ordinal).ToArray();
        var batchLines = ScriptBatchPolicy.BatchLines(data);
        var batches = ScriptBatchPolicy.Batches(selected, batchLines).Count;
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            job.Id, request.Scope, file = request.Scope == "file" ? request.File : null,
            stamp, batchLines, ids = selected.Select(s => s.Id)
        }))).ToLowerInvariant();
        var label = request.Scope == "all" ? "整个任务的全部待译对白" : request.Scope == "resume" ? "上次选择的范围" : "剧本 " + request.File;
        return (new(request.Scope, request.Scope == "file" ? request.File : null, label, selected.Length, unique.Length,
            selected.Sum(s => (long)s.Source.Length), range.Count(s => s.Status == "translated"), range.Count(s => s.Status == "skipped"),
            batches, ProviderLabel(providers), providers.Length == 1 ? providers[0].Model : "多个模型", stamp, fingerprint, providers), selected);
    }

    public object PreviewScope(string id, ScriptScopeRequest request)
    {
        lock (_gate)
        {
            if (Busy) throw new InvalidOperationException("请先暂停当前剧本任务，再选择新的翻译范围。");
            return ScopePlan(Job(id), request).Plan;
        }
    }

    public object TranslateScope(string id, ScriptScopeRequest request)
    {
        if (!request.Confirmed || string.IsNullOrEmpty(request.Fingerprint)) throw new ArgumentException("请先查看本次范围和条数，再确认开始翻译。");
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptTranslationService));
            if (Busy) throw new InvalidOperationException("已有剧本任务正在运行，请先暂停。");
            var job = Job(id); var (plan, selected) = ScopePlan(job, request);
            if (!string.Equals(plan.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("待译范围或翻译配置已变化，请重新查看翻译范围后再开始。");
            return StartBatch(job, selected, plan.Scope, plan.Label, plan.Configuration);
        }
    }

    // Caller holds _gate; every entry point explicitly identifies the selected range.
    private object StartBatch(ScriptJob job, ScriptSegment[] selected, string scope, string label, string? expectedConfiguration = null)
    {
            if (job.External?.Enabled == true) throw new InvalidOperationException("请先暂停此任务的外部助手领取，再使用 AI API 翻译。");
            var data = _store.Snapshot(); var game = data.Games.FirstOrDefault(g => g.Id == job.GameId) ?? throw new InvalidOperationException("找不到此任务的游戏，请先将游戏重新添加到库中。");
            if (!data.Settings.Enabled || !game.Enabled) throw new InvalidOperationException("翻译已暂停，请先开启翻译。");
            var providers = ScriptBatchPolicy.Resolve(data, game);
            var stamp = ScriptBatchPolicy.Stamp(data, game);
            if (expectedConfiguration != null && stamp != expectedConfiguration) throw new InvalidOperationException("翻译配置已改变，请重新查看范围后再开始。");
            _begin();
            try
            {
                _stop?.Dispose(); _stop = new CancellationTokenSource(); _active = job.Id;
                _limiter.Begin(providers, _jobs.Values.SelectMany(j => j.Providers));
                job.Configuration = stamp; job.ProviderName = ProviderLabel(providers); job.Model = providers.Length == 1 ? providers[0].Model : "多个模型";
                job.Providers = providers.Select(p => new ScriptProviderProgress { ProviderId = p.ProviderId, Name = p.Name, Model = p.Model,
                    Concurrency = p.Concurrency, NextRequestAt = _jobs.Values.SelectMany(j => j.Providers).Where(old => old.ProviderId == p.ProviderId)
                        .Select(old => old.NextRequestAt).DefaultIfEmpty().Max() }).ToList();
                if (scope != "resume") job.SelectionIds = selected.Select(s => s.Id).ToList();
                job.Scope = scope; job.ScopeLabel = label;
                job.Stage = "translating"; job.Selected = selected.Length; job.Finished = 0; job.Failed = 0;
                foreach (var segment in selected) { segment.Status = "queued"; segment.Error = ""; }
                job.Message = $"本次范围：{label}，共 {selected.Length} 条；其他范围不提交。"; Save(job);
                var batchLines = ScriptBatchPolicy.BatchLines(data);
                _work = Task.Run(() => TranslateAsync(job, selected, providers, stamp, batchLines, _stop.Token));
                return Summary(job);
            }
            catch { _end(); throw; }
    }

    public object Cancel(string id)
    {
        lock (_gate)
        {
            var job = Job(id);
            if (job.External?.Enabled == true)
                ExternalTransaction(job, () => { job.External.Enabled = false; job.External.Leases.Clear(); job.External.UpdatedAt = DateTimeOffset.UtcNow;
                    job.Stage = "paused"; job.Message = "外部领取已暂停，未回传批次已作废；已保存译文保留。"; });
            if (_active == id && Busy) _stop?.Cancel(); return new { cancelling = _active == id && Busy };
        }
    }
    public string OutputDirectory(string? id = null)
    {
        lock (_gate) { if (id != null) _ = Job(id); var folder = id == null ? _root : Folder(id); UpdatePackage.RejectLinks(folder); Directory.CreateDirectory(folder); return folder; }
    }
    public byte[] Export(string id, string format)
    {
        lock (_gate)
        {
            var job = Job(id);
            var translated = job.Segments.Where(s => s.Status == "translated").ToArray();
            if (translated.Length == 0) throw new InvalidOperationException("当前没有已完成译文，请先选择少量对白翻译。");
            if (format == "json") return JsonSerializer.SerializeToUtf8Bytes(new { format = "rika-script-translation-1", job.GameName, job.Archive,
                items = translated.Select(s => new { s.Id, s.File, s.Scene, s.Index, s.Speaker, s.Source, s.Translation, s.Status }) }, UpdatePackage.Json);
            if (format != "txt") throw new ArgumentException("请选择 JSON 或 TXT 格式。");
            var text = string.Join("\r\n\r\n", translated.Select(s => $"[{s.File} | {s.Scene} | {s.Index}] {s.Speaker}\r\n原文：{s.Source}\r\n译文：{s.Translation}"));
            return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        }
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _stop?.Cancel(); try { if (_worker is { HasExited: false }) _worker.Kill(true); } catch (InvalidOperationException) { } }
        try { _work?.Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
    }
}
