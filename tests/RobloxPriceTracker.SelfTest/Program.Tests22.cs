using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcDiscoverySelectionPreservesSourceDiversityAsync()
    {
        var ranked = Enumerable.Range(1, 210).Select(x => (long)x).ToArray();
        var feeds = Enumerable.Range(0, 7)
            .Select(feed => (IReadOnlyList<long>)Enumerable.Range(1, 30)
                .Select(index => (long)(feed * 30 + index)).ToArray())
            .ToArray();
        var recent = Enumerable.Range(301, 35).Select(x => (long)x).ToArray();
        var emotes = Enumerable.Range(401, 12).Select(x => (long)x).ToArray();

        var selected = UgcDiscoverySelectionPolicy.Select(ranked, feeds, recent, emotes, capacity: 120);
        AssertEqual(120, selected.Count);
        AssertEqual(120, selected.Distinct().Count());
        AssertTrue(emotes.Take(UgcDiscoverySelectionPolicy.EmoteReserve).All(id => selected.Contains(id)));
        AssertTrue(emotes.Skip(UgcDiscoverySelectionPolicy.EmoteReserve).All(id => !selected.Contains(id)));
        AssertTrue(recent.Take(20).All(id => selected.Contains(id)));
        AssertTrue(feeds.All(feed => feed.Take(4).All(id => selected.Contains(id))));
        AssertTrue(selected.SequenceEqual(
            UgcDiscoverySelectionPolicy.Select(ranked, feeds, recent, emotes, capacity: 120)));

        // Mixed overlapping sources never duplicate IDs or exceed request budget.
        var small = UgcDiscoverySelectionPolicy.Select(
            [1, 2, 3, 4, 5, 6],
            [[1, 2, 3], [1, 2, 4], [4, 5, 6]],
            [5, 6, 7], [2, 2, 8], capacity: 5);
        AssertEqual(5, small.Count);
        AssertEqual(5, small.Distinct().Count());
        AssertEqual(0, UgcDiscoverySelectionPolicy.Select(ranked, feeds, recent, emotes, 0).Count);
        // A verified asset-type hint reserves different accessory categories within the same 120-ID budget.
        var typed = ranked.Take(24).ToDictionary(id => id, id => (int)(8 + (id - 1) / 4));
        foreach (var id in emotes) typed[id] = 61;
        var typedSelected = UgcDiscoverySelectionPolicy.Select(ranked, feeds, recent, emotes, 120, typed);
        AssertTrue(Enumerable.Range(0, 6).All(group =>
            ranked.Skip(group * 4).Take(4).All(id => typedSelected.Contains(id))));
        AssertTrue(typedSelected.Count(id => typed.TryGetValue(id, out var type) && type == 61) <=
            UgcDiscoverySelectionPolicy.MaximumEmotes);
        return Task.CompletedTask;
    }

    static Task TestUgcDiscoveryEmoteReserveRequiresTypedSourceAsync()
    {
        const string json = """
        {"data":[
          {"id":7001,"itemType":"Asset","assetType":8,"creatorTargetId":22,"itemRestrictions":["Collectible"]},
          {"id":7002,"itemType":"Asset","assetType":61,"creatorTargetId":23,"itemRestrictions":["Limited"]},
          {"id":7003,"itemType":"Asset","creatorTargetId":24,"itemRestrictions":["Collectible"]},
          {"id":7004,"itemType":"Asset","assetType":61,"creatorTargetId":1,"itemRestrictions":["Collectible"]},
          {"id":7005,"itemType":"Asset","assetType":999,"creatorTargetId":25,"itemRestrictions":["Limited"]},
          {"id":7006,"itemType":"Bundle","assetType":61,"creatorTargetId":26}
        ]}
        """;
        using var document = JsonDocument.Parse(json);
        var hits = RobloxUgcCatalogDiscoveryParser.ParseDiscoveryHits(document.RootElement);
        AssertEqual(3, hits.Count);
        AssertEqual(7001L, hits[0].Id);
        AssertEqual(8, hits[0].AssetType);
        AssertEqual(7002L, hits[1].Id);
        AssertEqual(61, hits[1].AssetType);
        AssertEqual(7003L, hits[2].Id);
        AssertEqual(0, hits[2].AssetType);
        AssertEqual(1, hits.Count(x => x.AssetType == 61));
        AssertTrue(hits.Select(x => x.Id).SequenceEqual(
            RobloxUgcCatalogDiscoveryParser.ParseDiscoveryIds(document.RootElement)));
        return Task.CompletedTask;
    }
}
