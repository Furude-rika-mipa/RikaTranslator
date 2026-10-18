using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit;

public sealed class ComponentCleanupItem
{
    public string Path { get; set; } = "";
    public string Area { get; set; } = "game";
    public string Action { get; set; } = "keep";
    public string Reason { get; set; } = "";
    public long Bytes { get; set; }
    public bool Conflict { get; set; }
    public bool Archive { get; set; }
}

public sealed class ComponentCleanupPreview
{
    public string Fingerprint { get; set; } = "";
    public bool HasManifest { get; set; }
    public bool ActiveInstallation { get; set; }
    public bool CanClean { get; set; }
    public bool CanDeleteBackups { get; set; }
    public bool ResetRecord { get; set; }
    public string Message { get; set; } = "";
    public List<ComponentCleanupItem> Items { get; set; } = [];
    public int RemoveCount => Items.Count(item => item.Action == "remove");
    public int RestoreCount => Items.Count(item => item.Action == "restore");
    public int BackupCount => Items.Count(item => item.Action == "delete-backup");
    public int ConflictCount => Items.Count(item => item.Conflict);
}

public sealed class ComponentCleanupResult
{
    public int Removed { get; set; }
    public int Restored { get; set; }
    public int BackupsDeleted { get; set; }
    public int RemainingConflicts { get; set; }
    public List<string> Messages { get; set; } = [];
    public string AdapterStatus { get; set; } = "not-installed";
}

public sealed partial class EngineService
{
    private sealed record CleanupObservation(string Area, string Path, string? Hash, long Bytes);
    private sealed class CleanupSnapshot
    {
        public ComponentCleanupPreview Preview = new();
        public InstallationManifest? Manifest;
        public string Directory = "";
        public List<CleanupObservation> Observations = [];
    }

    public ComponentCleanupPreview CleanupPreview(GameRecord game) => ReadCleanupSnapshot(game).Preview;

    private CleanupSnapshot ReadCleanupSnapshot(GameRecord game)
    {
        var snapshot = new CleanupSnapshot();
        var preview = snapshot.Preview;
        try
        {
            var directory = snapshot.Directory = ManifestDirectory(game.Id);
            var manifestPath = SafeTarget(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                preview.Message = "没有本工具的安装清单，不能确认组件归属。其他工具安装的组件、游戏文件和本地资料将保留。";
                return snapshot;
            }
            var manifestBytes = ReadFileBounded(manifestPath, 4 * 1024 * 1024);
            var manifest = JsonSerializer.Deserialize<InstallationManifest>(manifestBytes, Json) ?? throw new InvalidOperationException("安装清单为空。");
            if (manifest.GameId != game.Id || !Path.GetFullPath(manifest.Root).Equals(Path.GetFullPath(game.Directory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("游戏路径或标识与安装清单不一致，请恢复原路径后再处理。");
            if (manifest.State is not ("installed" or "installing" or "font-updating" or "removing" or "conflict" or "removed" or "rolled-back") ||
                manifest.Files == null || manifest.Files.Count is < 1 or > 5000 || manifest.CreatedDirectories == null)
                throw new InvalidOperationException("安装清单的状态或文件列表无效。");
            if (!System.IO.Directory.Exists(game.Directory)) throw new InvalidOperationException("游戏目录不存在，原始备份将保留。请先确认游戏位置。");
            RejectLinks(game.Directory, game.Directory);
            RejectOtherCleanupOwners(game, directory);
            snapshot.Manifest = manifest; preview.HasManifest = true;
            var inactive = manifest.State is "removed" or "rolled-back";
            var allInstalled = manifest.State == "installed";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var relative = file.RelativePath.Replace('\\', '/');
                if (!seen.Add(relative) || !Regex.IsMatch(file.InstalledHash ?? "", "^[A-Fa-f0-9]{64}$") ||
                    file.Existed && !Regex.IsMatch(file.OriginalHash ?? "", "^[A-Fa-f0-9]{64}$"))
                    throw new InvalidOperationException("安装清单包含重复路径或无效校验信息。");
                var target = SafeTarget(manifest.Root, relative);
                if (Path.GetFullPath(target).Equals(Path.GetFullPath(game.ExecutablePath), StringComparison.OrdinalIgnoreCase) ||
                    relative.Split('/').Any(part => part.Equals("save", StringComparison.OrdinalIgnoreCase) || part.Equals("saves", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("清单指向游戏主程序或存档，已停止清理。");
                if (System.IO.Directory.Exists(target)) throw new InvalidOperationException("清单中的文件已变成目录，已停止清理。");
                var current = ObserveCleanupFile(snapshot, "game", relative, target);
                var item = new ComponentCleanupItem { Path = relative, Bytes = current.Bytes };
                var original = file.Existed && SameCleanupHash(current.Hash, file.OriginalHash);
                var known = current.Hash != null && (SameCleanupHash(current.Hash, file.InstalledHash) || (file.SupersededHashes ?? []).Any(hash => SameCleanupHash(hash, current.Hash)));
                // XUnity writes defaults to this exact configuration during normal use.
                // Preserve those bytes before restoring or removing the owned config.
                var mutable = IsUnityRuntimeConfig(manifest, file) && current.Hash != null && current.Bytes <= 1024 * 1024;
                if (!known && !(IsUnityRuntimeConfig(manifest, file) && current.Hash != null)) allInstalled = false;
                if (file.Existed && original)
                {
                    item.Action = "original"; item.Reason = "已是安装前的原文件，保留。";
                }
                else if (current.Hash != null && !known && !mutable)
                {
                    item.Reason = "文件与安装清单不符，保留外部修改。";
                    item.Conflict = !inactive || !file.Existed;
                }
                else if (file.Existed)
                {
                    if (file.BackupFile == null) { item.Conflict = true; item.Reason = "缺少原始备份路径，无法恢复。"; }
                    else
                    {
                        var backupRelative = file.BackupFile.Replace('\\', '/');
                        if (!IsCleanupBackupPath(backupRelative)) throw new InvalidOperationException("原始备份路径不符合本工具格式。");
                        var backup = ObserveCleanupFile(snapshot, "backup-source", backupRelative, SafeTarget(directory, backupRelative));
                        if (SameCleanupHash(backup.Hash, file.OriginalHash)) { item.Action = "restore"; item.Archive = mutable && !known; item.Reason = item.Archive ? "运行时配置先归档，再用已校验的原始备份恢复。" : "使用已校验的原始备份恢复。"; }
                        else { item.Conflict = true; item.Reason = "原始备份缺失或损坏，当前文件保留。"; }
                    }
                }
                else if (known || mutable) { item.Action = "remove"; item.Archive = mutable && !known; item.Reason = item.Archive ? "本工具的运行时配置先归档，再移除。" : "本工具新增，内容与安装校验值一致。"; }
                else { item.Action = "absent"; item.Reason = "已不存在，无需清理。"; }
                preview.Items.Add(item);
            }
            if (manifest.Adapter == "renpy-text-filter" && manifest.Files.Any(file => file.RelativePath.Replace('\\', '/') == "game/zz_game_translate_toolkit.rpy"))
            {
                const string relative = "game/zz_game_translate_toolkit.rpyc";
                var target = SafeTarget(manifest.Root, relative);
                if (File.Exists(target))
                {
                    var compiled = ObserveCleanupFile(snapshot, "game", relative, target);
                    preview.Items.Add(new() { Path = relative, Action = "remove", Archive = true, Bytes = compiled.Bytes, Reason = "本工具脚本对应的编译缓存先归档，再移除。" });
                }
            }
            preview.ActiveInstallation = allInstalled;
            ReadCleanupBackups(snapshot);
            preview.CanDeleteBackups = !allInstalled && preview.ConflictCount == 0 && preview.BackupCount > 0;
            preview.ResetRecord = !inactive && !allInstalled && preview.ConflictCount == 0;
            preview.CanClean = !allInstalled && preview.ConflictCount == 0 && (preview.RemoveCount + preview.RestoreCount > 0 || preview.CanDeleteBackups || preview.ResetRecord);
            preview.Message = allInstalled ? "适配仍完整安装，请先使用“卸载与恢复”。正常安装不会被当作残留删除。"
                : preview.ConflictCount > 0 ? "发现外部修改或原始备份缺失，暂不执行清理。所有当前文件和备份保留，请先检查列出的待处理项。"
                : preview.CanClean ? "先退出游戏，再确认清理。将恢复原文件、移除本工具残留；未列入清单的组件、译文、字体和运行期归档保留。"
                : "未发现可清理的组件或备份。未列入安装清单的文件、译文和运行期归档保留。";
            preview.Fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(new { game.Id, game.Directory, manifest = Hash(manifestBytes), snapshot.Observations, preview.ActiveInstallation, preview.CanDeleteBackups }, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            preview.CanClean = preview.CanDeleteBackups = false;
            preview.Message = "无法安全确认清理范围：" + ex.Message;
            preview.Fingerprint = "";
        }
        return snapshot;
    }

    private static bool SameCleanupHash(string? a, string? b) => a != null && b != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private void RejectOtherCleanupOwners(GameRecord game, string ownDirectory)
    {
        var count = 0;
        foreach (var directory in System.IO.Directory.EnumerateDirectories(_installRoot))
        {
            if (++count > 5000) throw new InvalidOperationException("安装记录过多，请先检查同目录的其他安装。");
            if (directory.Equals(ownDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path)) continue;
            InstallationManifest? other;
            try { RejectLinks(_installRoot, path); other = JsonSerializer.Deserialize<InstallationManifest>(ReadFileBounded(path, 4 * 1024 * 1024), Json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { continue; }
            if (other?.State is "removed" or "rolled-back" || string.IsNullOrEmpty(other?.Root)) continue;
            string otherRoot;
            try { otherRoot = Path.GetFullPath(other.Root); } catch (ArgumentException) { continue; }
            if (otherRoot.Equals(Path.GetFullPath(game.Directory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("同一个游戏目录仍有其他条目的安装或恢复记录。请先处理该条目，避免清理仍在使用的组件和备份。");
        }
    }
    private static bool IsCleanupBackupPath(string relative) => Regex.IsMatch(relative, "^backups/[a-fA-F0-9]{32}/[0-9]{4}\\.bak$");
    private static CleanupObservation ObserveCleanupFile(CleanupSnapshot snapshot, string area, string relative, string fullPath)
    {
        var length = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
        if (length > 64 * 1024 * 1024) throw new InvalidOperationException("文件超过清理校验上限，已保留：" + relative);
        var observation = new CleanupObservation(area, relative, File.Exists(fullPath) ? FileHash(fullPath) : null, length);
        snapshot.Observations.Add(observation); return observation;
    }
    private static void ReadCleanupBackups(CleanupSnapshot snapshot)
    {
        var root = SafeTarget(snapshot.Directory, "backups");
        if (!System.IO.Directory.Exists(root)) return;
        var pending = new Queue<string>(); pending.Enqueue(root); var count = 0;
        while (pending.TryDequeue(out var folder))
        {
            RejectLinks(root, folder);
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (++count > 5000) throw new InvalidOperationException("备份项目过多，请先手动检查。");
                RejectLinks(root, entry);
                if (!IsInside(root, entry)) throw new InvalidOperationException("备份目录越界。");
                var relative = Path.GetRelativePath(snapshot.Directory, entry).Replace('\\', '/');
                if (System.IO.Directory.Exists(entry))
                {
                    if (Regex.IsMatch(relative, "^backups/[a-fA-F0-9]{32}$")) pending.Enqueue(entry);
                    else snapshot.Preview.Items.Add(new() { Path = relative, Area = "backup", Reason = "非标准备份目录，保留。" });
                    continue;
                }
                var observed = ObserveCleanupFile(snapshot, "backup", relative, entry);
                snapshot.Preview.Items.Add(new() { Path = relative, Area = "backup", Bytes = observed.Bytes,
                    Action = IsCleanupBackupPath(relative) ? "delete-backup" : "keep",
                    Reason = IsCleanupBackupPath(relative) ? "本游戏的原始文件备份；仅在恢复完成并勾选删除后移除。" : "非标准备份文件，保留。" });
            }
        }
    }

    public async Task<ComponentCleanupResult> CleanupAsync(GameRecord game, string fingerprint, bool deleteBackups, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(fingerprint ?? "", "^[A-Fa-f0-9]{64}$")) throw new ArgumentException("请先扫描残留组件，再确认清理。");
        var gate = _locks.GetOrAdd(Path.GetFullPath(game.Directory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            EnsureStopped(game.Directory);
            var snapshot = ReadCleanupSnapshot(game); var plan = snapshot.Preview;
            if (!plan.HasManifest || !plan.CanClean || snapshot.Manifest == null) throw new InvalidOperationException(plan.Message);
            if (!SameCleanupHash(plan.Fingerprint, fingerprint)) throw new InvalidOperationException("文件或备份已发生变化，请重新扫描并确认，尚未执行清理。");
            if (deleteBackups && !plan.CanDeleteBackups) throw new InvalidOperationException("仍有未解决的文件或适配尚在使用，不能删除原始备份。");
            if (!deleteBackups && plan.RemoveCount + plan.RestoreCount == 0 && !plan.ResetRecord) throw new InvalidOperationException("没有需要清理的组件。原始备份默认保留，需勾选后才能删除。");
            var manifest = snapshot.Manifest; var result = new ComponentCleanupResult();
            var wasInactive = manifest.State is "removed" or "rolled-back";
            foreach (var item in plan.Items.Where(item => item.Action is "restore" or "remove").Reverse())
            {
                ct.ThrowIfCancellationRequested();
                var target = SafeTarget(manifest.Root, item.Path);
                var expected = snapshot.Observations.First(observed => observed.Area == "game" && observed.Path == item.Path);
                if (!MatchesCleanupObservation(expected, target)) throw new InvalidOperationException("清理期间文件改变，已停止并保留剩余文件：" + item.Path);
                var file = manifest.Files.FirstOrDefault(file => file.RelativePath.Replace('\\', '/') == item.Path);
                if (item.Archive)
                {
                    var bytes = ReadFileBounded(target, 64 * 1024 * 1024);
                    if (!SameCleanupHash(Hash(bytes), expected.Hash)) throw new InvalidOperationException("运行期文件已改变，已停止清理。");
                    var relative = $"generated/cleanup-{Guid.NewGuid():N}/{Path.GetFileName(target)}";
                    var archive = SafeTarget(snapshot.Directory, relative);
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(archive)!); AtomicWrite(archive, bytes);
                    manifest.GeneratedArchives ??= []; manifest.GeneratedArchives.Add(relative); SaveManifest(snapshot.Directory, manifest);
                    if (!MatchesCleanupObservation(expected, target)) throw new InvalidOperationException("运行期文件在归档后改变，已保留当前文件。");
                }
                if (item.Action == "restore")
                {
                    if (file == null) throw new InvalidOperationException("清单缺少要恢复的原文件。");
                    var backup = SafeTarget(snapshot.Directory, file.BackupFile!);
                    var original = ReadFileBounded(backup, 64 * 1024 * 1024);
                    if (!SameCleanupHash(Hash(original), file.OriginalHash)) throw new InvalidOperationException("原始备份发生变化，已停止恢复。");
                    var parent = Path.GetDirectoryName(target)!;
                    RejectLinks(manifest.Root, parent); System.IO.Directory.CreateDirectory(parent);
                    AtomicWrite(target, original); result.Restored++;
                }
                else { File.Delete(target); result.Removed++; }
                if (file != null) file.Applied = false;
                manifest.State = wasInactive ? "removed" : "conflict"; SaveManifest(snapshot.Directory, manifest);
            }
            // Re-read observations after the file operations before declaring recovery
            // complete or allowing deletion of any original-file backups.
            var remaining = ReadCleanupSnapshot(game).Preview;
            if (!remaining.HasManifest || string.IsNullOrEmpty(remaining.Fingerprint)) throw new InvalidOperationException("清理后无法复核文件，原始备份已保留，请重新扫描。");
            result.RemainingConflicts = remaining.ConflictCount + remaining.RemoveCount + remaining.RestoreCount;
            manifest.Conflicts = remaining.Items.Where(item => item.Conflict || item.Action is "remove" or "restore").Select(item => item.Path).ToList();
            manifest.State = result.RemainingConflicts == 0 ? "removed" : "conflict";
            SaveManifest(snapshot.Directory, manifest);
            if (deleteBackups)
            {
                if (result.RemainingConflicts != 0 || remaining.RemoveCount + remaining.RestoreCount != 0)
                    throw new InvalidOperationException("恢复尚未完成，原始备份已保留。");
                foreach (var item in plan.Items.Where(item => item.Action == "delete-backup"))
                {
                    ct.ThrowIfCancellationRequested();
                    var backup = SafeTarget(snapshot.Directory, item.Path);
                    var expected = snapshot.Observations.First(observed => observed.Area == "backup" && observed.Path == item.Path);
                    if (!MatchesCleanupObservation(expected, backup)) throw new InvalidOperationException("备份已改变，已停止删除剩余备份。");
                    File.Delete(backup); result.BackupsDeleted++;
                }
                RemoveEmptyCleanupBackupDirectories(snapshot.Directory);
            }
            RemoveEmptyParents(manifest);
            game.AdapterStatus = result.AdapterStatus = AdapterStatus(game);
            if (game.AdapterStatus == "not-installed") game.BridgeToken = "";
            result.Messages.Add(result.RemainingConflicts == 0 ? "可确认的残留已清理；游戏文件、存档、译文和未归属的组件保留。" : "已清理可确认的内容；部分文件需要手动处理，备份已保留。" );
            _logs.Write("info", "installation", "残留组件清理完成。", game.Id, details: new { result.Removed, result.Restored, result.BackupsDeleted, result.RemainingConflicts });
            return result;
        }
        finally { gate.Release(); }
    }
    private static bool MatchesCleanupObservation(CleanupObservation observation, string path) =>
        observation.Hash == null ? !File.Exists(path) && !System.IO.Directory.Exists(path) : File.Exists(path) && SameCleanupHash(observation.Hash, FileHash(path));
    private static void RemoveEmptyCleanupBackupDirectories(string directory)
    {
        var root = SafeTarget(directory, "backups");
        if (!System.IO.Directory.Exists(root)) return;
        foreach (var folder in System.IO.Directory.EnumerateDirectories(root))
        {
            RejectLinks(root, folder);
            if (IsInside(root, folder) && Regex.IsMatch(Path.GetFileName(folder), "^[a-fA-F0-9]{32}$") && !System.IO.Directory.EnumerateFileSystemEntries(folder).Any()) System.IO.Directory.Delete(folder, false);
        }
        RejectLinks(directory, root);
        if (!System.IO.Directory.EnumerateFileSystemEntries(root).Any()) System.IO.Directory.Delete(root, false);
    }
}
