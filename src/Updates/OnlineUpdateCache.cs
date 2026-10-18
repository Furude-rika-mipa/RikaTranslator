using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit.Updates;

public sealed partial class OnlineUpdateService
{
    private sealed class SavedOnlineUpdate
    {
        public int FormatVersion { get; set; } = 1;
        public string Repository { get; set; } = OnlineUpdateService.Repository;
        public string SessionId { get; set; } = "";
        public string PackageHash { get; set; } = "";
        public OnlineRelease? Release { get; set; }
    }

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _initialization;
    private SavedOnlineUpdate? _saved;
    private string CacheRecord => Path.Combine(_data, "updates", "online-pending.json");

    // This only reads local state. Opening the app never checks GitHub or installs.
    public Task InitializeAsync(CancellationToken ct)
    {
        Task initialization;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OnlineUpdateService));
            if (_initialization == null)
            {
                _busy = true; _stage = "verifying";
                _initialization = Task.Run(() => RestoreCacheAsync(_shutdown.Token));
            }
            initialization = _initialization;
        }
        return initialization.WaitAsync(ct);
    }

    private async Task RestoreCacheAsync(CancellationToken ct)
    {
        SavedOnlineUpdate? saved = null;
        try
        {
            UpdatePackage.RejectLinks(CacheRecord);
            if (File.Exists(CacheRecord))
            {
                if (new FileInfo(CacheRecord).Length > 256 * 1024) throw new InvalidDataException("更新缓存记录过大。");
                await using (var input = new FileStream(CacheRecord, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                    saved = await JsonSerializer.DeserializeAsync<SavedOnlineUpdate>(input, UpdatePackage.Json, ct)
                        ?? throw new InvalidDataException("更新缓存记录为空。");
                ValidateSaved(saved);
                if (UpdatePackage.ParseVersion(saved.Release!.Version) <= UpdatePackage.ParseVersion(_current))
                    await DropSavedAsync(saved, ct);
                else
                {
                    var preview = await _updates.RestorePendingAsync(saved.SessionId, saved.PackageHash, ct);
                    if (preview.TargetVersion != saved.Release.Version) throw new InvalidDataException("更新缓存版本不一致。");
                    ct.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        _saved = saved; _release = saved.Release; _preview = preview;
                        _downloaded = saved.Release.Size; _stage = "ready";
                    }
                    _logs.Write("info", "update", "已校验并恢复下载完成的更新，可直接安装。", details: new { preview.TargetVersion });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closing while verifying must preserve the durable record for next launch.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or JsonException or ArgumentException or FormatException or OverflowException)
        {
            try { await DropSavedAsync(saved, CancellationToken.None); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidDataException)
            { _logs.Write("warning", "update", "更新缓存暂时无法清理，请检查更新目录权限。"); }
            lock (_gate) _error = "之前下载的更新已失效或校验失败，请重新检查并下载。";
            _logs.Write("warning", "update", "已下载更新无法恢复，未安装或更改程序。");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                try { await CleanupOldDownloadsAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { _logs.Write("warning", "update", "部分过期更新缓存暂时无法清理。"); }
            lock (_gate) { _busy = false; if (_stage == "verifying") _stage = "idle"; }
        }
    }

    private static void ValidateSaved(SavedOnlineUpdate saved)
    {
        var release = saved.Release;
        if (saved.FormatVersion != 1 || saved.Repository != Repository ||
            !Regex.IsMatch(saved.SessionId ?? "", "^[a-f0-9]{32}$") ||
            !Regex.IsMatch(saved.PackageHash ?? "", "^[a-f0-9]{64}$") || release == null ||
            release.Id == null || !Regex.IsMatch(release.Id, "^[a-f0-9]{32}$") ||
            release.Version != TagVersion(release.Tag) ||
            release.FileName != $"RikaTranslator-{release.Version}-win-x64.zip" ||
            !Regex.IsMatch(release.Sha256 ?? "", "^[a-f0-9]{64}$") || release.Notes == null || release.Notes.Length > 20000)
            throw new InvalidDataException("更新缓存记录格式无效。");
        CheckSize(release.Size);
    }

    private async Task SaveCacheAsync(OnlineRelease release, UpdatePreview preview, CancellationToken ct)
    {
        var hash = await _updates.SavePendingAsync(preview.Id, ct);
        var saved = new SavedOnlineUpdate { SessionId = preview.Id, PackageHash = hash, Release = release };
        ValidateSaved(saved);
        ct.ThrowIfCancellationRequested();
        UpdatePackage.WriteJson(CacheRecord, saved);
        // Writing this atomic record is the commit point. A later close preserves it.
        lock (_gate) _saved = saved;
    }

    private async Task DropSavedAsync(SavedOnlineUpdate? saved, CancellationToken ct)
    {
        if (saved != null && Regex.IsMatch(saved.SessionId ?? "", "^[a-f0-9]{32}$"))
        {
            try { await _updates.DiscardSavedPendingAsync(saved.SessionId!, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                // The record cannot authorize touching another installation or a repair/backup task.
                _logs.Write("warning", "update", "已失效的缓存记录已忽略，无法确认归属的任务文件未更改。");
            }
        }
        foreach (var file in new[] { CacheRecord, CacheRecord + ".tmp" })
        {
            UpdatePackage.RejectLinks(file);
            if (File.Exists(file)) File.Delete(file);
        }
        lock (_gate) { _saved = null; _preview = null; _release = null; }
    }

    private static bool SamePackage(OnlineRelease first, OnlineRelease second) =>
        first.Version == second.Version && first.Tag == second.Tag && first.FileName == second.FileName &&
        first.Size == second.Size && first.Sha256 == second.Sha256;

    private async Task CleanupOldDownloadsAsync(CancellationToken ct)
    {
        var updates = Path.Combine(_data, "updates"); UpdatePackage.RejectLinks(updates);
        if (!Directory.Exists(updates)) return;
        // Pre-0.7.9 unconfirmed packages had no saved state. Only remove old, known
        // cache-only layouts; never touch journals, recovery files or program backups.
        foreach (var folder in Directory.EnumerateDirectories(updates).Take(200))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(folder);
            if (!Regex.IsMatch(id, "^[a-f0-9]{32}$") || id == _saved?.SessionId) continue;
            UpdatePackage.RejectLinks(folder);
            var entries = Directory.EnumerateFileSystemEntries(folder).ToArray();
            if (entries.Length == 0 || entries.Any(p => Directory.Exists(p) ||
                Path.GetFileName(p) is not ("package.zip" or "plan.json"))) continue;
            if (entries.Any(p => File.GetLastWriteTimeUtc(p) >= DateTime.UtcNow.AddDays(-7))) continue;
            if (File.Exists(Path.Combine(folder, "plan.json"))) await _updates.DiscardSavedPendingAsync(id, ct);
            else
            {
                var package = Path.Combine(folder, "package.zip"); UpdatePackage.RejectLinks(package);
                if (File.Exists(package)) File.Delete(package);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false);
            }
        }
        var downloads = Path.Combine(updates, "downloads"); UpdatePackage.RejectLinks(downloads);
        if (!Directory.Exists(downloads)) return;
        foreach (var folder in Directory.EnumerateDirectories(downloads).Take(200))
        {
            ct.ThrowIfCancellationRequested();
            if (!Regex.IsMatch(Path.GetFileName(folder), "^[a-f0-9]{32}$")) continue;
            UpdatePackage.RejectLinks(folder);
            var entries = Directory.EnumerateFileSystemEntries(folder).ToArray();
            if (entries.Any(p => Directory.Exists(p) || Path.GetFileName(p) is not ("release.zip" or "prepared.zip") ||
                File.GetLastWriteTimeUtc(p) >= DateTime.UtcNow.AddDays(-1))) continue;
            foreach (var file in entries) { UpdatePackage.RejectLinks(file); File.Delete(file); }
            Directory.Delete(folder, false);
        }
    }
}
