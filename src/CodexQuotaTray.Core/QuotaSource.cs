namespace CodexQuotaTray.Core;

public enum QuotaSourceKind { AuthFile, AppServer }

/// <summary>一次查询绑定一个来源身份，前后比较身份，防止切换实例后展示旧响应。</summary>
public sealed record QuotaConnection(string Identity, Func<CancellationToken, Task<QuotaSnapshot>> ReadAsync);

public interface IQuotaSource
{
    Task<QuotaConnection> ConnectAsync(CancellationToken cancellationToken);
}

public sealed class AuthFileQuotaSource(ICredentialSource credentials, IUsageClient client) : IQuotaSource
{
    public async Task<QuotaConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var auth = await credentials.ReadAsync(cancellationToken);
        return new(auth.Fingerprint, token => client.ReadAsync(auth, token));
    }
}
