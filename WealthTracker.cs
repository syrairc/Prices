using System;
using System.Collections.Generic;
using System.Linq;
using EmberHUD;

namespace Prices;

public sealed class WealthTracker(Func<DateTimeOffset> now)
{
    static readonly TimeSpan StashEvery = TimeSpan.FromSeconds(3);

    readonly RunTracker runs = new();
    IReadOnlyDictionary<int, Price> prices = new Dictionary<int, Price>();
    (int AreaId, string Name)? lastArea;
    DateTimeOffset lastSnapshotAt;
    int? lastTab;
    DateTimeOffset lastTabAt;

    public Func<string, WealthStore> Open { get; set; } = _ => new WealthStore();

    public string? Key { get; private set; }

    public WealthStore? Store { get; private set; }

    public Snapshot? Last { get; private set; }

    public bool Paused { get; set; }

    public (string Key, WealthStore Store)? Retired { get; set; }

    public Run? CurrentRun => runs.Open is { } open && Last is { } last
        ? new Run(open.At, last.At, open.AreaName, open.Total, last.Total, runs.Before is null, last.Unpriced > 0, Valuation.Diff(open.Lines, last.Lines, prices), [])
        : null;

    public bool Tick(GameState state, IReadOnlyDictionary<int, Price> current, int? season, int snapshotMinutes, string? character)
    {
        if (Paused || string.IsNullOrEmpty(character) || season is not { } known || state.Inventory is not { } bag || state.Area is not { } area) return false;
        prices = current;
        var key = $"{character}-{known}";
        if (key != Key) Switch(key);
        var store = Store!;
        var at = now();
        var added = false;
        StashTab? stored = null;
        if (state.Stash is { } stash && state.StashItems is { } items && (lastTab != stash.Tab || at - lastTabAt >= StashEvery))
        {
            stored = new StashTab(stash.Tab, at, Valuation.Value(items, prices, at, state.AreaId, area.Name, area.IsTown).Lines);
            store.SetTab(stored);
            (lastTab, lastTabAt, added) = (stash.Tab, at, true);
        }
        var due = lastArea != (state.AreaId, area.Name) || at - lastSnapshotAt >= TimeSpan.FromMinutes(Math.Max(1, snapshotMinutes));
        if (!due) return added;
        var snapshot = Valuation.Value(bag, prices, at, state.AreaId, area.Name, area.IsTown);
        store.AddSnapshot(snapshot, stored is null ? [] : [stored], Names(snapshot, store));
        if (runs.OnSnapshot(snapshot, prices) is { } run) store.AddRun(run);
        (Last, lastArea, lastSnapshotAt) = (snapshot, (state.AreaId, area.Name), at);
        return true;
    }

    public void Reset()
    {
        Store?.Clear();
        runs.Reset();
        (Last, lastArea, lastTab) = (null, null, null);
    }

    void Switch(string key)
    {
        if (Key is { } old && Store is { } oldStore) Retired = (old, oldStore);
        (Key, Store) = (key, Open(key));
        runs.Reset();
        (Last, lastArea, lastTab) = (null, null, null);
    }

    static Dictionary<int, string> Names(Snapshot snapshot, WealthStore store)
    {
        var names = new Dictionary<int, string>();
        foreach (var id in snapshot.Lines.Concat(store.Data.Tabs.SelectMany(tab => tab.Lines)).Select(line => line.BaseId).Distinct())
            if (ItemNames.Of(id) is { Length: > 0 } name) names[id] = name;
        return names;
    }
}
