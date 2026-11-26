using System.Collections.ObjectModel;
using System.Collections.Specialized;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static Task TestUgcRefreshReconcileAsync()
    {
        var previous = new[] { (Id: 1, Price: 95, Fresh: true),
            (Id: 2, Price: 95, Fresh: true), (Id: 3, Price: 95, Fresh: true),
            (Id: 4, Price: 95, Fresh: false) };
        var next = new[] { (Id: 1, Price: 150, Fresh: true), (Id: 5, Price: 65, Fresh: true) };
        var merged = KeyedSnapshotReconciler.MergeFresh(previous, next,
            x => x.Id, new[] { 3 }, x => x.Fresh);
        AssertTrue(merged.Select(x => x.Id).SequenceEqual(new[] { 1, 5, 2 }));
        AssertEqual(150, merged[0].Price); // new over-ceiling quote must replace old affordable quote
        AssertTrue(!UgcCategoryBuyingPricePolicy.IsLiveScannerListing(
            UgcCategoryBuyingPricePolicy.Good, merged[0].Price, 95,
            false, 95, 8, "UNKNOWN"));
        var rows = new ObservableCollection<(int Id, int Price, bool Fresh)>(previous);
        var resets = 0;
        var emptied = false;
        rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
            if (rows.Count == 0) emptied = true;
        };
        KeyedSnapshotReconciler.Apply(rows, merged, x => x.Id);
        AssertTrue(rows.SequenceEqual(merged));
        AssertEqual(0, resets);
        AssertTrue(!emptied);
        KeyedSnapshotReconciler.Apply(rows, new[] { (Id: 8, Price: 55, Fresh: true) }, x => x.Id);
        AssertEqual(0, resets);
        AssertTrue(!emptied);
        AssertEqual(8, rows.Single().Id);
        KeyedSnapshotReconciler.Apply(rows, Array.Empty<(int Id, int Price, bool Fresh)>(), x => x.Id);
        AssertEqual(0, rows.Count); // confirmed empty still allowed
        return Task.CompletedTask;
    }
}
