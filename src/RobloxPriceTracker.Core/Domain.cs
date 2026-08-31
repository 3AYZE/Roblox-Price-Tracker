namespace RobloxPriceTracker.Core;

public enum CatalogItemType
{
    Asset = 1
}

public readonly record struct ItemKey(CatalogItemType Type, long Id)
{
    public override string ToString() => $"{Type}:{Id}";

    public static bool TryParse(string value, out ItemKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Enum.TryParse(parts[0], true, out CatalogItemType type) || !long.TryParse(parts[1], out var id) || id <= 0)
        {
            return false;
        }

        key = new ItemKey(type, id);
        return true;
    }
}

public enum MarketStatus
{
    Unknown = 0,
    Available = 1,
    NoResellers = 2,
    OffSale = 3,
    MissingFromResponse = 4,
    Unsupported = 5,
    InvalidPrice = 6
}

public sealed record MarketObservation(
    ItemKey ItemKey,
    string? Name,
    MarketStatus Status,
    long? LowestResalePrice,
    bool IsResaleCapable,
    DateTimeOffset ObservedAtUtc,
    string? Diagnostic = null)
{
    public bool HasActionablePrice =>
        Status == MarketStatus.Available && LowestResalePrice is > 0;
}

public sealed record TrackedItem(
    ItemKey ItemKey,
    string Name,
    bool Enabled,
    DateTimeOffset AddedAtUtc);

public sealed record MarketState(
    ItemKey ItemKey,
    MarketStatus ObservedStatus,
    long? CurrentLowestPrice,
    long? LastValidPrice,
    long? TrackedLow,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastPriceAtUtc,
    DateTimeOffset? LastHistoryAtUtc,
    long LastPollSequence)
{
    public static MarketState Empty(ItemKey key) => new(
        key,
        MarketStatus.Unknown,
        null,
        null,
        null,
        null,
        null,
        null,
        0);
}

public enum AlertRuleType
{
    TargetPrice = 1,
    NewTrackedLow = 2
}

public enum AlertState
{
    Armed = 1,
    Triggered = 2,
    Disabled = 3
}

public sealed record AlertRule(
    long Id,
    ItemKey ItemKey,
    AlertRuleType RuleType,
    long? Threshold,
    AlertState State,
    int RearmBasisPoints,
    bool Enabled,
    DateTimeOffset? LastTriggeredAtUtc)
{
    public static AlertRule NewTarget(ItemKey key, long threshold, int rearmBasisPoints = 100) =>
        new(0, key, AlertRuleType.TargetPrice, threshold, AlertState.Armed, rearmBasisPoints, true, null);

    public static AlertRule NewTrackedLow(ItemKey key) =>
        new(0, key, AlertRuleType.NewTrackedLow, null, AlertState.Armed, 0, true, null);
}

public sealed record TrackerItemSnapshot(
    TrackedItem Item,
    MarketState Market,
    IReadOnlyList<AlertRule> Rules);

public enum AlertEventType
{
    TargetReached = 1,
    NewTrackedLow = 2,
    TargetApproaching = 3
}

public sealed record AlertEmission(
    long RuleId,
    AlertEventType EventType,
    long? OldPrice,
    long NewPrice,
    DateTimeOffset CreatedAtUtc,
    string Title,
    string Body,
    bool QueueNotification = true);

public sealed record RuleMutation(AlertRule Rule);

public sealed record ObservationDecision(
    TrackerItemSnapshot UpdatedSnapshot,
    bool ShouldWriteHistory,
    IReadOnlyList<RuleMutation> RuleMutations,
    IReadOnlyList<AlertEmission> Alerts,
    long ExpectedPreviousPollSequence,
    bool IgnoredAsOldObservation = false);
