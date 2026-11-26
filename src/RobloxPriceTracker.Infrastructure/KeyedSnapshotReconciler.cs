using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Merge independently verified snapshots without treating an omitted discovery
/// candidate as a confirmed stockout. New observations and invalidations win;
/// retained records keep their original verification timestamps.
/// </summary>
public static class KeyedSnapshotReconciler
{
    public static T[] MergeFresh<T, TKey>(IEnumerable<T> previous, IEnumerable<T> incoming,
        Func<T, TKey> key, IEnumerable<TKey> invalidated, Func<T, bool> isFresh)
        where TKey : notnull
    {
        var rejected = new HashSet<TKey>(invalidated);
        var result = new List<T>();
        var seen = new HashSet<TKey>();
        foreach (var row in incoming)
            if (!rejected.Contains(key(row)) && seen.Add(key(row))) result.Add(row);
        foreach (var row in previous)
            if (!rejected.Contains(key(row)) && !seen.Contains(key(row)) && isFresh(row))
            {
                seen.Add(key(row));
                result.Add(row);
            }
        return result.ToArray();
    }

    /// <summary>Update an existing UI collection by identity, never by Reset/Clear.</summary>
    public static void Apply<T, TKey>(ObservableCollection<T> target, IEnumerable<T> incoming,
        Func<T, TKey> key, Func<T, T, bool>? equivalent = null) where TKey : notnull
    {
        equivalent ??= EqualityComparer<T>.Default.Equals;
        var desired = incoming.GroupBy(key).Select(g => g.First()).ToArray();
        var desiredKeys = new HashSet<TKey>(desired.Select(key));
        // Insert new items before removing old ones to avoid a transient empty list.
        var indices = new Dictionary<TKey, int>();
        for (var i = 0; i < target.Count; i++) indices.TryAdd(key(target[i]), i);
        foreach (var item in desired)
        {
            if (indices.TryGetValue(key(item), out var index))
            {
                if (!equivalent(target[index], item)) target[index] = item;
            }
            else
            {
                indices[key(item)] = target.Count;
                target.Add(item);
            }
        }
        var retained = new HashSet<TKey>();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desiredKeys.Contains(key(target[i])) || !retained.Add(key(target[i])))
                target.RemoveAt(i);
        // Keep source ranking for views without a separate sort policy.
        var comparer = EqualityComparer<TKey>.Default;
        for (var i = 0; i < desired.Length; i++)
        {
            var wanted = key(desired[i]);
            if (comparer.Equals(key(target[i]), wanted)) continue;
            for (var j = i + 1; j < target.Count; j++)
                if (comparer.Equals(key(target[j]), wanted))
                {
                    target.Move(j, i);
                    break;
                }
        }
    }
}
