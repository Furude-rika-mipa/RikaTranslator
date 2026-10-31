using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTranslateToolkit.Updates;

namespace GameTranslateToolkit.ScriptTranslation;

public sealed partial class ScriptTranslationService
{
    private sealed class MultiPatchResult
    {
        public List<MultiPatchFile> Files { get; set; } = [];
        public List<MultiPatchCharacter> Mapping { get; set; } = [];
    }
    private sealed class MultiPatchFile
    {
        public string Name { get; set; } = "";
        public string Payload { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public string PayloadHash { get; set; } = "";
        public int Changed { get; set; }
        public long Bytes { get; set; }
    }
    private sealed class MultiPatchCharacter
    {
        public string Code { get; set; } = "";
        public int Character { get; set; }
    }
    private static bool SupportsScriptPatch(ScriptJob job) => job.Format == "psb3" ||
        job.Format == "multi-engine-v1" && job.Engine is "WillPlus" or "YU-RIS" or "ExHIBIT" or "Escu:de" or "SiglusEngine" or "BGI / Ethornell";

    private PatchBuild BuildMultiPatch(ScriptJob job, ScriptPatchRequest request)
    {
        if (!SupportsScriptPatch(job)) throw new InvalidOperationException("此剧本格式尚未适配补丁生成。");
        if (Busy || job.External?.Enabled == true) throw new InvalidOperationException("请先暂停剧本翻译或外部领取，再生成汉化补丁。");
        var game = Game(job.GameId);
        if (MultiEngine(game) != job.Engine) throw new InvalidDataException("游戏引擎与提取任务不一致。");
        var complete = job.Segments.Where(s => s.Status == "translated").ToArray();
        if (complete.Length == 0) throw new InvalidOperationException("当前没有已完成译文，请先翻译部分对白。");
        var exe = PatchReadFile(game.ExecutablePath, 128L * 1024 * 1024);
        var pe = exe.Length >= 64 ? BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(60)) : -1;
        if (pe < 64 || pe > exe.Length - 24 || !exe.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8)) throw new InvalidDataException("游戏 EXE 格式无效。");
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(pe + 4));
        var arch = machine == 0x14c ? "x86" : machine == 0x8664 ? "x64" : throw new InvalidDataException("补丁启动器支持 Windows x86 / x64 游戏。");
        var componentRoot = Path.Combine(_application, "adapters", "script-patch", "multi", arch);
        var components = VerifyPatchComponents(componentRoot); var gameHash = PatchHash(exe);
        var gameName = Path.GetFileName(game.ExecutablePath);
        if (gameName.Length > 200 || gameName.Any(c => char.IsControl(c) || "/\\:\"<>|".Contains(c))) throw new InvalidDataException("游戏启动程序文件名不受支持。");
        byte[]? fontBytes = null; string? fontFile = null; var family = "";
        if (request.ChineseFont)
        {
            if (!string.IsNullOrEmpty(request.FontFile))
            {
                fontBytes = FontAssets.ReadSourceFont(PatchFontPath(job.Id, request.FontFile));
                if (PatchHash(fontBytes) != request.FontFile[..64]) throw new InvalidDataException("补丁字体已变化，请重新选择。");
                family = PatchFontFamily(fontBytes); fontFile = request.FontFile;
            }
            else
            {
                family = PatchFontFamily(request.FontFamily);
                if (!FontCatalog.Installed().Any(f => f.Aliases.Contains(family, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException("本机没有这个系统字体，请从列表选择或导入字体文件。");
            }
        }
        var allowed = MultiArchives(game, job.Engine);
        if (!allowed.Any(a => a.Name == job.Archive)) throw new InvalidDataException("原始剧本资源已变化，请重新检查游戏。");
        var hashes = MultiInputHashes(game, job.Engine, job.Archive); var worker = VerifyMultiReader();
        var work = Path.Combine(Folder(job.Id), "patch-work-" + Guid.NewGuid().ToString("N"));
        UpdatePackage.RejectLinks(work); Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "request.json"); var output = Path.Combine(work, "output");
            UpdatePackage.WriteJson(input, new { engine = job.Engine, executable = game.ExecutablePath, archive = job.Archive,
                rows = complete.Select(s => new { s.File, s.Index, s.Speaker, s.Source, s.Translation }) });
            var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(worker)!, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--patch", input, output }) start.ArgumentList.Add(arg);
            foreach (var key in start.Environment.Keys.Where(k => Regex.IsMatch(k, "API_KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|PYTHON", RegexOptions.IgnoreCase)).ToArray()) start.Environment.Remove(key);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("补丁写回组件未能启动。");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180_000)) { process.Kill(true); process.WaitForExit(5000); throw new InvalidDataException("补丁写回超时，未改动游戏或译文。"); }
            _ = stdout.GetAwaiter().GetResult(); var error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                // Return only the worker's intentional bounded format error;
                // never surface tracebacks, game dialogue, paths or environment.
                string message = "此游戏的剧本写回未通过验证，未生成补丁；已完成译文仍保留。";
                try { var doc = JsonDocument.Parse(error.Trim()); if (doc.RootElement.GetProperty("code").GetString() == "ValueError")
                    { var value = doc.RootElement.GetProperty("error").GetString(); if (value is { Length: > 0 and < 200 }) message = value; } }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
                throw new InvalidDataException(message);
            }
            foreach (var item in hashes)
                if (!File.Exists(item.Key) || UpdatePackage.Hash(item.Key) != item.Value) throw new InvalidDataException("游戏资源在写回期间发生变化，请重新生成。");
            var result = JsonSerializer.Deserialize<MultiPatchResult>(PatchReadFile(Path.Combine(output, "result.json"), 1024 * 1024), UpdatePackage.Json)
                ?? throw new InvalidDataException("补丁资源清单无效。");
            if (result.Files.Count is < 1 or > 256 || result.Mapping.Count > 4096) throw new InvalidDataException("补丁资源数量无效。");
            var files = new List<PatchFile>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            for (var i = 0; i < result.Files.Count; i++)
            {
                var file = result.Files[i];
                if (file.Payload != $"{i + 1:D4}.bin" || string.IsNullOrEmpty(file.Name) || file.Name.Length > 512 ||
                    file.Name.Any(c => char.IsControl(c) || ":|<>\"?*".Contains(c)) || Path.IsPathRooted(file.Name) ||
                    file.Name.Replace('\\', '/').Split('/').Any(s => s is "" or "." or "..") || !names.Add(file.Name) || file.Changed < 1)
                    throw new InvalidDataException("补丁资源路径或条数无效。");
                var originalPath = Path.GetFullPath(Path.Combine(game.Directory, file.Name));
                var gameRoot = Path.GetFullPath(game.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!originalPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("补丁资源越界。");
                UpdatePackage.RejectLinks(originalPath);
                var bytes = PatchReadFile(Path.Combine(output, file.Payload), 128L * 1024 * 1024); total += bytes.Length;
                if (bytes.Length != file.Bytes || total > 512L * 1024 * 1024 || PatchHash(bytes) != file.PayloadHash || UpdatePackage.Hash(originalPath) != file.SourceHash)
                    throw new InvalidDataException("补丁资源校验失败。");
                files.Add(new(file.Name, file.SourceHash, file.PayloadHash, bytes, file.Changed));
            }
            if (files.Sum(f => f.Changed) != complete.Length) throw new InvalidDataException("部分译文没有成功回填，未生成补丁。");
            using var map = new MemoryStream(); var codes = new HashSet<int>();
            Span<byte> mapRow = stackalloc byte[4];
            foreach (var c in result.Mapping)
            {
                if (!Regex.IsMatch(c.Code, "\\A[a-f0-9]{4}\\z") || c.Character is < 32 or > 65535 || c.Character is >= 0xd800 and <= 0xdfff) throw new InvalidDataException("补丁字符映射无效。");
                var code = Convert.FromHexString(c.Code); var value = code[0] * 256 + code[1];
                if (!codes.Add(value) || !(code[0] is >= 0x81 and < 0xa0 or >= 0xe0 and <= 0xfc) || code[1] is 0 or >= 64) throw new InvalidDataException("补丁字符编码无效。");
                BinaryPrimitives.WriteUInt16LittleEndian(mapRow, (ushort)value); BinaryPrimitives.WriteUInt16LittleEndian(mapRow[2..], (ushort)c.Character); map.Write(mapRow);
            }
            var characterMap = map.ToArray();
            var fingerprint = ExternalHash(new { engine = job.Engine, gameName, gameHash, request.ChineseFont, family, fontFile, components,
                rows = complete.OrderBy(s => s.Id).Select(s => new { s.Id, s.Source, s.Translation }),
                files = files.Select(f => new { f.Name, f.SourceHash, f.PayloadHash, f.Changed }), characterHash = PatchHash(characterMap) });
            return new(new(job.GameName, gameName, complete.Length, job.Segments.Count, job.Segments.Count - complete.Length,
                files.Count, total + (fontBytes?.Length ?? 0), fingerprint, request.ChineseFont, family, fontFile), gameHash, files, components, fontBytes, componentRoot, true, characterMap);
        }
        finally { UpdatePackage.RejectLinks(work); if (Directory.Exists(work)) Directory.Delete(work, true); }
    }

    private void WriteMultiPatchMetadata(string root, PatchBuild build, ScriptPatchRequest request, List<object> manifest)
    {
        var fontName = build.FontBytes == null ? "" : "custom" + Path.GetExtension(build.Plan.FontFile);
        File.WriteAllText(Path.Combine(root, "RikaPatch.ini"), $"[Patch]\r\nFormat=2\r\nGame={build.Plan.GameExecutable}\r\nGameSha256={build.GameHash}\r\nPatchDllSha256={build.Components["RikaScriptPatch.dll"]}\r\nCharacterMapSha256={PatchHash(build.CharacterMap ?? [])}\r\nChineseFont={(request.ChineseFont ? 1 : 0)}\r\nFontFamily={build.Plan.FontFamily}\r\nFontFile={fontName}\r\nFontSha256={(build.FontBytes == null ? "" : PatchHash(build.FontBytes))}\r\n", Encoding.Unicode);
        UpdatePackage.WriteJson(Path.Combine(root, "patch-manifest.json"), new { format = "rika-resource-patch-1", version = 1,
            gameExecutable = build.Plan.GameExecutable, gameSha256 = build.GameHash, build.Plan.Translated, build.Plan.Total,
            build.Plan.Remaining, chineseFont = request.ChineseFont, build.Plan.FontFamily, fontFile = fontName, files = manifest });
        CopyPatchLicenses(Path.Combine(_application, "licenses", "ScriptExtraction"), Path.Combine(root, "licenses", "ScriptExtraction"));
        File.WriteAllText(Path.Combine(root, "使用说明.txt"), $"Rika 资源汉化补丁\r\n\r\n已回填 {build.Plan.Translated} 条，修改 {build.Plan.Files} 份资源，其余 {build.Plan.Remaining} 条保持原文。\r\n\r\n1. 退出游戏，将整个 RikaPatch 文件夹复制到 {build.Plan.GameExecutable} 所在目录。\r\n2. 运行 RikaPatch 内的 RikaPatchLauncher.exe。\r\n3. 启动器核对游戏与资源版本，再在游戏进程中加载已回填的资源副本。\r\n\r\n无需梨花或翻译 API 保持运行。不覆盖原游戏 EXE、资源、设置或存档。删除 RikaPatch 文件夹即可移除本补丁。\r\n原有认证流程仍由游戏处理；本补丁不提供认证绕过。\r\n更新译文后需重新生成。中文字体：{build.Plan.FontFamily}；自定义字体仅在游戏进程内加载。分享前请确认字体可再分发。\r\n补丁只覆盖本任务提取到的已译对白和选项；不翻译角色名、图片或其他未提取内容。\r\n如启动失败，查看 loader-status.txt。实际字体、换行、选项、剧情跳转和存档仍需按游戏验证。\r\n源码与许可证见 licenses。\r\n", new UTF8Encoding(true));
    }
}
