using GitHub.Copilot;

namespace Atalaya.Copilot;

/// <summary>
/// Isolation layer over the SDK's usage event (§6.3). The official docs only show this event
/// with TypeScript names; this class is the single place that touches the C# shape, so a package
/// change is absorbed here. Token/cost fields are read defensively regardless of numeric type.
/// Cache-token fields (Hito 1a) are read reflectively because the SDK exposes them under a name
/// that has moved across versions; when the property is absent we simply report 0 (evidence-only,
/// no invented data).
/// </summary>
public static class UsageAdapter
{
    // Best-effort names seen across SDK versions and matching upstream JS naming.
    private static readonly string[] CacheReadNames =
        { "CacheReadInputTokens", "CacheReadTokens", "CachedInputTokens", "CacheRead" };
    private static readonly string[] CacheWriteNames =
        { "CacheWriteInputTokens", "CacheWriteTokens", "CacheCreationInputTokens", "CacheWrite" };
    private static readonly string[] CostUnitNames =
        { "Currency", "CostUnit", "Unit", "CostCurrency" };

    public static UsageSample From(AssistantUsageData data)
        => new(
            ToLong(data.InputTokens),
            ToLong(data.OutputTokens),
            ToDecimalOrNull(data.Cost),
            data.Model,
            ReadLong(data, CacheReadNames),
            ReadLong(data, CacheWriteNames),
            ReadString(data, CostUnitNames));

    private static long ToLong(object? value) => value is null ? 0 : Convert.ToInt64(value);

    private static decimal? ToDecimalOrNull(object? value)
        => value is null ? null : Convert.ToDecimal(value);

    private static long ReadLong(object source, string[] candidateNames)
    {
        Type type = source.GetType();
        foreach (string name in candidateNames)
        {
            var prop = type.GetProperty(name);
            if (prop is null)
            {
                continue;
            }

            object? value = prop.GetValue(source);
            if (value is not null)
            {
                try { return Convert.ToInt64(value); }
                catch { return 0; }
            }
        }

        return 0;
    }

    private static string? ReadString(object source, string[] candidateNames)
    {
        Type type = source.GetType();
        foreach (string name in candidateNames)
        {
            var prop = type.GetProperty(name);
            if (prop is null)
            {
                continue;
            }

            object? value = prop.GetValue(source);
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return null;
    }
}
