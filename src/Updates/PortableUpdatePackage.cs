using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameTranslateToolkit.Updates;

public static class PortableUpdatePackage
{
    // Convert the already authenticated Release asset into the installer's
    // existing manifest format. GitHub source archives do not match this root.
    public static async Task ConvertAsync(string archive, string output, string version, string notes, CancellationToken ct)
    {
        UpdatePackage.ParseVersion(version);
        UpdatePackage.RejectLinks(archive); UpdatePackage.RejectLinks(output);
        if (new FileInfo(archive).Length is < 22 or > UpdatePackage.MaxArchiveSize) throw new InvalidDataException("完整程序包大小无效。");
        using var input = ZipFile.OpenRead(archive);
        if (input.Entries.Count is < 2 or > 10000) throw new InvalidDataException("完整程序包文件数量无效。");
        var prefix = $"RikaTranslator-{version}-win-x64/";
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in input.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("请选择该版本完整 Windows x64 程序包，不能使用源码 ZIP 或链接文件。");
            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0 && entry.FullName.EndsWith('/')) continue;
            var directory = relative.EndsWith('/');
            if (directory) relative = relative.TrimEnd('/');
            UpdatePackage.CheckRelativePath(relative);
            if (!paths.Add(relative)) throw new InvalidDataException("完整程序包包含重复路径。");
            if (directory) continue;
            if (entry.Length is < 0 or > UpdatePackage.MaxArchiveSize) throw new InvalidDataException("完整程序包文件过大。");
            expanded = checked(expanded + entry.Length);
            if (expanded > UpdatePackage.MaxExpandedSize || entries.Count >= 5000) throw new InvalidDataException("完整程序包展开后过大。");
            entries.Add(relative, entry);
        }
        // Reject file/directory collisions before writing any payload.
        foreach (var relative in paths)
            for (var slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
                if (entries.ContainsKey(relative[..slash])) throw new InvalidDataException("程序包文件与目录路径冲突。");
        if (UpdatePackage.RequiredFor("runtime").Any(p => !entries.ContainsKey(p)))
            throw new InvalidDataException("完整程序包缺少启动程序、更新程序或运行库。");
        var manifest = new UpdateManifest
        {
            Version = version, MinimumVersion = "0.5.1", Layout = "runtime",
            ReleaseNotes = string.IsNullOrWhiteSpace(notes) ? $"更新到 Rika Translator {version}。" : notes[..Math.Min(notes.Length, 20000)]
        };
        try
        {
            using var outputFile = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(outputFile, ZipArchiveMode.Create);
            var buffer = new byte[81920];
            foreach (var (relative, entry) in entries)
            {
                ct.ThrowIfCancellationRequested();
                using var source = entry.Open();
                using var destination = zip.CreateEntry("payload/" + relative, CompressionLevel.Fastest).Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > entry.Length) throw new InvalidDataException("程序包文件超出声明长度。");
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total != entry.Length) throw new InvalidDataException("完整程序包文件不完整。");
                manifest.Files.Add(new UpdateFile { Path = relative, Size = total, Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() });
            }
            using var metadata = zip.CreateEntry("rika-update.json").Open();
            await JsonSerializer.SerializeAsync(metadata, manifest, UpdatePackage.Json, ct);
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
    }
}
