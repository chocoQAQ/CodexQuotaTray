using System.Text.Json;

namespace CodexQuotaTray.Core;

/// <summary>将 app-server 的分钟窗口和 camelCase 字段转换为统一额度模型。</summary>
public static class AppServerUsageParser
{
    public static QuotaSnapshot Parse(JsonElement result, DateTimeOffset now)
    {
        var windows = new List<QuotaWindow>();
        string? plan = null;
        var buckets = UsageParser.Get(result, "rateLimitsByLimitId");
        if (buckets.ValueKind == JsonValueKind.Object && buckets.EnumerateObject().Any())
            foreach (var bucket in buckets.EnumerateObject()) Add(bucket.Value, bucket.Name);
        else Add(UsageParser.Get(result, "rateLimits"), "codex");
        if (windows.Count == 0 || windows.All(w => w.RemainingPercent is null))
            throw new QuotaException("app-server 未返回可用额度，请确认已使用 ChatGPT 账号登录。", true);
        return new(windows.AsReadOnly(), plan, now);

        void Add(JsonElement limit, string fallback)
        {
            var id = UsageParser.Text(limit, "limitId") ?? fallback;
            var label = UsageParser.Text(limit, "limitName") ?? (id == "codex" ? "Codex" : id);
            plan ??= UsageParser.Text(limit, "planType");
            foreach (var name in new[] { "primary", "secondary" })
            {
                var window = UsageParser.Get(limit, name);
                if (window.ValueKind != JsonValueKind.Object) continue;
                var used = Number(window, "usedPercent");
                var minutes = Number(window, "windowDurationMins");
                var reset = Number(window, "resetsAt");
                windows.Add(new(id, label, used.HasValue ? Math.Clamp(100 - used.Value, 0, 100) : null,
                    minutes is > 0 and <= 5259600000 ? minutes * 60 : null,
                    reset is >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)reset) : null));
            }
        }
    }

    private static double? Number(JsonElement element, string name) =>
        UsageParser.Get(element, name) is { ValueKind: JsonValueKind.Number } number &&
        number.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
}
