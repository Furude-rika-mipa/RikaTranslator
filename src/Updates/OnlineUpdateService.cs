using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTranslateToolkit.Updates;

public sealed record OnlineRelease(string Id, string Version, string Tag, string Notes, string FileName, long Size, string Sha256);
public sealed record OnlineUpdateState(string Stage, bool Busy, string CurrentVersion, OnlineRelease? Release,
    long DownloadedBytes, string Error, UpdatePreview? Preview);

public sealed partial class OnlineUpdateService : IDisposable
{
    public const string Repository = "Furude-rika-mipa/RikaTranslator";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    private const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private readonly string _data, _current;
    private readonly LocalUpdateService _updates;
    private readonly LogService _logs;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _gate = new();
    private string _stage = "idle", _error = "";
    private bool _busy, _cancelling, _disposed;
    private long _downloaded;
    private OnlineRelease? _release;
    private UpdatePreview? _preview;
    private CancellationTokenSource? _cancel;
    private Task? _download;

    public OnlineUpdateService(string data, LocalUpdateService updates, LogService logs, HttpClient? http = null, string? currentVersion = null)
    {
        _data = Path.GetFullPath(data); _updates = updates; _logs = logs;
        _current = currentVersion ?? Program.Version; UpdatePackage.ParseVersion(_current);
        _ownsHttp = http == null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public OnlineUpdateState Status()
    {
        lock (_gate) return new(_stage, _busy || _cancelling, _current, _release, _downloaded, _error, _preview);
    }
    private CancellationTokenSource Begin(string stage, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed || _updates.Scheduled) throw new InvalidOperationException("程序正在退出并安装更新。");
            if (_busy || _cancelling) throw new InvalidOperationException("正在处理更新，请等待或取消当前下载。");
            _busy = true; _stage = stage; _error = ""; _downloaded = 0;
            return _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
    }
    private void Finish(CancellationTokenSource operation)
    {
        lock (_gate) { _busy = false; if (_cancel == operation) _cancel = null; }
        operation.Dispose();
    }
    private void Failure(Exception ex, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ct.IsCancellationRequested) { _stage = "cancelled"; _error = ""; }
            else
            {
                _stage = "error";
                _error = ex is InvalidDataException or InvalidOperationException ? ex.Message : "无法连接 GitHub 或完成下载，请检查网络后重试，也可打开发布页手动下载。";
                _logs.Write("warning", "update", _error);
            }
        }
    }

    public async Task<OnlineUpdateState> CheckAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        var operation = Begin("checking", ct);
        operation.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            OnlineRelease? found;
            try { found = await ReadApiReleaseAsync(operation.Token); }
            catch (HttpRequestException) { found = await ReadPublicReleaseAsync(operation.Token); }
            if (_saved?.Release is { } cached && _preview != null && found != null && SamePackage(cached, found))
            {
                lock (_gate) { _release = found; _stage = "ready"; _downloaded = found.Size; }
            }
            else
            {
                if (_saved != null) await DropSavedAsync(_saved, operation.Token);
                lock (_gate)
                {
                    _release = found;
                    _stage = found != null && UpdatePackage.ParseVersion(found.Version) > UpdatePackage.ParseVersion(_current) ? "available" : "current";
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or UnauthorizedAccessException)
        {
            // A metadata timeout is an error, while explicit cancellation stays quiet.
            if (_saved != null && _preview != null && !_cancelling)
            {
                lock (_gate) { _stage = "ready"; _error = ct.IsCancellationRequested ? "" : "暂时无法检查新版，已下载的更新仍可安装。"; }
            }
            else Failure(ex, ct.IsCancellationRequested || _cancelling ? operation.Token : CancellationToken.None);
        }
        finally { Finish(operation); }
        return Status();
    }

    public OnlineUpdateState StartDownload(string id)
    {
        OnlineRelease release;
        CancellationTokenSource operation;
        lock (_gate)
        {
            if (_release == null || _release.Id != id || _stage is not ("available" or "error" or "cancelled") ||
                UpdatePackage.ParseVersion(_release.Version) <= UpdatePackage.ParseVersion(_current))
                throw new InvalidOperationException("新版信息已失效，请重新检查更新。");
            release = _release;
            operation = Begin("downloading"); operation.CancelAfter(TimeSpan.FromHours(2));
            _preview = null;
            _download = Task.Run(() => DownloadAsync(release, operation));
        }
        return Status();
    }
    private async Task DownloadAsync(OnlineRelease release, CancellationTokenSource operation)
    {
        var ct = operation.Token;
        var scratch = Path.Combine(_data, "updates", "downloads", Guid.NewGuid().ToString("N"));
        UpdatePreview? preview = null;
        try
        {
            UpdatePackage.RejectLinks(scratch); Directory.CreateDirectory(scratch);
            var archive = Path.Combine(scratch, "release.zip");
            using (var response = await SendAsync(AssetUrl(release.Tag, release.FileName), HttpMethod.Get, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long declared && declared != release.Size)
                    throw new InvalidDataException("GitHub 下载文件大小发生变化，请重新检查更新。");
                using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("下载超出发布文件大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct); hash.AppendData(buffer, 0, read);
                    lock (_gate) _downloaded = total;
                }
                if (total != release.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != release.Sha256)
                    throw new InvalidDataException("下载文件不完整或 SHA-256 校验失败，未安装。请重试。");
            }
            lock (_gate) _stage = "preparing";
            var prepared = Path.Combine(scratch, "prepared.zip");
            await PortableUpdatePackage.ConvertAsync(archive, prepared, release.Version, release.Notes, ct);
            preview = await _updates.InspectAsync(prepared, ct, createBackup: false);
            await SaveCacheAsync(release, preview, ct);
            lock (_gate) { _preview = preview; _stage = "ready"; }
            _logs.Write("info", "update", "GitHub 新版下载及校验完成，等待确认安装。", details: new { release.Version });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (preview != null) await _updates.CancelAsync(preview.Id, CancellationToken.None);
            Failure(ex, ct);
        }
        finally
        {
            try { LocalUpdateService.RemoveTemporary(scratch); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Finish(operation);
        }
    }
    public async Task<OnlineUpdateState> CancelAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        Task? download;
        lock (_gate)
        {
            if (_updates.Scheduled) throw new InvalidOperationException("安装已经开始，不能取消。");
            if (_cancelling) return Status();
            _cancelling = true; _cancel?.Cancel(); download = _download;
        }
        try
        {
            if (download != null) await download;
            UpdatePreview? preview;
            OnlineRelease? offer;
            lock (_gate) { preview = _preview; offer = _release; }
            if (_saved == null && preview != null) await _updates.CancelAsync(preview.Id, ct);
            await DropSavedAsync(_saved, ct);
            lock (_gate) { _preview = null; _release = offer; _stage = "cancelled"; _error = ""; }
        }
        finally { lock (_gate) _cancelling = false; }
        return Status();
    }

    private async Task<OnlineRelease?> ReadApiReleaseAsync(CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(ApiUrl), HttpMethod.Get, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await ReadSmallAsync(response, 2 * 1024 * 1024, ct));
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub 最新版本尚未正式发布。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = TagVersion(tag);
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        if (UpdatePackage.ParseVersion(version) <= UpdatePackage.ParseVersion(_current)) return null;
        var filename = $"RikaTranslator-{version}-win-x64.zip";
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == filename).ToList();
        if (assets.Count != 1) throw new InvalidDataException($"发现 v{version}，但 Release 尚未提供 {filename}。请到发布页查看。");
        var asset = assets[0];
        if (asset.GetProperty("state").GetString() != "uploaded") throw new InvalidDataException("新版程序包尚未上传完成。");
        var url = asset.GetProperty("browser_download_url").GetString();
        if (url != AssetUrl(tag, filename).AbsoluteUri) throw new InvalidDataException("新版文件地址不属于当前发布。");
        var size = asset.GetProperty("size").GetInt64(); CheckSize(size);
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        var sha = digest != null && Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") ? digest[7..].ToLowerInvariant() : await ReadChecksumAsync(tag, filename, ct);
        return new(Guid.NewGuid().ToString("N"), version, tag, TrimNotes(notes), filename, size, sha);
    }
    private async Task<OnlineRelease?> ReadPublicReleaseAsync(CancellationToken ct)
    {
        // Public web redirects also work when the shared anonymous API quota is exhausted.
        using var page = await SendAsync(new Uri(ReleasesUrl + "/latest"), HttpMethod.Get, ct);
        page.EnsureSuccessStatusCode();
        var uri = page.RequestMessage!.RequestUri!;
        var prefix = "/" + Repository + "/releases/tag/";
        if (uri.Host != "github.com" || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("无法确定 GitHub 最新发布版本。");
        var tag = uri.AbsolutePath[prefix.Length..]; var version = TagVersion(tag);
        if (UpdatePackage.ParseVersion(version) <= UpdatePackage.ParseVersion(_current)) return null;
        var filename = $"RikaTranslator-{version}-win-x64.zip";
        var sha = await ReadChecksumAsync(tag, filename, ct);
        using var asset = await SendAsync(AssetUrl(tag, filename), HttpMethod.Head, ct);
        asset.EnsureSuccessStatusCode();
        var size = asset.Content.Headers.ContentLength ?? 0; CheckSize(size);
        return new(Guid.NewGuid().ToString("N"), version, tag, "GitHub 提供了新版本。完整更新说明请点击“查看发布页”。", filename, size, sha);
    }
    private async Task<string> ReadChecksumAsync(string tag, string filename, CancellationToken ct)
    {
        using var response = await SendAsync(AssetUrl(tag, "SHA256SUMS.txt"), HttpMethod.Get, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("新版尚未提供 SHA256SUMS.txt，无法校验，请到发布页查看。");
        var text = Encoding.UTF8.GetString(await ReadSmallAsync(response, 65536, ct)).TrimStart('\ufeff');
        var matches = Regex.Matches(text, @"(?m)^([a-fA-F0-9]{64})[ \t]+\*?" + Regex.Escape(filename) + @"[ \t]*\r?$");
        if (matches.Count != 1) throw new InvalidDataException("发布校验文件缺少完整程序包的 SHA-256。");
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }
    private static string TagVersion(string tag)
    {
        if (!Regex.IsMatch(tag, @"^v?\d{1,5}\.\d{1,5}\.\d{1,5}$")) throw new InvalidDataException("GitHub 发布标签不是正式版本号。");
        var version = tag.TrimStart('v'); UpdatePackage.ParseVersion(version); return version;
    }
    private static void CheckSize(long size)
    { if (size is < 22 or > UpdatePackage.MaxArchiveSize) throw new InvalidDataException("GitHub 完整程序包大小无效，最大支持 1 GB。"); }
    private static string TrimNotes(string notes) => notes[..Math.Min(notes.Length, 20000)];
    private static Uri AssetUrl(string tag, string file) => new(ReleasesUrl + "/download/" + tag + "/" + file);
    internal static void ValidateUri(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("更新连接必须使用官方 HTTPS 地址。");
        var allowed = uri.Host switch
        {
            "api.github.com" => uri.AbsoluteUri == ApiUrl,
            "github.com" => uri.AbsolutePath.StartsWith("/" + Repository + "/releases/", StringComparison.Ordinal),
            "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com" => true,
            _ => false
        };
        if (!allowed) throw new InvalidDataException("更新连接跳转到了非 GitHub 地址。");
    }
    private async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, CancellationToken ct)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            ValidateUri(uri);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.UserAgent.ParseAdd("RikaTranslator/" + _current);
            if (uri.Host == "api.github.com")
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.RequestMessage ??= request;
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new InvalidDataException("GitHub 下载跳转地址缺失。");
                uri = new Uri(uri, location); continue;
            }
            return response;
        }
        throw new InvalidDataException("GitHub 下载跳转次数过多。");
    }
    private static async Task<byte[]> ReadSmallAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("GitHub 更新信息过大。");
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("GitHub 更新信息过大。");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _cancel?.Cancel(); }
        _shutdown.Cancel();
        if (_ownsHttp) _http.Dispose();
    }
}
