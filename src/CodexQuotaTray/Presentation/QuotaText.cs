using CodexQuotaTray.Core;

namespace CodexQuotaTray.Presentation;

internal static class QuotaText
{
    public static string Percent(QuotaWindow window) => window.RemainingPercent is { } value ? $"{value:0.#}%" : "未知";
    public static string Reset(QuotaWindow window) => window.ResetsAt?.ToLocalTime().ToString("MM-dd HH:mm (zzz)") ?? "时间未知";
    public static string Countdown(QuotaWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } reset) return "等待接口返回重置时间";
        var left = reset - now;
        if (left <= TimeSpan.Zero) return "已到重置时间，等待接口确认";
        var minutes = Math.Ceiling(left.TotalMinutes);
        return minutes >= 1440 ? $"还有 {Math.Floor(minutes / 1440)} 天 {Math.Floor(minutes % 1440 / 60)} 小时重置" :
            minutes >= 60 ? $"还有 {Math.Floor(minutes / 60)} 小时 {minutes % 60} 分钟重置" : $"还有 {minutes} 分钟重置";
    }
}
