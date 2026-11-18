using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace CodexQuotaTray.Core;

public interface IUsageClient
{
    Task<QuotaSnapshot> ReadAsync(CodexCredentials credentials, CancellationToken cancellationToken);
}

public sealed class UsageClient(HttpClient http, TimeProvider? timeProvider = null) : IUsageClient
{
    public static readonly Uri Endpoint = new("https://chatgpt.com/backend-api/wham/usage");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        // 凭据只能发送给固定的官方地址，禁止重定向到其他主机。
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<QuotaSnapshot> ReadAsync(CodexCredentials credentials, CancellationToken cancellationToken)
    {
        // 与官方 backend-client 的 get_rate_limit_status 保持一致：只发 GET。
        // 不发送 reserve 请求头，也不调用兑换重置额度的 POST 接口。
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodexQuotaTray/1.1.0");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
            request.Headers.Add("ChatGPT-Account-Id", credentials.AccountId);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new QuotaException("登录已过期，请在 Codex 中重新登录。", true),
                HttpStatusCode.Forbidden => new QuotaException("额度接口拒绝访问，请检查 Codex 登录状态或网络。", true),
                HttpStatusCode.TooManyRequests => new QuotaException("额度查询过于频繁，下一分钟重试。"),
                _ => new QuotaException($"额度接口返回 HTTP {(int)response.StatusCode}，下一分钟重试。")
            };
        }

        // 限制解压后的响应体大小，避免异常响应占用大量内存。
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > 1024 * 1024)
                throw new QuotaException("额度响应过大，已取消读取。");
            buffer.Write(chunk, 0, count);
        }
        return UsageParser.Parse(Encoding.UTF8.GetString(buffer.ToArray()), clock.GetUtcNow());
    }
}
