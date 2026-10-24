using System;
using System.Collections.Generic;
using System.Linq;
using EmberHUD;

namespace Prices;

public static class Valuation
{
    public const int FeBaseId = 100300;

    public static Snapshot Value(IReadOnlyList<InventoryItem> items, IReadOnlyDictionary<int, Price> prices, DateTimeOffset at, int areaId, string areaName, bool inTown)
    {
        var lines = items.GroupBy(item => item.BaseId).Select(group =>
        {
            var count = group.Sum(item => item.Count);
            var value = group.Sum(item => Worth(item, prices));
            return new Line(group.Key, count, count == 0 ? 0 : value / count, value);
        }).ToArray();
        var fe = lines.Where(line => line.BaseId == FeBaseId).Sum(line => line.Value);
        var unpriced = lines.Count(line => line.BaseId != FeBaseId && !prices.ContainsKey(line.BaseId));
        return new Snapshot(at, areaId, areaName, inTown, lines.Sum(line => line.Value), fe, unpriced, lines);
    }

    public static Line[] Diff(Line[] before, Line[] after, IReadOnlyDictionary<int, Price> prices)
    {
        var counts = new Dictionary<int, int>();
        foreach (var line in after) counts[line.BaseId] = counts.GetValueOrDefault(line.BaseId) + line.Count;
        foreach (var line in before) counts[line.BaseId] = counts.GetValueOrDefault(line.BaseId) - line.Count;
        return counts.Where(pair => pair.Value != 0).Select(pair =>
        {
            var unit = UnitPrice(pair.Key, prices);
            return new Line(pair.Key, pair.Value, unit, pair.Value * unit);
        }).ToArray();
    }

    static float UnitPrice(int baseId, IReadOnlyDictionary<int, Price> prices) =>
        baseId == FeBaseId ? 1f : prices.TryGetValue(baseId, out var price) ? price.Median : 0f;

    static float Worth(InventoryItem item, IReadOnlyDictionary<int, Price> prices)
    {
        if (item.BaseId == FeBaseId) return item.Count;
        if (!prices.TryGetValue(item.BaseId, out var price)) return 0f;
        return item.Bag == InventoryBag.Equipment ? (price.Low ?? price.Median) * item.Count : price.Median * item.Count;
    }
}
