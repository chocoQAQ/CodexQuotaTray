namespace CodexQuotaTray.Core;

public interface ICodexActivity
{
    bool IsRunning();
}

/// <summary>查询、超时、重入保护和账号切换统一在此处理，与托盘 UI 解耦。</summary>
public sealed class QuotaMonitor(ICodexActivity activity, ICredentialSource credentials, IUsageClient client)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private string? fingerprint;
    public QuotaState State { get; private set; } = QuotaState.Initial;

    public async Task<QuotaState> RefreshAsync(CancellationToken cancellationToken)
    {
        // 定时刷新和手动刷新同时到来时，只允许一个请求在途。
        if (!await refreshGate.WaitAsync(0, cancellationToken)) return State;
        try
        {
            if (!activity.IsRunning())
            {
                fingerprint = null;
                return State = new(null, "等待 Codex 启动，每分钟自动检测。", false);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var auth = await credentials.ReadAsync(timeout.Token);
            if (fingerprint != auth.Fingerprint)
            {
                State = QuotaState.Initial;
                fingerprint = auth.Fingerprint;
            }
            var snapshot = await client.ReadAsync(auth, timeout.Token);
            // 请求期间退出或换号时，丢弃旧响应，不把旧账号额度展示成当前额度。
            if (!activity.IsRunning())
                return State = new(null, "等待 Codex 启动，每分钟自动检测。", false);
            var current = await credentials.ReadAsync(timeout.Token);
            if (current.Fingerprint != auth.Fingerprint)
                return State = new(null, "Codex 登录状态已变化，下一分钟重新读取。", false);
            return State = new(snapshot, "每 60 秒通过 HTTP 更新", false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("额度查询超时，下一分钟重试。");
        }
        catch (QuotaException error)
        {
            return Fail(error.Message, error.ClearSnapshot);
        }
        catch (HttpRequestException)
        {
            return Fail("无法连接额度接口，请检查网络；下一分钟重试。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Fail("读取额度失败，下一分钟重试。");
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private QuotaState Fail(string message, bool clear = false) =>
        State = new(clear ? null : State.Snapshot, message, true);
}
