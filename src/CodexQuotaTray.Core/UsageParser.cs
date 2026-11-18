using System.Text.Json;

namespace CodexQuotaTray.Core;

public static class UsageParser
{
    public static QuotaSnapshot Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var windows = new List<QuotaWindow>();
            AddWindows(Get(root, "rate_limit"), "codex", "Codex", windows, now);
            var additional = Get(root, "additional_rate_limits");
            if (additional.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in additional.EnumerateArray())
                {
                    var bucket = Text(item, "metered_feature") ?? "additional";
                    AddWindows(Get(item, "rate_limit"), bucket,
                        Text(item, "limit_name") ?? bucket, windows, now);
                }
            }

            if (windows.Count == 0 || windows.All(w => w.RemainingPercent is null))
                throw new QuotaException("接口未返回可用的额度窗口。", true);
            return new QuotaSnapshot(windows.AsReadOnly(), Text(root, "plan_type"), now);
        }
        catch (JsonException)
        {
            throw new QuotaException("额度响应格式无效，下一分钟重试。");
        }
    }

    private static void AddWindows(JsonElement limit, string bucket, string label,
        List<QuotaWindow> output, DateTimeOffset now)
    {
        foreach (var key in new[] { "primary_window", "secondary_window" })
        {
            var window = Get(limit, key);
            if (window.ValueKind != JsonValueKind.Object) continue;
            var used = Number(window, "used_percent");
            var duration = Number(window, "limit_window_seconds");
            var timestamp = Number(window, "reset_at");
            DateTimeOffset? reset = null;
            if (timestamp is >= 0 and <= 253402300799)
                reset = DateTimeOffset.FromUnixTimeSeconds((long)timestamp.Value);
            // 某些响应仅有相对秒数；绝不根据本地时间假定额度已经恢复。
            else if (timestamp is null && Number(window, "reset_after_seconds") is { } seconds &&
                     seconds >= 0 && seconds <= (DateTimeOffset.MaxValue - now).TotalSeconds)
                reset = now.AddSeconds(seconds);
            output.Add(new QuotaWindow(bucket, label,
                used.HasValue ? Math.Clamp(100 - used.Value, 0, 100) : null,
                duration is > 0 ? duration : null, reset));
        }
    }

    internal static JsonElement Get(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value : default;
    internal static string? Text(JsonElement element, string key) =>
        Get(element, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static double? Number(JsonElement element, string key) =>
        Get(element, key) is { ValueKind: JsonValueKind.Number } value &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
