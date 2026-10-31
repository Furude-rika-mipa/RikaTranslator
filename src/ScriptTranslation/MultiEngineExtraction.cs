using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit.ScriptTranslation;

public sealed partial class ScriptTranslationService
{
    private sealed record ScriptArchive(string Name, long Bytes);
    private sealed class ScriptReadResult
    {
        public string Engine { get; set; } = "";
        public string Format { get; set; } = "";
        public int Resources { get; set; }
        public int ScriptFiles { get; set; }
        public int Scenes { get; set; }
        public List<ScriptSegment> Segments { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
    }

    private static string? MultiEngine(GameRecord game)
    {
        if (game.Engine is "WillPlus" or "YU-RIS" or "SiglusEngine" or "ExHIBIT" or "Escu:de" or "BGI / Ethornell") return game.Engine;
        if (game.Engine == "Unknown")
        {
            if (File.Exists(Path.Combine(game.Directory, "ExHIBIT.ini")) && Directory.Exists(Path.Combine(game.Directory, "rld"))) return "ExHIBIT";
            var script = Path.Combine(game.Directory, "script.bin");
            if (File.Exists(script))
            {
                UpdatePackage.RejectLinks(script);
                using var stream = File.OpenRead(script); Span<byte> header = stackalloc byte[8];
                if (stream.Read(header) == 8 && header.SequenceEqual("ESC-ARC2"u8)) return "Escu:de";
            }
        }
        return null;
    }

    private static List<ScriptArchive> MultiArchives(GameRecord game, string engine)
    {
        UpdatePackage.RejectLinks(game.Directory); UpdatePackage.RejectLinks(game.ExecutablePath);
        if (!File.Exists(game.ExecutablePath)) throw new InvalidOperationException("游戏启动程序不存在。");
        var paths = engine switch
        {
            "WillPlus" => Directory.EnumerateFiles(game.Directory, "*.arc").Where(p => Path.GetFileNameWithoutExtension(p).Equals("Scn", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(p).Equals("Rio", StringComparison.OrdinalIgnoreCase)),
            "YU-RIS" => Directory.Exists(Path.Combine(game.Directory, "pac")) ? Directory.EnumerateFiles(Path.Combine(game.Directory, "pac"), "*.ypf").Where(p => Path.GetFileNameWithoutExtension(p).Equals("Scripts", StringComparison.OrdinalIgnoreCase)) : [],
            "Escu:de" => new[] { Path.Combine(game.Directory, "script.bin") }.Where(File.Exists),
            "SiglusEngine" => new[] { Path.Combine(game.Directory, "Scene.pck") }.Where(File.Exists),
            "ExHIBIT" => [],
            "BGI / Ethornell" => BgiScriptFiles(game),
            _ => Enumerable.Empty<string>()
        };
        var result = new List<ScriptArchive>();
        foreach (var file in paths.Take(100))
        {
            UpdatePackage.RejectLinks(file); var bytes = new FileInfo(file).Length;
            if (bytes is > 0 and <= 64 * 1024 * 1024)
                result.Add(new(Path.GetRelativePath(game.Directory, file).Replace('\\', '/'), bytes));
        }
        if (engine == "ExHIBIT")
        {
            var directory = Path.Combine(game.Directory, "rld"); UpdatePackage.RejectLinks(directory);
            if (Directory.Exists(directory))
            {
                var files = Directory.EnumerateFiles(directory, "*.rld").Take(10001).ToArray();
                if (files.Length > 10000) throw new InvalidDataException("RLD 剧本数量超出范围。");
                long bytes = 0;
                foreach (var file in files) { UpdatePackage.RejectLinks(file); bytes = checked(bytes + new FileInfo(file).Length); }
                if (files.Length > 0 && bytes <= 512L * 1024 * 1024) result.Add(new("rld", bytes));
            }
        }
        if (engine == "BGI / Ethornell" && result.Count > 0)
            result.Add(new("Archive", result.Sum(a => a.Bytes)));
        return result.OrderBy(a => a.Name.Equals("Scn.arc", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(a => a.Name).ToList();
    }

    private static string[] BgiScriptFiles(GameRecord game)
    {
        var directory = Path.Combine(game.Directory, "Archive"); UpdatePackage.RejectLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var files = Directory.EnumerateFiles(directory, "data01*.arc").Take(101).ToArray();
        if (files.Length > 100) throw new InvalidDataException("BGI 剧本资源包超过当前读取范围。");
        foreach (var file in files) UpdatePackage.RejectLinks(file);
        return files;
    }

    private object InspectMultiEngine(GameRecord game, string engine)
    {
        var archives = MultiArchives(game, engine);
        return new { supported = archives.Count > 0, engine, archives, offline = true,
            message = archives.Count > 0 ? $"{engine}：离线读取剧本，无需启动游戏；提取后可用 AI / MCP 翻译，并将已完成译文生成独立汉化补丁。具体资源版本需通过写回检查。" : "没有找到当前适配的剧本资源，未开始 AI 翻译。" };
    }

    private Dictionary<string, string> MultiInputHashes(GameRecord game, string engine, string archive)
    {
        IEnumerable<string> files = engine == "BGI / Ethornell" && archive == "Archive" ? BgiScriptFiles(game)
            : archive == "rld" ? Directory.EnumerateFiles(Path.Combine(game.Directory, "rld"), "*.rld").Take(10001)
            : [Path.Combine(game.Directory, archive.Replace('/', Path.DirectorySeparatorChar))];
        files = files.Append(game.ExecutablePath);
        if (engine == "ExHIBIT") files = files.Append(Path.Combine(game.Directory, "ExHIBIT.ini"));
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            UpdatePackage.RejectLinks(file);
            hashes.Add(file, UpdatePackage.Hash(file));
        }
        return hashes;
    }

    private string VerifyMultiReader()
    {
        var root = Path.Combine(_application, "adapters", "script-extraction", "multi");
        var manifest = Path.Combine(root, "component-manifest.json"); UpdatePackage.RejectLinks(manifest);
        var file = Path.Combine(root, "RikaScriptReader.exe"); UpdatePackage.RejectLinks(file);
        if (!File.Exists(file) || !File.Exists(manifest)) throw new InvalidDataException("多引擎剧本读取组件不完整，请使用完整程序目录。");
        if (new FileInfo(manifest).Length > 16384) throw new InvalidDataException("多引擎组件清单无效。");
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(manifest), UpdatePackage.Json);
        if (expected?.Count != 1 || !expected.TryGetValue("RikaScriptReader.exe", out var hash) || !UpdatePackage.Hash(file).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("多引擎剧本读取组件校验失败，请重新安装完整程序。");
        return file;
    }

    private object ExtractMultiEngine(GameRecord game, string engine, ScriptExtractionRequest request)
    {
        var archive = MultiArchives(game, engine).FirstOrDefault(a => a.Name == request.Archive)
            ?? throw new ArgumentException("请选择检查列表里的剧本资源。");
        var worker = VerifyMultiReader();
        var app = Path.GetFullPath(_application).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(game.Directory).StartsWith(app, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(_application).StartsWith(Path.GetFullPath(game.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请将梨花和游戏分别放在独立目录。");
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptTranslationService));
            if (Busy) throw new InvalidOperationException("已有剧本任务正在运行，请先暂停或取消。");
            if (_jobs.Count >= 100) throw new InvalidOperationException("剧本任务已达到 100 个，请先整理输出目录。");
            var job = new ScriptJob { Id = Guid.NewGuid().ToString("N"), GameId = game.Id, GameName = game.Name,
                Engine = engine, Format = "multi-engine-v1", Archive = archive.Name, Message = "正在离线读取剧本…" };
            _begin();
            try
            {
                _jobs.Add(job.Id, job); Save(job); _active = job.Id;
                _stop?.Dispose(); _stop = new CancellationTokenSource();
                _work = Task.Run(() => ExtractMultiAsync(job, game, worker, _stop.Token));
                return Summary(job);
            }
            catch { _end(); throw; }
        }
    }

    private async Task ExtractMultiAsync(ScriptJob job, GameRecord game, string worker, CancellationToken ct)
    {
        Process? process = null;
        var output = Path.Combine(Folder(job.Id), "extraction.json");
        try
        {
            var inputs = MultiInputHashes(game, job.Engine, job.Archive);
            UpdatePackage.RejectLinks(output);
            var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(worker)!, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { job.Engine, game.ExecutablePath, job.Archive, output }) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(k => System.Text.RegularExpressions.Regex.IsMatch(k, "API_KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|PYTHON", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray()) start.Environment.Remove(key);
            process = new() { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("无法启动多引擎剧本读取组件。");
            lock (_gate) _worker = process;
            var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout; _ = await stderr;
            if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidDataException("此游戏的剧本读取未完成，可能使用了尚未适配的格式；未开始 AI 翻译。");
            UpdatePackage.RejectLinks(output);
            if (new FileInfo(output).Length > 64L * 1024 * 1024) throw new InvalidDataException("剧本读取结果超过大小限制。");
            foreach (var item in inputs)
                if (!File.Exists(item.Key) || UpdatePackage.Hash(item.Key) != item.Value) throw new InvalidDataException("游戏资源在读取期间发生变化，请重新提取。");
            var result = JsonSerializer.Deserialize<ScriptReadResult>(File.ReadAllBytes(output), UpdatePackage.Json) ?? throw new InvalidDataException("剧本结果无效。");
            if (result.Engine != job.Engine || result.Format != job.Format || result.Segments == null || result.Segments.Count is < 1 or > 100000 ||
                result.Segments.Any(s => s == null || string.IsNullOrWhiteSpace(s.Source) || s.Source.Length > 4096 || string.IsNullOrEmpty(s.File) || s.File.Length > 512 || s.Index < 0 || s.Speaker == null || s.Speaker.Length > 512))
                throw new InvalidDataException("剧本对白结构无效。");
            var identities = new HashSet<string>();
            foreach (var segment in result.Segments)
            {
                segment.Scene = segment.File;
                segment.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{job.Engine}\0{segment.File}\0{segment.Index}\0{segment.Source}"))).ToLowerInvariant();
                if (!identities.Add(segment.Id)) throw new InvalidDataException("剧本对白编号重复。");
                segment.Translation = ""; segment.Status = "pending"; segment.Error = "";
            }
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                job.Segments = result.Segments; job.Resources = result.Resources; job.ScriptFiles = result.ScriptFiles; job.Scenes = result.Scenes;
                job.Warnings = ["支持将本任务已完成译文回填到资源副本并生成独立补丁；中文显示和剧情跳转仍需在游戏中检查。"];
                job.Stage = "ready"; job.Message = $"已离线读取 {job.Segments.Count:N0} 条对白。请选择翻译范围；当前未选择任何条目。";
                Save(job);
            }
            _logs.Write("info", "script-translation", "多引擎剧本离线提取完成。", game.Id, details: new { engine = job.Engine, lines = job.Segments.Count, files = job.ScriptFiles });
        }
        catch (OperationCanceledException)
        { lock (_gate) { job.Stage = ct.IsCancellationRequested ? "cancelled" : "failed"; job.Message = ct.IsCancellationRequested ? "提取已取消，未开始 AI 翻译。" : "离线提取超时，未开始 AI 翻译。"; Save(job); } }
        catch (Exception ex)
        { lock (_gate) { job.Stage = "failed"; job.Message = ex is InvalidDataException ? ex.Message : "剧本离线读取失败，未开始 AI 翻译。"; Save(job); } }
        finally
        {
            if (process != null) { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch (InvalidOperationException) { } process.Dispose(); }
            lock (_gate) _worker = null;
            try { if (File.Exists(output)) { UpdatePackage.RejectLinks(output); File.Delete(output); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            finally { _end(); }
        }
    }
}
