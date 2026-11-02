using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit.ScriptTranslation;

public sealed record ScriptPatchRequest(string? Fingerprint = null, bool Confirmed = false, bool ChineseFont = false,
    string FontFamily = "Microsoft YaHei", string? FontFile = null);
public sealed record ScriptPatchPlan(string GameName, string GameExecutable, int Translated, int Total, int Remaining,
    int Files, long Bytes, string Fingerprint, bool ChineseFont, string FontFamily, string? FontFile);
public sealed record ScriptPatchOutput(string Id, string Directory, DateTimeOffset CreatedAt, int Translated, int Files, int Remaining);

public sealed partial class ScriptTranslationService
{
    private sealed record PatchFile(string Name, string SourceHash, string PayloadHash, byte[] Bytes, int Changed);
    private sealed record PatchBuild(ScriptPatchPlan Plan, string GameHash, List<PatchFile> Files, Dictionary<string, string> Components, byte[]? FontBytes,
        string? ComponentRoot = null, bool ResourcePatch = false, byte[]? CharacterMap = null);
    private static readonly string[] PatchComponents = ["RikaPatchLauncher.exe", "RikaScriptPatch.dll"];
    private string PatchComponentDirectory => Path.Combine(_application, "adapters", "script-patch", "native");
    private string PatchLicenseDirectory => Path.Combine(_application, "licenses", "ScriptPatch");

    private static byte[] PatchReadFile(string path, long max)
    {
        UpdatePackage.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > max) throw new InvalidDataException("补丁输入文件超过大小限制。");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static string PatchHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private Dictionary<string, string> VerifyPatchComponents(string? directory = null)
    {
        directory ??= PatchComponentDirectory;
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(PatchReadFile(Path.Combine(directory,
            "component-manifest.json"), 16384)) ?? throw new InvalidDataException("补丁组件清单无效。");
        if (!manifest.Keys.Order().SequenceEqual(PatchComponents.Order()) || manifest.Any(p => !Regex.IsMatch(p.Value ?? "", "\\A[a-f0-9]{64}\\z")))
            throw new InvalidDataException("补丁组件清单不完整，请重新安装完整程序。");
        foreach (var item in manifest)
            if (PatchHash(PatchReadFile(Path.Combine(directory, item.Key), 8 * 1024 * 1024)) != item.Value)
                throw new InvalidDataException("补丁组件校验失败，请重新安装完整程序。");
        // The corresponding source and notices travel with every generated binary patch.
        foreach (var name in new[] { "LICENSE", "NOTICE.md", "source/patch.cpp", "source/loader.cpp", "source/build-native.ps1" })
            _ = PatchReadFile(Path.Combine(PatchLicenseDirectory, name), 1024 * 1024);
        return manifest;
    }
    private static void CheckPatchGame(byte[] bytes)
    {
        if (bytes.Length < 64 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("游戏 EXE 格式无效。");
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60));
        if (pe < 64 || pe > bytes.Length - 24 || !bytes.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 4)) != 0x14c)
            throw new InvalidDataException("汉化补丁暂支持 32 位 KiriKiri / Hxv4 游戏。");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6));
        var sections = pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20));
        if (count is < 1 or > 96 || (long)sections + count * 40 > bytes.Length) throw new InvalidDataException("游戏 EXE 节区无效。");
        // Same stream ABI signature as the native component; reject unsupported engines before exporting.
        var signature = Convert.FromHexString("558BEC6AFF682A2A2A2A64A12A2A2A2A5083EC5C535657A12A2A2A2A33C5508D45F464A32A2A2A2A8965F0894DECC7452A2A2A2A2AE82A2A2A2A8B4DF464890D2A2A2A2A595F5E5B8BE55DC3");
        var matches = 0;
        for (var s = 0; s < count; s++)
        {
            var at = sections + s * 40;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 36)) & 0x20000000) == 0) continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 16));
            var start = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 20));
            if (start + (long)size > bytes.Length) throw new InvalidDataException("游戏 EXE 节区越界。");
            for (long i = start; i + signature.Length <= start + (long)size; i++)
            {
                var found = true;
                for (var j = 0; j < signature.Length; j++) if (signature[j] != 0x2a && bytes[(int)i + j] != signature[j]) { found = false; break; }
                if (found) matches++;
            }
        }
        if (matches != 1) throw new InvalidDataException("此游戏的剧本加载方式暂不支持补丁生成；已完成译文仍可导出 JSON / TXT。");
    }
    private PatchBuild BuildScriptPatch(ScriptJob job, ScriptPatchRequest request)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScriptTranslationService));
        if (job.Format == "multi-engine-v1") return BuildMultiPatch(job, request);
        if (job.Format != "psb3") throw new InvalidOperationException("此剧本格式尚未适配汉化补丁。");
        if (Busy || job.External?.Enabled == true) throw new InvalidOperationException("请先暂停剧本翻译或外部领取，再生成汉化补丁。");
        var game = Game(job.GameId);
        if (game.Engine != "KiriKiri") throw new InvalidOperationException("汉化补丁暂支持 KiriKiri / Hxv4 的 PSB 3 场景资源。");
        var complete = job.Segments.Where(s => s.Status == "translated").ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (complete.Count == 0) throw new InvalidOperationException("当前没有已完成译文，请先翻译部分对白。");
        var components = VerifyPatchComponents();
        byte[]? fontBytes = null; string? fontFile = null; var fontFamily = "";
        if (request.ChineseFont)
        {
            if (!string.IsNullOrEmpty(request.FontFile))
            {
                fontBytes = FontAssets.ReadSourceFont(PatchFontPath(job.Id, request.FontFile));
                if (PatchHash(fontBytes) != request.FontFile[..64]) throw new InvalidDataException("补丁字体已变化，请重新选择。");
                fontFamily = PatchFontFamily(fontBytes); fontFile = request.FontFile;
            }
            else
            {
                fontFamily = PatchFontFamily(request.FontFamily);
                if (!FontCatalog.Installed().Any(f => f.Aliases.Contains(fontFamily, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException("本机没有这个系统字体，请从列表选择或导入 TTF / OTF 文件。");
            }
        }
        var exe = PatchReadFile(game.ExecutablePath, 128L * 1024 * 1024); CheckPatchGame(exe);
        var gameName = Path.GetFileName(game.ExecutablePath);
        if (gameName.Length > 200 || gameName.Any(c => c < 32 || "/\\:\"<>|".Contains(c))) throw new InvalidDataException("游戏启动程序文件名不受支持。");
        var gameHash = PatchHash(exe);
        var raw = Path.Combine(Folder(job.Id), "Extractor_Output", "data"); UpdatePackage.RejectLinks(raw);
        if (!Directory.Exists(raw)) throw new InvalidDataException("提取的原始剧本不存在，请重新提取；已完成译文未改动。");
        var files = new List<PatchFile>(); var matched = new HashSet<string>(StringComparer.Ordinal);
        var seenHashes = new HashSet<string>(); var examined = 0; long totalBytes = 0;
        // Traverse manually so junctions are rejected before descending into them.
        var pending = new Stack<string>(); pending.Push(raw); var magic = new byte[4];
        while (pending.TryPop(out var directory))
        {
            UpdatePackage.RejectLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if (++examined > 10000) throw new InvalidDataException("提取资源数量超过补丁检查限制。");
                UpdatePackage.RejectLinks(entry);
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                using (var input = File.OpenRead(entry))
                {
                    if (input.Length < 8 || input.Length > PsbScenarioReader.MaxBytes) continue;
                    input.ReadExactly(magic);
                    if (!magic.AsSpan().SequenceEqual("mdf\0"u8) && !magic.AsSpan().SequenceEqual("PSB\0"u8)) continue;
                    totalBytes += input.Length;
                    if (totalBytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("剧本检查总大小超过 2 GB。");
                }
                var original = PatchReadFile(entry, PsbScenarioReader.MaxBytes);
                var resource = Path.GetRelativePath(raw, entry); // Keep extraction's Windows separators for stable segment IDs.
                PsbScenarioReader reader; (List<ScriptSegment> Segments, int Scenes) extracted;
                try { reader = new PsbScenarioReader(original); extracted = reader.Extract(resource); }
                catch (InvalidDataException) { continue; } // Other PSB asset types are not dialogue.
                var rows = extracted.Segments.Where(s => complete.ContainsKey(s.Id)).ToArray();
                if (rows.Length == 0) continue;
                foreach (var row in rows)
                {
                    var translated = complete[row.Id];
                    if (!matched.Add(row.Id) || row.Source != translated.Source || row.File != translated.File ||
                        row.Scene != translated.Scene || row.Index != translated.Index || row.Speaker != translated.Speaker)
                        throw new InvalidDataException("译文位置与原始剧本不一致，请核对提取任务。");
                }
                var patch = reader.BuildPatch(resource, complete);
                if (patch.Changed != rows.Length) throw new InvalidDataException("部分译文未能回填，没有生成补丁。");
                var sourceHash = PatchHash(original);
                if (!seenHashes.Add(sourceHash)) throw new InvalidDataException("提取结果含重复资源，无法确定补丁对应关系。");
                files.Add(new(patch.Name, sourceHash, PatchHash(patch.Bytes), patch.Bytes, patch.Changed));
                if (files.Count > 256) throw new InvalidDataException("单个补丁最多包含 256 份修改后的剧本。");
            }
        }
        if (matched.Count != complete.Count) throw new InvalidDataException("有已完成译文找不到对应的原始剧本，请恢复原提取目录后重试。");
        files = files.OrderBy(f => f.SourceHash, StringComparer.Ordinal).ToList();
        var fingerprint = ExternalHash(new { gameName, gameHash, request.ChineseFont, fontFamily, fontFile, total = job.Segments.Count, components,
            rows = complete.Values.OrderBy(s => s.Id).Select(s => new { s.Id, s.Source, s.Translation }),
            files = files.Select(f => new { f.SourceHash, f.PayloadHash, f.Changed }) });
        return new(new(job.GameName, gameName, complete.Count, job.Segments.Count, job.Segments.Count - complete.Count,
            files.Count, files.Sum(f => (long)f.Bytes.Length) + (fontBytes?.Length ?? 0), fingerprint, request.ChineseFont, fontFamily, fontFile), gameHash, files, components, fontBytes);
    }
    public ScriptPatchPlan PreviewScriptPatch(string id, ScriptPatchRequest request)
    {
        _begin(); try { lock (_gate) return BuildScriptPatch(Job(id), request).Plan; } finally { _end(); }
    }
    public ScriptPatchOutput GenerateScriptPatch(string id, ScriptPatchRequest request)
    {
        if (!request.Confirmed || string.IsNullOrEmpty(request.Fingerprint)) throw new ArgumentException("请先预览补丁内容并确认生成。");
        _begin();
        try
        {
            lock (_gate)
            {
                var build = BuildScriptPatch(Job(id), request);
                if (build.Plan.Fingerprint != request.Fingerprint) throw new InvalidOperationException("译文、游戏或组件已变化，请重新预览补丁。");
                var patchId = Guid.NewGuid().ToString("N");
                var parent = Path.Combine(Folder(id), "patches"); UpdatePackage.RejectLinks(parent); Directory.CreateDirectory(parent);
                var stage = Path.Combine(parent, patchId + ".staging"); var final = Path.Combine(parent, patchId);
                UpdatePackage.RejectLinks(stage); UpdatePackage.RejectLinks(final);
                Directory.CreateDirectory(stage);
                try
                {
                    var root = Path.Combine(stage, "RikaPatch"); var payloadDirectory = build.ResourcePatch ? "resources" : "scripts";
                    Directory.CreateDirectory(Path.Combine(root, payloadDirectory));
                    var map = new StringBuilder(); var manifest = new List<object>();
                    for (var i = 0; i < build.Files.Count; i++)
                    {
                        var file = build.Files[i]; var name = (i + 1).ToString("D4") + (build.ResourcePatch ? ".bin" : ".mdf");
                        File.WriteAllBytes(Path.Combine(root, payloadDirectory, name), file.Bytes);
                        map.AppendLine(build.ResourcePatch ? $"{file.Name}|{file.SourceHash}|{file.PayloadHash}|{name}" : $"{file.SourceHash} {file.PayloadHash} {name}");
                        manifest.Add(new { script = file.Name, payload = name, file.SourceHash, file.PayloadHash, lines = file.Changed });
                    }
                    foreach (var component in build.Components)
                    {
                        var bytes = PatchReadFile(Path.Combine(build.ComponentRoot ?? PatchComponentDirectory, component.Key), 8 * 1024 * 1024);
                        if (PatchHash(bytes) != component.Value) throw new InvalidDataException("补丁组件在生成时发生变化，请重试。");
                        File.WriteAllBytes(Path.Combine(root, component.Key), bytes);
                    }
                    if (build.ResourcePatch) File.WriteAllText(Path.Combine(root, "resource-map.txt"), map.ToString(), Encoding.Unicode);
                    if (build.ResourcePatch) File.WriteAllBytes(Path.Combine(root, "character-map.bin"), build.CharacterMap ?? []);
                    var fontName = build.FontBytes == null ? "" : "custom" + Path.GetExtension(build.Plan.FontFile);
                    if (build.FontBytes != null) { Directory.CreateDirectory(Path.Combine(root, "font")); File.WriteAllBytes(Path.Combine(root, "font", fontName), build.FontBytes); }
                    File.WriteAllText(Path.Combine(root, "RikaPatch.ini"), $"[Patch]\r\nFormat=1\r\nGame={build.Plan.GameExecutable}\r\nGameSha256={build.GameHash}\r\nPatchDllSha256={build.Components["RikaScriptPatch.dll"]}\r\nChineseFont={(request.ChineseFont ? 1 : 0)}\r\nFontFamily={build.Plan.FontFamily}\r\nFontFile={fontName}\r\nFontSha256={(build.FontBytes == null ? "" : PatchHash(build.FontBytes))}\r\nDiagnostics=0\r\n", Encoding.Unicode);
                    if (!build.ResourcePatch)
                    {
                        var settings = new StringBuilder($"MapCount={build.Files.Count}\r\n[Scripts]\r\n");
                        for (var i = 0; i < build.Files.Count; i++)
                        {
                            var number = (i + 1).ToString("D4"); var file = build.Files[i];
                            settings.Append($"Source{number}={file.SourceHash}\r\nHash{number}={file.PayloadHash}\r\n");
                        }
                        File.AppendAllText(Path.Combine(root, "RikaPatch.ini"), settings.ToString(), Encoding.Unicode);
                    }
                    UpdatePackage.WriteJson(Path.Combine(root, "patch-manifest.json"), new { format = "rika-kirikiri-patch-1", version = 1,
                        gameExecutable = build.Plan.GameExecutable, gameSha256 = build.GameHash, build.Plan.Translated, build.Plan.Total,
                        build.Plan.Remaining, chineseFont = request.ChineseFont, build.Plan.FontFamily, fontFile = fontName,
                        fontSha256 = build.FontBytes == null ? "" : PatchHash(build.FontBytes), files = manifest });
                    CopyPatchLicenses(PatchLicenseDirectory, Path.Combine(root, "licenses"));
                    File.WriteAllText(Path.Combine(root, "使用说明.txt"), $"Rika 汉化补丁（部分汉化）\r\n\r\n已回填 {build.Plan.Translated} 条对白，涉及 {build.Plan.Files} 份剧本。其余 {build.Plan.Remaining} 条保持原文。\r\n\r\n使用方法：\r\n1. 退出游戏，把整个 RikaPatch 文件夹复制到 {build.Plan.GameExecutable} 所在目录。\r\n2. 打开 RikaPatch 文件夹，运行 RikaPatchLauncher.exe。\r\n3. 从原来的游戏 EXE 启动时不加载本补丁。移除本补丁只需删除 RikaPatch 文件夹。\r\n\r\n补丁不覆盖原始 EXE、XP3 资源包或存档；游戏启动后仍按原有方式保存进度。\r\n启动器核对游戏 EXE 和补丁文件，运行时只替换原始内容完全匹配的剧本资源。\r\n不包含翻译 API Key，也不需要梨花或 AI 服务保持运行。\r\n本补丁只修改对白；角色名、菜单图片和游戏指令保留。\r\n更新译文后，请在梨花中重新生成补丁；旧补丁不会自动变化。\r\n补丁字体：{build.Plan.FontFamily}\r\n可在生成窗口选择系统字体或 TTF / OTF 文件。文件会随补丁携带，只在游戏进程内加载；分享前请确认字体允许再分发。实际显示仍需在游戏中检查。\r\n如加载失败，可查看 loader-status.txt；本功能目前仅支持已适配的 32 位 KiriKiri / Hxv4 PSB 3 游戏。\r\n\r\n加载组件源码、GPL-3.0 和第三方许可见 licenses 文件夹。\r\n", new UTF8Encoding(true));
                    if (build.ResourcePatch) WriteMultiPatchMetadata(root, build, request, manifest);
                    var created = DateTimeOffset.UtcNow;
                    UpdatePackage.WriteJson(Path.Combine(stage, "output.json"), new ScriptPatchOutput(patchId, "RikaPatch", created,
                        build.Plan.Translated, build.Plan.Files, build.Plan.Remaining));
                    Directory.Move(stage, final);
                    return new(patchId, Path.Combine(final, "RikaPatch"), created, build.Plan.Translated, build.Plan.Files, build.Plan.Remaining);
                }
                catch
                {
                    // Only delete this call's own staging directory inside the validated job output.
                    UpdatePackage.RejectLinks(stage);
                    if (Directory.Exists(stage)) Directory.Delete(stage, true);
                    throw;
                }
            }
        }
        finally { _end(); }
    }
    private static void CopyPatchLicenses(string source, string destination)
    {
        UpdatePackage.RejectLinks(source); Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            UpdatePackage.RejectLinks(path);
            var target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyPatchLicenses(path, target);
            else File.WriteAllBytes(target, PatchReadFile(path, 8 * 1024 * 1024));
        }
    }
    public string ScriptPatchDirectory(string id, string patchId)
    {
        lock (_gate)
        {
            _ = Job(id);
            if (!ValidId(patchId)) throw new ArgumentException("无效的补丁编号。");
            var parent = Path.Combine(Folder(id), "patches", patchId);
            var report = JsonSerializer.Deserialize<ScriptPatchOutput>(PatchReadFile(Path.Combine(parent, "output.json"), 4096), UpdatePackage.Json);
            var path = Path.Combine(parent, "RikaPatch"); UpdatePackage.RejectLinks(path);
            if (report?.Id != patchId || report.Directory != "RikaPatch" || !Directory.Exists(path)) throw new InvalidDataException("补丁目录不存在。");
            return path;
        }
    }
    public object DeleteScriptPatch(string id, string patchId)
    {
        _begin();
        try
        {
            lock (_gate)
            {
                _ = ScriptPatchDirectory(id, patchId);
                var parent = Path.Combine(Folder(id), "patches", patchId);
                // Reject links anywhere inside this one generated output before deleting it.
                var pending = new Stack<string>(); pending.Push(parent);
                while (pending.Count > 0)
                {
                    var current = pending.Pop(); UpdatePackage.RejectLinks(current);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        UpdatePackage.RejectLinks(entry);
                        if (Directory.Exists(entry)) pending.Push(entry);
                    }
                }
                Directory.Delete(parent, true);
                return new { deleted = true };
            }
        }
        finally { _end(); }
    }
    public IReadOnlyList<ScriptPatchOutput> ScriptPatches(string id)
    {
        lock (_gate)
        {
            _ = Job(id); var parent = Path.Combine(Folder(id), "patches"); UpdatePackage.RejectLinks(parent);
            if (!Directory.Exists(parent)) return [];
            var result = new List<ScriptPatchOutput>();
            foreach (var path in Directory.EnumerateDirectories(parent).OrderDescending().Take(1000))
            {
                var patchId = Path.GetFileName(path); if (!ValidId(patchId)) continue;
                try
                {
                    var directory = ScriptPatchDirectory(id, patchId);
                    var output = JsonSerializer.Deserialize<ScriptPatchOutput>(PatchReadFile(Path.Combine(path, "output.json"), 4096), UpdatePackage.Json)!;
                    result.Add(output with { Directory = directory });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
            }
            return result.OrderByDescending(p => p.CreatedAt).Take(20).ToArray();
        }
    }
}
