using GitHub.Copilot;

namespace Atalaya.Copilot;

/// <summary>
/// Isolation layer over the SDK's usage event (§6.3). The official docs only show this event
/// with TypeScript names; this class is the single place that touches the C# shape, so a package
/// change is absorbed here. Token/cost fields are read defensively regardless of numeric type.
/// </summary>
public static class UsageAdapter
{
    public static UsageSample From(AssistantUsageData data)
        => new(ToLong(data.InputTokens), ToLong(data.OutputTokens), ToDecimalOrNull(data.Cost), data.Model);

    private static long ToLong(object? value) => value is null ? 0 : Convert.ToInt64(value);

    private static decimal? ToDecimalOrNull(object? value)
        => value is null ? null : Convert.ToDecimal(value);
}
