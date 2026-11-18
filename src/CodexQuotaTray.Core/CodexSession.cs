namespace CodexQuotaTray.Core;

/// <summary>只保存识别账号目录必需的信息，不携带完整环境块或命令行。</summary>
public sealed record CodexSession(string Key, int ProcessId, string Home, string? StorageOverride = null);

public sealed record CodexSessionCandidate(CodexSession? Session, bool IsForeground, bool IsDesktopServer, string? Error = null);

public interface ICodexSessionSource
{
    CodexSession Resolve();
}

public static class CodexSessionSelection
{
    public static CodexSession Select(IReadOnlyList<CodexSessionCandidate> candidates, string? previousKey)
    {
        if (candidates.Count == 0)
            throw new QuotaException("未找到可识别的 Codex 用户会话，等待 Codex 启动。", true);

        var foreground = candidates.Where(c => c.IsForeground).ToArray();
        if (foreground.Length > 0) return Choose(foreground);

        // 用户打开圆环/切换到其他软件后，保持最近识别的实例，避免账号跳动。
        var previous = candidates.FirstOrDefault(c => c.Session?.Key == previousKey && previousKey is not null);
        if (previous?.Session is { } remembered) return remembered;

        var desktop = candidates.Where(c => c.IsDesktopServer).ToArray();
        return Choose(desktop.Length > 0 ? desktop : candidates);
    }

    private static CodexSession Choose(IReadOnlyList<CodexSessionCandidate> candidates)
    {
        if (candidates.Any(c => c.Session is null))
            throw new QuotaException(candidates.First(c => c.Session is null).Error ??
                "无法读取目标 Codex 进程的登录目录，请以相同用户和权限运行圆环。", true);
        var sessions = candidates.Select(c => c.Session!).ToArray();
        if (sessions.Select(c => c.Home).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
            sessions.Select(c => c.StorageOverride ?? "file").Distinct(StringComparer.Ordinal).Count() != 1)
            throw new QuotaException("检测到多个 Codex 登录目录，请将要查询的 Codex 窗口切到前台后刷新。", true);
        // 同一目录的多个会话共享文件凭据；按 PID 排序使选择稳定。
        return sessions.OrderBy(c => c.ProcessId).First();
    }
}

/// <summary>每次查询都重新定位进程目录，并校验读取过程中是否切换了实例。</summary>
public sealed class ActiveCredentialSource(ICodexSessionSource sessions) : ICredentialSource
{
    public async Task<CodexCredentials> ReadAsync(CancellationToken cancellationToken)
    {
        var session = sessions.Resolve();
        if (session.StorageOverride is not null and not "file")
            throw new QuotaException("目标 Codex 使用安全存储或临时凭据，目前不能读取该账号。", true);
        var credentials = await new FileCredentialSource(session.Home, session.StorageOverride).ReadAsync(cancellationToken);
        if (sessions.Resolve() != session)
            throw new QuotaException("活跃 Codex 实例已变化，请重新刷新。", true);
        // 进程、目录也纳入指纹，防止切换实例时沿用上一实例的响应。
        return new CodexCredentials(credentials.AccessToken, credentials.AccountId, session.Key);
    }
}
