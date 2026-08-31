namespace RobloxPriceTracker.Core;

public sealed class AlertEngine
{
    private const int NearTargetBasisPoints = 1_000; // 10% above target.
    private const int MeaningfulDropBasisPoints = 200; // 2% below the previous tracked low.
    private static readonly TimeSpan PriceAlertCooldown = TimeSpan.FromHours(1);

    private readonly TimeSpan _historyHeartbeat;

    public AlertEngine(TimeSpan? historyHeartbeat = null)
    {
        _historyHeartbeat = historyHeartbeat ?? TimeSpan.FromHours(3);
    }

    public ObservationDecision Evaluate(
        TrackerItemSnapshot snapshot,
        MarketObservation observation,
        long pollSequence,
        DateTimeOffset nowUtc)
    {
        if (observation.ItemKey != snapshot.Item.ItemKey)
        {
            throw new InvalidOperationException("Observation item does not match snapshot item.");
        }

        if (pollSequence <= snapshot.Market.LastPollSequence)
        {
            return new ObservationDecision(snapshot, false, Array.Empty<RuleMutation>(), Array.Empty<AlertEmission>(), snapshot.Market.LastPollSequence, true);
        }

        if (observation.Status == MarketStatus.MissingFromResponse)
        {
            return new ObservationDecision(snapshot, false, Array.Empty<RuleMutation>(), Array.Empty<AlertEmission>(), snapshot.Market.LastPollSequence);
        }

        var oldMarket = snapshot.Market;
        var currentPrice = observation.HasActionablePrice ? observation.LowestResalePrice : null;
        var oldTrackedLow = oldMarket.TrackedLow;
        var newTrackedLow = oldTrackedLow;
        var lastValidPrice = oldMarket.LastValidPrice;
        var lastPriceAtUtc = oldMarket.LastPriceAtUtc;

        if (observation.HasActionablePrice)
        {
            var validPrice = observation.LowestResalePrice!.Value;
            if (!PriceValidation.IsValidResalePrice(validPrice))
            {
                currentPrice = null;
                observation = observation with
                {
                    Status = MarketStatus.InvalidPrice,
                    LowestResalePrice = null,
                    Diagnostic = $"Provider returned invalid resale price: {validPrice}."
                };
            }
            else
            {
                lastValidPrice = validPrice;
                lastPriceAtUtc = nowUtc;
                newTrackedLow = oldTrackedLow is null ? validPrice : Math.Min(oldTrackedLow.Value, validPrice);
            }
        }

        var statusChanged = oldMarket.ObservedStatus != observation.Status;
        var priceChanged = oldMarket.CurrentLowestPrice != currentPrice;
        var heartbeatDue = oldMarket.LastHistoryAtUtc is null || nowUtc - oldMarket.LastHistoryAtUtc >= _historyHeartbeat;
        var shouldWriteHistory = statusChanged || priceChanged || heartbeatDue;

        var updatedMarket = oldMarket with
        {
            ObservedStatus = observation.Status,
            CurrentLowestPrice = currentPrice,
            LastValidPrice = lastValidPrice,
            TrackedLow = newTrackedLow,
            LastSuccessAtUtc = nowUtc,
            LastPriceAtUtc = lastPriceAtUtc,
            LastHistoryAtUtc = shouldWriteHistory ? nowUtc : oldMarket.LastHistoryAtUtc,
            LastPollSequence = pollSequence
        };

        var mutations = new List<RuleMutation>();
        var alerts = new List<AlertEmission>();
        var updatedRules = new List<AlertRule>(snapshot.Rules.Count);

        foreach (var rule in snapshot.Rules)
        {
            var updatedRule = EvaluateRule(snapshot.Item, oldMarket, snapshot.Rules, rule, observation, nowUtc, alerts);
            updatedRules.Add(updatedRule);
            if (updatedRule != rule)
            {
                mutations.Add(new RuleMutation(updatedRule));
            }
        }

        CoalesceNotifications(alerts);

        var updatedSnapshot = snapshot with
        {
            Market = updatedMarket,
            Rules = updatedRules
        };

        return new ObservationDecision(updatedSnapshot, shouldWriteHistory, mutations, alerts, oldMarket.LastPollSequence);
    }

    private static AlertRule EvaluateRule(
        TrackedItem item,
        MarketState oldMarket,
        IReadOnlyList<AlertRule> allRules,
        AlertRule rule,
        MarketObservation observation,
        DateTimeOffset nowUtc,
        List<AlertEmission> alerts)
    {
        if (!rule.Enabled || rule.State == AlertState.Disabled || !observation.HasActionablePrice)
        {
            return rule;
        }

        var price = observation.LowestResalePrice!.Value;
        if (!PriceValidation.IsValidResalePrice(price))
        {
            return rule;
        }

        switch (rule.RuleType)
        {
            case AlertRuleType.TargetPrice:
            {
                if (rule.Threshold is null)
                {
                    return rule;
                }

                var threshold = rule.Threshold.Value;
                PriceValidation.ThrowIfInvalidTarget(threshold);

                if (rule.State == AlertState.Armed && price <= threshold)
                {
                    alerts.Add(new AlertEmission(
                        rule.Id,
                        AlertEventType.TargetReached,
                        oldMarket.CurrentLowestPrice,
                        price,
                        nowUtc,
                        $"{item.Name} hit your target",
                        $"Current lowest reseller: {price:N0} R$ | Target: {threshold:N0} R$"));

                    return rule with
                    {
                        State = AlertState.Triggered,
                        LastTriggeredAtUtc = nowUtc
                    };
                }

                if (rule.State == AlertState.Armed && ShouldNotifyApproachingTarget(oldMarket, price, threshold, rule.LastTriggeredAtUtc, nowUtc))
                {
                    var previous = oldMarket.CurrentLowestPrice ?? oldMarket.TrackedLow ?? price;
                    var distancePct = Math.Max(0d, (price - threshold) / (double)threshold * 100d);
                    alerts.Add(new AlertEmission(
                        rule.Id,
                        AlertEventType.TargetApproaching,
                        previous,
                        price,
                        nowUtc,
                        $"{item.Name} is approaching your target",
                        $"Price is falling: {previous:N0} R$ → {price:N0} R$ | Target: {threshold:N0} R$ | {distancePct:0.#}% above target"));

                    return rule with { LastTriggeredAtUtc = nowUtc };
                }

                if (rule.State == AlertState.Triggered && price >= CalculateRearmPrice(threshold, rule.RearmBasisPoints))
                {
                    return rule with { State = AlertState.Armed };
                }

                return rule;
            }

            case AlertRuleType.NewTrackedLow:
            {
                if (oldMarket.TrackedLow is null || price >= oldMarket.TrackedLow.Value)
                {
                    return rule;
                }

                // A configured target owns the notification path. The target rule will alert only
                // when the price is meaningfully falling near the target, and again when it is hit.
                // This prevents the default new-low rule from sending a notification for every tiny tick.
                var hasEnabledTarget = allRules.Any(x =>
                    x.Enabled &&
                    x.State != AlertState.Disabled &&
                    x.RuleType == AlertRuleType.TargetPrice &&
                    x.Threshold is > 0);
                if (hasEnabledTarget)
                {
                    return rule;
                }

                if (!IsMeaningfulNewLow(oldMarket.TrackedLow.Value, price) || !CooldownElapsed(rule.LastTriggeredAtUtc, nowUtc))
                {
                    return rule;
                }

                alerts.Add(new AlertEmission(
                    rule.Id,
                    AlertEventType.NewTrackedLow,
                    oldMarket.TrackedLow,
                    price,
                    nowUtc,
                    $"{item.Name} hit a meaningful new low",
                    $"Tracked low: {oldMarket.TrackedLow.Value:N0} R$ → {price:N0} R$"));

                return rule with { LastTriggeredAtUtc = nowUtc };
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(rule.RuleType), rule.RuleType, "Unknown alert rule type.");
        }
    }

    private static bool ShouldNotifyApproachingTarget(
        MarketState oldMarket,
        long price,
        long threshold,
        DateTimeOffset? lastTriggeredAtUtc,
        DateTimeOffset nowUtc)
    {
        if (price <= threshold || price > CalculateNearTargetCeiling(threshold))
        {
            return false;
        }

        if (!CooldownElapsed(lastTriggeredAtUtc, nowUtc))
        {
            return false;
        }

        if (oldMarket.CurrentLowestPrice is not > 0 || price >= oldMarket.CurrentLowestPrice.Value)
        {
            return false;
        }

        // Require an actual new tracked low so a temporary bounce-and-return does not create noise.
        if (oldMarket.TrackedLow is not > 0 || price >= oldMarket.TrackedLow.Value)
        {
            return false;
        }

        return IsMeaningfulNewLow(oldMarket.TrackedLow.Value, price);
    }

    private static bool IsMeaningfulNewLow(long previousLow, long price)
    {
        if (previousLow <= 0 || price <= 0 || price >= previousLow)
        {
            return false;
        }

        var requiredDrop = Math.Max(1L, (long)Math.Ceiling(previousLow * (MeaningfulDropBasisPoints / 10_000d)));
        return previousLow - price >= requiredDrop;
    }

    private static bool CooldownElapsed(DateTimeOffset? lastTriggeredAtUtc, DateTimeOffset nowUtc) =>
        lastTriggeredAtUtc is null || nowUtc - lastTriggeredAtUtc.Value >= PriceAlertCooldown;

    public static long CalculateNearTargetCeiling(long threshold)
    {
        PriceValidation.ThrowIfInvalidTarget(threshold);
        var margin = Math.Max(1L, (long)Math.Ceiling(threshold * (NearTargetBasisPoints / 10_000d)));
        return Math.Min(PriceValidation.MaxRobux, threshold + margin);
    }

    private static void CoalesceNotifications(List<AlertEmission> alerts)
    {
        if (alerts.Count <= 1)
        {
            return;
        }

        var winnerIndex = alerts.FindIndex(x => x.EventType == AlertEventType.TargetReached);
        if (winnerIndex < 0)
        {
            winnerIndex = alerts.FindIndex(x => x.EventType == AlertEventType.TargetApproaching);
        }
        if (winnerIndex < 0)
        {
            winnerIndex = 0;
        }

        var winner = alerts[winnerIndex];
        var hasNewLow = alerts.Any(x => x.EventType == AlertEventType.NewTrackedLow);
        if (winner.EventType == AlertEventType.TargetReached && hasNewLow)
        {
            winner = winner with
            {
                Title = winner.Title + " + new tracked low",
                Body = winner.Body + " | This price is also a new tracked low."
            };
            alerts[winnerIndex] = winner;
        }

        for (var i = 0; i < alerts.Count; i++)
        {
            if (i != winnerIndex)
            {
                alerts[i] = alerts[i] with { QueueNotification = false };
            }
        }
    }

    public static long CalculateRearmPrice(long threshold, int rearmBasisPoints)
    {
        PriceValidation.ThrowIfInvalidTarget(threshold);
        if (rearmBasisPoints < 0 || rearmBasisPoints > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(rearmBasisPoints));
        }

        var margin = Math.Max(1L, checked((threshold * rearmBasisPoints + 9_999L) / 10_000L));
        return checked(threshold + margin);
    }
}
