using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTray.Core;

// 不使用 record，防止自动生成的 ToString 意外输出 access token。
public sealed class CodexCredentials(string accessToken, string? accountId, string? contextKey = null)
{
    public string AccessToken { get; } = accessToken;
    public string? AccountId { get; } = accountId;
    public string Fingerprint { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(contextKey + "\n" + accountId + "\n" + accessToken)));
    public override string ToString() => "CodexCredentials [redacted]";
}

public interface ICredentialSource
{
    Task<CodexCredentials> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>每次请求重读文件，以跟随 Codex 换号/续期；不写入或刷新凭据。</summary>
public sealed class FileCredentialSource(string codexHome, string? storageOverride = null) : ICredentialSource
{
    public async Task<CodexCredentials> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await VerifyStorageModeAsync(cancellationToken);
            var path = Path.Combine(codexHome, "auth.json");
            // 允许 Codex 原子替换 auth.json，不阻碍它的登录状态更新。
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > 1024 * 1024)
                throw new QuotaException("Codex 登录文件大小异常。", true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var mode = UsageParser.Text(root, "auth_mode");
            if (mode is not null && mode != "chatgpt")
                throw new QuotaException("当前登录方式不支持订阅额度查询，请在 Codex 中使用 ChatGPT 登录。", true);
            var tokens = UsageParser.Get(root, "tokens");
            var token = UsageParser.Text(tokens, "access_token");
            var account = UsageParser.Text(tokens, "account_id");
            if (string.IsNullOrWhiteSpace(token))
                throw new QuotaException("未找到 ChatGPT 登录凭据，请先在 Codex 中登录。", true);
            if (token.Any(char.IsWhiteSpace) || account?.Any(char.IsControl) == true)
                throw new QuotaException("Codex 登录凭据格式无效。", true);
            return new CodexCredentials(token, account);
        }
        catch (IOException)
        {
            throw new QuotaException("无法读取 auth.json，请确认 Codex 已登录且 CODEX_HOME 一致。", true);
        }
        catch (UnauthorizedAccessException)
        {
            throw new QuotaException("没有读取 Codex 登录文件的权限。", true);
        }
        catch (JsonException)
        {
            throw new QuotaException("Codex 登录文件正在更新或格式无效，下一分钟重试。", true);
        }
    }

    private async Task VerifyStorageModeAsync(CancellationToken cancellationToken)
    {
        // 命令行 -c 覆盖配置文件；由进程识别层提供已经筛选过的单一配置值。
        if (storageOverride is not null)
        {
            if (storageOverride != "file")
                throw new QuotaException("目标 Codex 使用不受支持的凭据存储方式。", true);
            return;
        }
        var config = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(config)) return;
        // 这里只检查顶层的存储方式，不解析/改写 Codex 的其他配置。
        // 不支持的存储方式必须报错，不能误读遗留 auth.json 中的旧账号。
        foreach (var line in await File.ReadAllLinesAsync(config, cancellationToken))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) break;
            var match = System.Text.RegularExpressions.Regex.Match(trimmed,
                "^(?:cli_auth_credentials_store|\"cli_auth_credentials_store\"|'cli_auth_credentials_store')\\s*=\\s*[\"']([^\"']+)[\"']");
            if (match.Success && match.Groups[1].Value != "file")
                throw new QuotaException("当前 Codex 使用安全存储或临时凭据；此版本仅支持 auth.json 文件登录。", true);
        }
    }
}
