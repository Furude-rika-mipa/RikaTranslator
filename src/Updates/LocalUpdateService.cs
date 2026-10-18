using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameTranslateToolkit.Updates;

public sealed class UpdatePreview
{
    public string Id { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public int FileCount { get; set; }
    public long PackageSize { get; set; }
    public long ExpandedSize { get; set; }
    public bool CreateBackup { get; set; }
}

public sealed class LocalUpdateService
{
    private readonly string _root, _data;
    private readonly LogService _logs;
    private readonly string[] _restartArguments;
    private readonly bool _server;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _requests = new();
    private UpdatePlan? _pending;
    private int _writers;
    private bool _scheduled;
    public LocalUpdateService(string root, string data, LogService logs, bool server, int port)
    {
        _root = Path.GetFullPath(root); _data = Path.GetFullPath(data); _logs = logs; _server = server;
        _restartArguments = server ? ["--server", "--data-dir", _data, "--port", port.ToString()] : ["--data-dir", _data, "--port", port.ToString()];
    }

    public void EnterMutation()
    {
        lock (_requests)
        {
            if (_scheduled) throw new InvalidOperationException("正在退出并安装更新，请等待工具重新打开。");
            _writers++;
        }
    }
    public void ExitMutation() { lock (_requests) _writers--; }
    public bool Scheduled { get { lock (_requests) return _scheduled; } }

    public object Status()
    {
        UpdateResult? result = null;
        try
        {
            var path = Path.Combine(_data, "updates", "last-result.json"); UpdatePackage.RejectLinks(path);
            if (File.Exists(path) && new FileInfo(path).Length < 65536)
                result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), UpdatePackage.Json);
        }
        catch { /* A damaged report must not hide the update entry point. */ }
        var recovery = new List<object>();
        var repair = new List<object>();
        var sessions = Path.Combine(_data, "updates");
        if (Directory.Exists(sessions))
            foreach (var folder in Directory.EnumerateDirectories(sessions).Take(200))
                try
                {
                    var plan = ReadPlan(Path.GetFileName(folder));
                    var path = Path.Combine(folder, "journal.json"); UpdatePackage.RejectLinks(path);
                    if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                    var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path), UpdatePackage.Json);
                    if (journal?.State is "applying" or "repair-required" && !plan.CreateBackup) repair.Add(new { plan.Id, plan.TargetVersion });
                    else if (journal?.State is "applying" or "rollback-failed") recovery.Add(new { plan.Id, plan.CurrentVersion, plan.TargetVersion });
                }
                catch { }
        return new { currentVersion = Program.Version, architecture = "win-x64", lastResult = result, recovery, repair, scheduled = Scheduled };
    }

    public async Task<UpdatePreview> InspectAsync(string input, CancellationToken ct, bool createBackup = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Scheduled) throw new InvalidOperationException("更新已经开始。");
            DiscardPending();
            var path = Path.GetFullPath(input.Trim().Trim('"'));
            if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidDataException("请选择存在的 ZIP 更新包。");
            UpdatePackage.RejectLinks(path);
            var plan = new UpdatePlan { Id = Guid.NewGuid().ToString("N"), TargetDirectory = _root, DataDirectory = _data, CurrentVersion = Program.Version, RestartArguments = _restartArguments, CreateBackup = createBackup };
            var session = UpdateInstaller.Session(plan); UpdatePackage.RejectLinks(session); Directory.CreateDirectory(session);
            var package = Path.Combine(session, "package.zip");
            try
            {
                await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                {
                    if (source.Length > UpdatePackage.MaxArchiveSize) throw new InvalidDataException("更新包超过 1 GB。");
                    await using var destination = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await UpdatePackage.CopyBoundedAsync(source, destination, source.Length, ct);
                }
                var manifest = await UpdatePackage.ValidateAsync(package, Program.Version, ct);
                foreach (var file in manifest.Files.Concat(manifest.RemoveFiles))
                    if (UpdatePackage.Within(_data, UpdatePackage.Target(_root, file.Path))) throw new InvalidDataException("更新包不能覆盖当前用户数据目录。");
                plan.TargetVersion = manifest.Version; plan.PackageHash = UpdatePackage.Hash(package);
                _pending = plan;
                _logs.Write("info", "update", "本地更新包校验通过，等待用户确认。", details: new { fromVersion = plan.CurrentVersion, targetVersion = plan.TargetVersion, files = manifest.Files.Count });
                return new UpdatePreview { Id = plan.Id, CurrentVersion = plan.CurrentVersion, TargetVersion = manifest.Version, ReleaseNotes = manifest.ReleaseNotes, FileCount = manifest.Files.Count, PackageSize = new FileInfo(package).Length, ExpandedSize = manifest.Files.Sum(f => f.Size), CreateBackup = plan.CreateBackup };
            }
            catch { if (File.Exists(package)) File.Delete(package); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (Scheduled) throw new InvalidOperationException("更新已经开始。"); if (_pending?.Id == id) DiscardPending(); }
        finally { _gate.Release(); }
    }

    internal async Task<string> SavePendingAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Scheduled || _pending?.Id != id || _pending.CreateBackup)
                throw new InvalidOperationException("待安装的在线更新已失效。");
            UpdatePackage.WriteJson(Path.Combine(UpdateInstaller.Session(_pending), "plan.json"), _pending);
            return _pending.PackageHash;
        }
        finally { _gate.Release(); }
    }

    internal async Task<UpdatePreview> RestorePendingAsync(string id, string expectedHash, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Scheduled || _pending != null && _pending.Id != id)
                throw new InvalidOperationException("已有其他更新任务，请重新检查更新。");
            var plan = ReadPlan(id);
            var session = UpdateInstaller.Session(plan);
            var package = Path.Combine(session, "package.zip");
            UpdatePackage.RejectLinks(package);
            if (plan.CreateBackup || File.Exists(Path.Combine(session, "journal.json")) ||
                File.Exists(Path.Combine(session, "runner-ready.json")))
                throw new InvalidDataException("此更新任务已尝试安装，不能作为下载缓存恢复。");
            if (plan.PackageHash != expectedHash || new FileInfo(package).Length is < 22 or > UpdatePackage.MaxArchiveSize)
                throw new InvalidDataException("已下载更新包的校验记录无效。");
            await using (var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                if (Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant() != expectedHash)
                    throw new InvalidDataException("已下载更新包的 SHA-256 校验失败。");
            var manifest = await UpdatePackage.ValidateAsync(package, Program.Version, ct);
            if (manifest.Version != plan.TargetVersion) throw new InvalidDataException("已下载更新包的目标版本发生变化。");
            plan.CurrentVersion = Program.Version;
            plan.RestartArguments = _restartArguments;
            plan.ParentProcessId = 0; plan.ParentStartTimeUtcTicks = 0;
            UpdatePackage.WriteJson(Path.Combine(session, "plan.json"), plan);
            _pending = plan;
            return new UpdatePreview { Id = plan.Id, CurrentVersion = Program.Version, TargetVersion = manifest.Version,
                ReleaseNotes = manifest.ReleaseNotes, FileCount = manifest.Files.Count, PackageSize = new FileInfo(package).Length,
                ExpandedSize = manifest.Files.Sum(f => f.Size), CreateBackup = false };
        }
        finally { _gate.Release(); }
    }

    internal async Task DiscardSavedPendingAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Scheduled) throw new InvalidOperationException("安装已经开始，不能删除更新包。");
            if (_pending?.Id == id) { DiscardPending(); return; }
            DeleteUnstartedPackage(ReadPlan(id));
        }
        finally { _gate.Release(); }
    }

    private static void DeleteUnstartedPackage(UpdatePlan plan)
    {
        var session = UpdateInstaller.Session(plan);
        UpdatePackage.RejectLinks(session);
        if (plan.CreateBackup || File.Exists(Path.Combine(session, "journal.json")) ||
            File.Exists(Path.Combine(session, "runner-ready.json"))) return;
        foreach (var name in new[] { "package.zip", "plan.json" })
        {
            var path = Path.Combine(session, name); UpdatePackage.RejectLinks(path);
            if (File.Exists(path)) File.Delete(path);
        }
        if (Directory.Exists(session) && !Directory.EnumerateFileSystemEntries(session).Any()) Directory.Delete(session, false);
    }

    private void DiscardPending()
    {
        if (_pending == null) return;
        DeleteUnstartedPackage(_pending);
        _logs.Write("info", "update", "已取消待确认的本地更新。"); _pending = null;
    }

    public async Task ScheduleAsync(string id, bool confirmed, TranslationStats stats, bool restore, CancellationToken ct)
    {
        if (!confirmed) throw new InvalidOperationException("请先查看更新说明，再确认更新并重启。");
        await _gate.WaitAsync(ct);
        try
        {
            var plan = restore ? ReadPlan(id) : _pending?.Id == id ? _pending : throw new InvalidOperationException("更新校验已失效，请重新选择更新包。");
            lock (_requests)
            {
                if (_scheduled) throw new InvalidOperationException("更新已经开始。");
                if (_writers > 0 || stats.Queued > 0 || stats.InFlight > 0) throw new InvalidOperationException("当前还有操作或翻译任务正在执行，请关闭游戏并等待任务完成后更新。");
                _scheduled = true;
            }
            try
            {
                var session = UpdateInstaller.Session(plan);
                var runner = Path.Combine(session, "runner");
                if (!restore)
                {
                    var manifest = await UpdatePackage.ValidateAsync(Path.Combine(session, "package.zip"), plan.CurrentVersion, ct);
                    if (UpdatePackage.Hash(Path.Combine(session, "package.zip")) != plan.PackageHash) throw new InvalidDataException("更新包发生变化，请重新校验。");
                    var runtimeLayout = UpdatePackage.DetectLayout(_root) == "runtime";
                    var runnerFiles = runtimeLayout ? Directory.GetFiles(Path.Combine(_root, "runtime"), "*", SearchOption.AllDirectories).Append(Path.Combine(_root, "RikaUpdater.exe")).ToArray() : Directory.GetFiles(_root, "*.dll").Concat(new[] { "RikaUpdater.exe", "RikaUpdater.deps.json", "RikaUpdater.runtimeconfig.json" }.Select(n => Path.Combine(_root, n))).ToArray();
                    if (runnerFiles.Any(f => !File.Exists(f))) throw new InvalidOperationException("独立更新程序不完整，请先解压完整便携版。");
                    var oldBytes = plan.CreateBackup ? manifest.Files.Concat(manifest.RemoveFiles).Sum(f => File.Exists(UpdatePackage.Target(_root, f.Path)) ? new FileInfo(UpdatePackage.Target(_root, f.Path)).Length : 0L) : 0L;
                    var required = manifest.Files.Sum(f => f.Size) + oldBytes + runnerFiles.Sum(f => new FileInfo(f).Length) + 64L * 1024 * 1024;
                    if (new DriveInfo(Path.GetPathRoot(_data)!).AvailableFreeSpace < required) throw new IOException("数据目录所在磁盘空间不足，无法准备更新文件。");
                    if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < manifest.Files.Max(f => f.Size) + 64L * 1024 * 1024) throw new IOException("程序所在磁盘空间不足。");
                    UpdatePackage.RejectLinks(runner); Directory.CreateDirectory(runner);
                    foreach (var file in runnerFiles) { var target = UpdatePackage.Target(runner, Path.GetRelativePath(_root, file).Replace('\\', '/')); UpdatePackage.RejectLinks(file); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true); }
                }
                else
                {
                    if (!plan.CreateBackup) throw new InvalidOperationException("此更新没有旧程序备份，请使用完整程序包修复。");
                    var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(session, "journal.json")), UpdatePackage.Json);
                    if (journal?.State is not ("applying" or "rollback-failed")) throw new InvalidOperationException("此任务不需要恢复。");
                }
                using var current = Process.GetCurrentProcess();
                plan.ParentProcessId = current.Id; plan.ParentStartTimeUtcTicks = current.StartTime.ToUniversalTime().Ticks;
                var planPath = Path.Combine(session, "plan.json"); UpdatePackage.WriteJson(planPath, plan);
                var start = new ProcessStartInfo(Path.Combine(runner, "RikaUpdater.exe")) { UseShellExecute = false, WorkingDirectory = runner, CreateNoWindow = _server };
                start.ArgumentList.Add("--plan"); start.ArgumentList.Add(planPath);
                if (_server) start.ArgumentList.Add("--quiet");
                if (restore) start.ArgumentList.Add("--restore");
                using var process = Process.Start(start) ?? throw new IOException("无法启动独立更新程序。");
                try
                {
                    var readyPath = Path.Combine(session, "runner-ready.json");
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    bool ready = false;
                    while (DateTime.UtcNow < deadline && !process.HasExited)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (File.Exists(readyPath))
                        {
                            var marker = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(readyPath));
                            if (marker.GetProperty("id").GetString() == id && marker.GetProperty("processId").GetInt32() == process.Id) { ready = true; break; }
                        }
                        await Task.Delay(150, ct);
                    }
                    if (!ready) throw new IOException("独立更新程序未能就绪，当前程序继续运行。请查看更新日志。");
                }
                catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
                _logs.Write("info", "update", restore ? "已确认恢复旧程序，将退出并交给独立更新程序。" : "已确认本地更新，将退出并交给独立更新程序。", details: new { plan.CurrentVersion, plan.TargetVersion });
            }
            catch { lock (_requests) _scheduled = false; throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task CleanupCompletedAsync(CancellationToken ct)
    {
        // Allow the independent updater to exit after starting this application.
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        var sessions = Path.Combine(_data, "updates");
        if (!Directory.Exists(sessions)) return;
        foreach (var folder in Directory.EnumerateDirectories(sessions).Take(200))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var plan = ReadPlan(Path.GetFileName(folder));
                if (plan.CreateBackup) continue;
                var journalPath = Path.Combine(folder, "journal.json"); UpdatePackage.RejectLinks(journalPath);
                if (!File.Exists(journalPath) || new FileInfo(journalPath).Length > 4 * 1024 * 1024) continue;
                var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(journalPath), UpdatePackage.Json);
                if (journal?.State != "complete") continue;
                var ready = Path.Combine(folder, "runner-ready.json"); UpdatePackage.RejectLinks(ready);
                if (File.Exists(ready))
                {
                    var pid = JsonDocument.Parse(File.ReadAllText(ready)).RootElement.GetProperty("processId").GetInt32();
                    try { using var helper = Process.GetProcessById(pid); if (!helper.HasExited) continue; }
                    catch (ArgumentException) { }
                }
                foreach (var name in new[] { "package.zip", "payload", "runner", "health" })
                    RemoveTemporary(UpdatePackage.Target(folder, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException) { }
        }
    }

    internal static void RemoveTemporary(string path)
    {
        UpdatePackage.RejectLinks(path);
        if (File.Exists(path)) { File.Delete(path); return; }
        if (!Directory.Exists(path)) return;
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) RemoveTemporary(child);
        Directory.Delete(path, false);
    }

    private UpdatePlan ReadPlan(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new InvalidDataException("更新任务标识无效。");
        var path = Path.Combine(_data, "updates", id, "plan.json"); UpdatePackage.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new InvalidDataException("没有找到更新任务记录。");
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path), UpdatePackage.Json) ?? throw new InvalidDataException("更新任务记录无效。");
        if (plan.Id != id || !Path.GetFullPath(plan.TargetDirectory).Equals(_root, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(plan.DataDirectory).Equals(_data, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新记录不属于当前程序。");
        return plan;
    }
}
