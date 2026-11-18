namespace CodexQuotaTray.Core;

/// <summary>同一个窗口的额度和时间必须一起显示，避免左右半环指向不同窗口。</summary>
public sealed record QuotaWindow(
    string Bucket, string Label, double? RemainingPercent,
    double? WindowSeconds, DateTimeOffset? ResetsAt)
{
    public double? TimeRemainingPercent(DateTimeOffset now) =>
        ResetsAt is { } reset && WindowSeconds is > 0
            ? Math.Clamp((reset - now).TotalSeconds / WindowSeconds.Value * 100, 0, 100)
            : null;

    public string DurationLabel => WindowSeconds switch
    {
        null => "额度窗口",
        var s when s % 604800 == 0 => $"{s / 604800:0.#} 周额度",
        var s when s % 86400 == 0 => $"{s / 86400:0.#} 天额度",
        var s when s % 3600 == 0 => $"{s / 3600:0.#} 小时额度",
        var s => $"{s / 60:0.#} 分钟额度"
    };
}

public sealed record QuotaSnapshot(IReadOnlyList<QuotaWindow> Windows, string? Plan, DateTimeOffset UpdatedAt)
{
    public QuotaWindow? MainWindow => Windows
        .OrderBy(w => w.Bucket == "codex" ? 0 : 1)
        .ThenBy(w => w.RemainingPercent ?? double.MaxValue)
        .FirstOrDefault();
}

public sealed record QuotaState(QuotaSnapshot? Snapshot, string Message, bool IsError)
{
    public static QuotaState Initial { get; } = new(null, "正在读取 Codex 额度…", false);
    public bool IsStale(DateTimeOffset now) => IsError || Snapshot is null ||
        now - Snapshot.UpdatedAt > TimeSpan.FromSeconds(150);
}

/// <summary>只传递经过筛选的中文消息，不能把 HTTP 响应或凭据带进日志/UI。</summary>
public sealed class QuotaException(string message, bool clearSnapshot = false) : Exception(message)
{
    public bool ClearSnapshot { get; } = clearSnapshot;
}
