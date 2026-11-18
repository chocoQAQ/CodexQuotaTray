using System.Text;
using System.Text.Json;

namespace CodexQuotaTray.Core;

/// <summary>只使用初始化、账号和额度读取 RPC；不创建会话、不执行模型任务。</summary>
public sealed class AppServerProtocol(TextReader input, TextWriter output)
{
    private int nextId;
    private int received;
    private readonly char[] character = new char[1];

    public async Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken token)
    {
        await RequestAsync("initialize", new { clientInfo = new {
            name = "codex_quota_tray", title = "CodexQuotaTray", version = typeof(AppServerProtocol).Assembly.GetName().Version?.ToString() } }, token);
        await SendAsync(new { method = "initialized", @params = new { } }, token);
        var before = await ReadAccountAsync(token);
        var result = await RequestAsync("account/rateLimits/read", new { }, token);
        var after = await ReadAccountAsync(token);
        if (before != after) throw new QuotaException("Codex 账号已变化，请重新刷新。", true);
        return AppServerUsageParser.Parse(result, DateTimeOffset.UtcNow);
    }

    private async Task<string> ReadAccountAsync(CancellationToken token)
    {
        var result = await RequestAsync("account/read", new { refreshToken = false }, token);
        var account = UsageParser.Get(result, "account");
        if (account.ValueKind != JsonValueKind.Object || UsageParser.Text(account, "type") != "chatgpt")
            throw new QuotaException("请在 Codex 中使用 ChatGPT 账号登录后重试。", true);
        // 仅用于本次查询的换号校验；账号字段不持久化、不输出到界面或日志。
        return account.GetRawText();
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        var id = ++nextId;
        await SendAsync(new { id, method, @params = parameters }, token);
        while (true)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (await input.ReadAsync(character.AsMemory(), token) == 0)
                    throw new QuotaException("app-server 已退出，请升级 Codex 或切换到 auth.json。", true);
                if (++received > 1024 * 1024)
                    throw new QuotaException("app-server 响应过大，已停止读取。", true);
                if (character[0] == '\n') break;
                line.Append(character[0]);
            }
            if (string.IsNullOrWhiteSpace(line.ToString())) continue;
            using var document = JsonDocument.Parse(line.ToString());
            var root = document.RootElement;
            var responseId = UsageParser.Get(root, "id");
            if (UsageParser.Get(root, "method").ValueKind == JsonValueKind.String)
            {
                // 不接收宿主令牌刷新/工具执行请求，明确拒绝，避免等待双方相互阻塞。
                if (responseId.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                    await SendAsync(new { id = responseId, error = new { code = -32601, message = "Unsupported method" } }, token);
                continue;
            }
            if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var value) || value != id) continue;
            if (UsageParser.Get(root, "error").ValueKind != JsonValueKind.Undefined)
                throw new QuotaException("app-server 查询失败，请检查 Codex 登录或切换到 auth.json。", true);
            var result = UsageParser.Get(root, "result");
            if (result.ValueKind != JsonValueKind.Object) throw new QuotaException("app-server 响应格式无效。", true);
            return result.Clone();
        }
    }

    private async Task SendAsync(object message, CancellationToken token)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
        await output.FlushAsync(token);
    }
}
