using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexQuotaTray.Core;

public enum UpdateCheckStatus { Current, Available, Unavailable }

public sealed record AvailableUpdate(Version Version, Uri ReleasePage);

public sealed record UpdateCheckResult(UpdateCheckStatus Status, AvailableUpdate? Update = null, string? Message = null);

public interface IUpdateClient
{
    Task<UpdateCheckResult> CheckAsync(Version installedVersion, CancellationToken cancellationToken);
}

/// <summary>匿名读取本项目最新正式 Release；与 Codex 额度请求及凭据完全分离。</summary>
public sealed class UpdateClient(HttpClient http) : IUpdateClient
{
    public static readonly Uri Endpoint = new("https://api.github.com/repos/chocoQAQ/CodexQuotaTray/releases/latest");
    private const int MaximumResponseBytes = 256 * 1024;

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<UpdateCheckResult> CheckAsync(Version installedVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 覆盖响应头及响应体整个读取过程，避免慢速响应拖住后台任务。
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.UserAgent.ParseAdd($"CodexQuotaTray/{installedVersion}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "GitHub 暂无可用的正式发行版。",
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub 查询受限，请稍后重试。",
                    _ => "暂时无法查询 GitHub 更新，请稍后重试。"
                });
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    return Unavailable("GitHub 更新响应过大，已取消检查。");
                buffer.Write(chunk, 0, count);
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            // latest 接口本身过滤预发布和草稿；这里再次验证，避免异常响应触发提示。
            if (UsageParser.Get(root, "draft").ValueKind != JsonValueKind.False ||
                UsageParser.Get(root, "prerelease").ValueKind != JsonValueKind.False)
                return Unavailable("GitHub 暂无可用的正式发行版。");
            var tag = UsageParser.Text(root, "tag_name");
            if (tag is null || !TryParseVersion(tag, out var latest))
                return Unavailable("GitHub 最新发行版的版本号无法识别。");
            var current = Normalize(installedVersion);
            if (latest <= current) return new(UpdateCheckStatus.Current);

            // 页面由固定仓库和已验证的数字版本标签构造，不打开响应中的任意 html_url。
            var page = new Uri("https://github.com/chocoQAQ/CodexQuotaTray/releases/tag/" + tag);
            return new(UpdateCheckStatus.Available, new AvailableUpdate(latest, page));

        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("检查更新超时，请稍后重试。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        {
            return Unavailable("检查更新失败，请检查网络后重试。");
        }
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        // 兼容手工三段版本与 CD 的四段版本；数字比较可正确区分 .9 与 .10。
        if (tag.Length > 64 || !Regex.IsMatch(tag, @"^[vV]?[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$")) return false;
        var text = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(text, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor,
        Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static UpdateCheckResult Unavailable(string message) => new(UpdateCheckStatus.Unavailable, Message: message);
}
