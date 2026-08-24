namespace RobloxPriceTracker.Core;

public sealed class PollPlanner
{
    public TimeSpan NormalInterval { get; }
    public TimeSpan NearTargetInterval { get; }
    public decimal NearTargetRatio { get; }

    public PollPlanner(TimeSpan? normalInterval = null, TimeSpan? nearTargetInterval = null, decimal nearTargetRatio = 1.10m)
    {
        NormalInterval = normalInterval ?? TimeSpan.FromSeconds(60);
        NearTargetInterval = nearTargetInterval ?? TimeSpan.FromSeconds(25);
        NearTargetRatio = nearTargetRatio;
    }

    public TimeSpan GetNextDelay(IReadOnlyCollection<TrackerItemSnapshot> snapshots, RateLimitGovernor governor)
    {
        var desired = snapshots.Any(IsNearArmedTarget) ? NearTargetInterval : NormalInterval;
        var governed = governor.RemainingDelay;
        return governed > desired ? governed : desired;
    }

    private bool IsNearArmedTarget(TrackerItemSnapshot snapshot)
    {
        if (snapshot.Market.CurrentLowestPrice is not > 0)
        {
            return false;
        }

        var current = snapshot.Market.CurrentLowestPrice.Value;
        return snapshot.Rules.Any(rule =>
            rule.Enabled &&
            rule.RuleType == AlertRuleType.TargetPrice &&
            rule.State == AlertState.Armed &&
            rule.Threshold is > 0 &&
            current <= rule.Threshold.Value * NearTargetRatio);
    }
}
