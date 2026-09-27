using RobloxPriceTracker.Core;

namespace RobloxPriceTracker.Infrastructure;

// Tracker records are never physically deleted by the UI: REMOVE changes Enabled to false.
// Missing keys therefore indicate an incomplete file, not an intentional user removal.
public static class TrackerStateRecovery
{
    public static int Merge(JsonFileRepository.Store current, JsonFileRepository.Store evidence)
    {
        JsonFileRepository.NormalizeCounters(current);
        JsonFileRepository.NormalizeCounters(evidence);

        var missing = evidence.Items.Keys
            .Where(key => !current.Items.ContainsKey(key))
            .ToArray();
        if (missing.Length == 0 && evidence.PriceHistory.Count <= current.PriceHistory.Count)
            return 0;

        foreach (var key in missing)
        {
            current.Items[key] = evidence.Items[key];
            if (evidence.MarketStates.TryGetValue(key, out var market))
                current.MarketStates[key] = market;
        }

        // Preserve the state/target of an already-present item. Saved records of deliberately
        // disabled items must not be re-enabled by an older snapshot.
        var ruleByKind = current.Rules.ToDictionary(
            rule => (rule.ItemKey, rule.RuleType));
        var usedRuleIds = current.Rules.Select(rule => rule.Id).ToHashSet();
        var nextRuleId = Math.Max(current.NextRuleId, usedRuleIds.Count == 0 ? 1 : usedRuleIds.Max() + 1);
        var ruleIds = new Dictionary<long, long>();
        foreach (var rule in evidence.Rules)
        {
            if (ruleByKind.TryGetValue((rule.ItemKey, rule.RuleType), out var existing))
            {
                ruleIds[rule.Id] = existing.Id;
                continue;
            }
            var id = usedRuleIds.Add(rule.Id) ? rule.Id : nextRuleId++;
            var restored = rule with { Id = id };
            current.Rules.Add(restored);
            ruleByKind[(rule.ItemKey, rule.RuleType)] = restored;
            ruleIds[rule.Id] = id;
            nextRuleId = Math.Max(nextRuleId, id + 1);
        }

        var seenPrices = current.PriceHistory.Select(x =>
            (x.ItemKey, x.ObservedAtUtc, x.Price, x.Status, x.PollSequence)).ToHashSet();
        foreach (var sample in evidence.PriceHistory)
        {
            if (seenPrices.Add((sample.ItemKey, sample.ObservedAtUtc, sample.Price, sample.Status, sample.PollSequence)))
                current.PriceHistory.Add(sample);
        }

        var eventsByKey = current.AlertEvents.ToDictionary(
            x => (x.ItemKey, x.EventType, x.CreatedAtUtc, x.NewPrice),
            x => x.Id);
        var usedEventIds = current.AlertEvents.Select(x => x.Id).ToHashSet();
        var nextEventId = Math.Max(current.NextAlertEventId, usedEventIds.Count == 0 ? 1 : usedEventIds.Max() + 1);
        var eventIds = new Dictionary<long, long>();
        foreach (var entry in evidence.AlertEvents)
        {
            var identity = (entry.ItemKey, entry.EventType, entry.CreatedAtUtc, entry.NewPrice);
            if (eventsByKey.TryGetValue(identity, out var existingId))
            {
                eventIds[entry.Id] = existingId;
                continue;
            }
            if (!ruleIds.TryGetValue(entry.RuleId, out var ruleId))
                continue;
            var id = usedEventIds.Add(entry.Id) ? entry.Id : nextEventId++;
            current.AlertEvents.Add(entry with { Id = id, RuleId = ruleId });
            eventsByKey[identity] = id;
            eventIds[entry.Id] = id;
            nextEventId = Math.Max(nextEventId, id + 1);
        }

        var usedNotificationIds = current.Notifications.Select(x => x.Id).ToHashSet();
        var nextNotificationId = Math.Max(current.NextNotificationId,
            usedNotificationIds.Count == 0 ? 1 : usedNotificationIds.Max() + 1);
        var notificationKeys = current.Notifications
            .Select(x => (x.AlertEventId, x.Title, x.CreatedAtUtc))
            .ToHashSet();
        foreach (var notification in evidence.Notifications)
        {
            if (!eventIds.TryGetValue(notification.AlertEventId, out var eventId))
                continue;
            if (!notificationKeys.Add((eventId, notification.Title, notification.CreatedAtUtc)))
                continue;
            var id = usedNotificationIds.Add(notification.Id) ? notification.Id : nextNotificationId++;
            current.Notifications.Add(notification with { Id = id, AlertEventId = eventId });
            nextNotificationId = Math.Max(nextNotificationId, id + 1);
        }

        JsonFileRepository.NormalizeCounters(current);
        return missing.Length;
    }
}
