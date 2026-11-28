using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexQuotaTray.Core;

public sealed record UpdateProgress(string Message, long Downloaded = 0, long Total = 0);
public sealed record PreparedUpdate(string ExecutablePath, string Sha256);

/// <summary>只下载对应发布包，先校验 ZIP 的 SHA-256，再提取固定名称的 EXE。</summary>
public sealed class UpdateDownloader(HttpClient http)
{
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExecutableBytes = 512L * 1024 * 1024;

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<PreparedUpdate> PrepareAsync(AvailableUpdate update, string directory,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        progress?.Report(new("正在获取更新校验信息…"));
        using var checksumsResponse = await GetAsync(update.ChecksumsUrl, token);
        await using var checksumsStream = await checksumsResponse.Content.ReadAsStreamAsync(token);
        using var checksums = new MemoryStream();
        await CopyLimitedAsync(checksumsStream, checksums, 16 * 1024, null, token);
        var expectedHash = ParseChecksum(Encoding.UTF8.GetString(checksums.ToArray()), update.ArchiveName);

        progress?.Report(new("正在下载更新…", 0, update.ArchiveSize));
        var archivePath = Path.Combine(directory, "package.zip");
        using var response = await GetAsync(update.ArchiveUrl, token);
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var length = await CopyLimitedAsync(source, target, MaximumArchiveBytes,
                bytes => progress?.Report(new("正在下载更新…", bytes, update.ArchiveSize)), token);
            if (length != update.ArchiveSize) throw new InvalidDataException("更新包下载不完整。");
        }
        progress?.Report(new("正在校验更新文件…"));
        await using (var archive = File.OpenRead(archivePath))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(archive, token));
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包校验失败，已停止更新。");
        }

        progress?.Report(new("正在准备安装更新…"));
        using var zip = ZipFile.OpenRead(archivePath);
        // 不使用批量解压，不允许包内路径写出更新目录。
        if (!zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "CodexQuotaTray.exe", "README.md" }))
            throw new InvalidDataException("更新包文件列表无效。");
        var executable = zip.GetEntry("CodexQuotaTray.exe")!;
        if (executable.Length is <= 0 or > MaximumExecutableBytes)
            throw new InvalidDataException("更新程序大小无效。");
        var executablePath = Path.Combine(directory, "CodexQuotaTray.exe");
        await using (var source = executable.Open())
        await using (var target = new FileStream(executablePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            if (await CopyLimitedAsync(source, target, MaximumExecutableBytes, null, token) != executable.Length)
                throw new InvalidDataException("更新程序提取不完整。");
        }
        await using var file = File.OpenRead(executablePath);
        return new PreparedUpdate(executablePath, Convert.ToHexString(await SHA256.HashDataAsync(file, token)));
    }

    public static string ParseChecksum(string text, string archiveName)
    {
        var matches = text.TrimStart('\uFEFF').Split('\n')
            .Select(line => Regex.Match(line.TrimEnd('\r'), @"^([0-9a-fA-F]{64})[ \t]+\*?" + Regex.Escape(archiveName) + "$"))
            .Where(match => match.Success).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("更新校验信息缺失或重复。");
        return matches[0].Groups[1].Value;
    }

    private async Task<HttpResponseMessage> GetAsync(Uri initial, CancellationToken token)
    {
        var url = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0 || !url.IsDefaultPort ||
                url.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("更新下载地址无效。");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("CodexQuotaTray-Updater/1.0");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("更新下载重定向无效。");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            response.Dispose();
            throw new HttpRequestException("更新下载失败，请稍后重试。");
        }
        throw new InvalidDataException("更新下载重定向过多。");
    }

    private static async Task<long> CopyLimitedAsync(Stream source, Stream target, long maximum,
        Action<long>? report, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0, reported = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("更新文件超过大小限制。");
            await target.WriteAsync(buffer.AsMemory(0, count), token);
            // 限制 UI 通知频率，避免大文件下载时消息队列积压。
            if (total - reported >= 256 * 1024) { report?.Invoke(total); reported = total; }
        }
        report?.Invoke(total);
        return total;
    }
}
