using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class WealthTrackerTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    static readonly Dictionary<int, Price> Prices = new() { [1] = new Price(10, null, null), [2] = new Price(4, null, null) };

    sealed class Clock
    {
        public DateTimeOffset At = T0;
        public void Advance(double seconds) => At = At.AddSeconds(seconds);
    }

    static (WealthTracker Tracker, Clock Clock) Make()
    {
        var clock = new Clock();
        var tracker = new WealthTracker(() => clock.At) { Open = _ => new WealthStore() };
        return (tracker, clock);
    }

    static InventoryItem Item(int baseId, int count, int slot = 0) => new(InventoryBag.Commodities, slot, "id", baseId, 0, 0, 0, count, 100, 0);

    static GameState Map(InventoryItem[]? bag, int areaId = 10, string name = "Map A") =>
        GameState.Empty with { AreaId = areaId, Area = new MapInfo(name, 1, null, null, false, [], []), Inventory = bag };

    static GameState Town(InventoryItem[]? bag) =>
        GameState.Empty with { AreaId = 1, Area = new MapInfo("Town", 1, null, null, true, [], []), Inventory = bag };

    static GameState WithStash(GameState state, int tab, InventoryItem[]? items) =>
        state with { Stash = new StashView(tab, default, default, []), StashItems = items };

    static bool Tick(WealthTracker tracker, GameState state, int minutes = 5, string? character = "Hero", int? season = 12, IReadOnlyDictionary<int, Price>? prices = null) =>
        tracker.Tick(state, prices ?? Prices, season, minutes, character);

    [Test]
    public void AreaChangeTakesSnapshot()
    {
        var (tracker, clock) = Make();
        Check.That(Tick(tracker, Map([Item(1, 2), Item(2, 1)])), "first snapshot");
        clock.Advance(60);
        Check.That(Tick(tracker, Town([Item(1, 2), Item(2, 1)])), "area change");
        Check.Equal(1, tracker.Store!.Data.Runs.Length);
        Check.Equal(2, tracker.Store.Data.Totals.Length);
        Check.Equal("Hero-12", tracker.Key);
    }

    [Test]
    public void NullInventoryWritesNothing()
    {
        var (tracker, _) = Make();
        Check.That(!Tick(tracker, Map(null)), "false");
        Check.That(tracker.Store is null || tracker.Store.Data.Totals.Length == 0, "nothing stored");
    }

    [Test]
    public void NullAreaWritesNothing()
    {
        var (tracker, _) = Make();
        Check.That(!Tick(tracker, Map([Item(1, 1)]) with { Area = null }), "false");
        Check.That(tracker.Store is null || tracker.Store.Data.Totals.Length == 0, "nothing stored");
    }

    [Test]
    public void UnknownCharacterWritesNothing()
    {
        var (tracker, _) = Make();
        Check.That(!Tick(tracker, Map([Item(1, 1)]), character: null), "null");
        Check.That(!Tick(tracker, Map([Item(1, 1)]), character: ""), "empty");
        Check.That(tracker.Store is null, "no store");
    }

    [Test]
    public void UnknownSeasonWritesNothing()
    {
        var (tracker, _) = Make();
        Check.That(!Tick(tracker, Map([Item(1, 1)]), season: null), "false");
        Check.That(tracker.Store is null, "no store");
    }

    [Test]
    public void TimerSnapshotAfterInterval()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Map([Item(1, 1)]));
        clock.Advance(60);
        Check.That(!Tick(tracker, Map([Item(1, 1)])), "one minute is too soon");
        clock.Advance(6 * 60);
        Check.That(Tick(tracker, Map([Item(1, 1)])), "six minutes");
        Check.Equal(2, tracker.Store!.Data.Totals.Length);
    }

    [Test]
    public void StashTabIsStoredWhenSeen()
    {
        var (tracker, _) = Make();
        Check.That(Tick(tracker, WithStash(Map([Item(1, 1)]), 1, [Item(2, 3)])), "stored");
        var tab = tracker.Store!.Data.Tabs.Single();
        Check.Equal(1, tab.Index);
        Check.Equal(2, tab.Lines.Single().BaseId);
        Check.Equal(10f + 12f, tracker.Store.Data.Totals[^1].Total);
    }

    [Test]
    public void BadStashReadKeepsLastGoodTab()
    {
        var (tracker, clock) = Make();
        Tick(tracker, WithStash(Map([Item(1, 1)]), 1, [Item(2, 3)]));
        clock.Advance(10);
        Tick(tracker, WithStash(Map([Item(1, 1)]), 1, null));
        Check.Equal(3, tracker.Store!.Data.Tabs.Single().Lines.Single().Count);
    }

    [Test]
    public void TabSeenEmptyIsStoredEmpty()
    {
        var (tracker, clock) = Make();
        Tick(tracker, WithStash(Map([Item(1, 1)]), 1, [Item(2, 3)]));
        clock.Advance(10);
        Tick(tracker, WithStash(Map([Item(1, 1)]), 1, []));
        Check.Equal(0, tracker.Store!.Data.Tabs.Single().Lines.Length);
    }

    [Test]
    public void CharacterChangeClosesRunWithoutDiff()
    {
        var (tracker, clock) = Make();
        WealthStore? first = null;
        Tick(tracker, Town([Item(1, 5)]));
        Tick(tracker, Map([Item(1, 5)]));
        first = tracker.Store;
        clock.Advance(120);
        Check.That(Tick(tracker, Map([Item(1, 1)]), character: "Other"), "snapshot for B");
        Check.Equal("Other-12", tracker.Key);
        Check.Equal(0, first!.Data.Runs.Length);
        Check.Equal(0, tracker.Store!.Data.Runs.Length);
        Check.Equal("Hero-12", tracker.Retired.Single().Key);
        Check.That(tracker.CurrentRun is not null, "B's run starts partial");
        Check.That(tracker.CurrentRun!.Partial, "partial");
    }

    [Test]
    public void PausedWritesNothing()
    {
        var (tracker, _) = Make();
        tracker.Paused = true;
        Check.That(!Tick(tracker, Map([Item(1, 1)])), "false");
        Check.That(tracker.Store is null, "no store");
    }

    [Test]
    public void ResetClearsStoreAndOpenRun()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Town([Item(1, 1)]));
        Tick(tracker, Map([Item(1, 1)]));
        clock.Advance(60);
        Tick(tracker, Town([Item(1, 3)]));
        Check.Equal(1, tracker.Store!.Data.Runs.Length);
        Tick(tracker, Map([Item(1, 3)]));
        Check.That(tracker.CurrentRun is not null, "run open");
        tracker.Reset();
        Check.Equal(0, tracker.Store.Data.Runs.Length);
        Check.Equal(0, tracker.Store.Data.Totals.Length);
        Check.That(tracker.CurrentRun is null, "run closed");
        Check.That(tracker.Last is null, "no last");
    }

    static int SessionRuns(WealthTracker tracker) => tracker.Session.View(null, tracker.Last, [])?.Runs ?? -1;

    static WealthTracker OneClosedRun(Clock clock)
    {
        var tracker = new WealthTracker(() => clock.At);
        Tick(tracker, Town([Item(1, 1)]));
        clock.Advance(1);
        Tick(tracker, Map([Item(1, 1)]));
        clock.Advance(60);
        Tick(tracker, Town([Item(1, 3)]));
        return tracker;
    }

    [Test]
    public void ClosedRunReachesTheSession()
    {
        var tracker = OneClosedRun(new Clock());
        Check.Equal(1, SessionRuns(tracker));
    }

    [Test]
    public void ResetEmptiesTheSession()
    {
        var tracker = OneClosedRun(new Clock());
        tracker.Reset();
        Tick(tracker, Town([Item(1, 3)]));
        Check.Equal(0, SessionRuns(tracker));
    }

    [Test]
    public void ResetWithOpenRunIsNotCountedWhenItCloses()
    {
        var clock = new Clock();
        var tracker = new WealthTracker(() => clock.At);
        Tick(tracker, Town([Item(1, 1)]));
        clock.Advance(1);
        Tick(tracker, Map([Item(1, 1)]));
        clock.Advance(30);
        tracker.Reset();
        clock.Advance(30);
        Tick(tracker, Town([Item(1, 3)]));
        Check.Equal(0, SessionRuns(tracker));
    }

    [Test]
    public void KeyChangeWithOpenRunIsNotCountedWhenItCloses()
    {
        var clock = new Clock();
        var tracker = new WealthTracker(() => clock.At);
        Tick(tracker, Town([Item(1, 1)]));
        clock.Advance(1);
        Tick(tracker, Map([Item(1, 1)]));
        clock.Advance(30);
        Tick(tracker, Town([Item(1, 3)]), character: "Other");
        Check.Equal(0, SessionRuns(tracker));
    }

    [Test]
    public void KeyChangeRestartsTheSession()
    {
        var clock = new Clock();
        var tracker = OneClosedRun(clock);
        clock.Advance(1);
        Tick(tracker, Town([Item(1, 3)]), character: "Other");
        Check.Equal(0, SessionRuns(tracker));
    }

    [Test]
    public void ChangedPricesValueFresh()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Map([Item(1, 1)]));
        Check.Equal(10f, tracker.Last!.Total);
        clock.Advance(6 * 60);
        Tick(tracker, Map([Item(1, 1)]), prices: new Dictionary<int, Price> { [1] = new Price(50, null, null) });
        Check.Equal(50f, tracker.Last!.Total);
    }

    [Test]
    public void LeavingTownTakesSnapshot()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Town([Item(1, 5)]));
        clock.Advance(30);
        Check.That(Tick(tracker, Map([Item(1, 4)])), "leaving town snapshots");
        Check.That(!tracker.Last!.InTown, "last is the map");
        clock.Advance(60);
        Tick(tracker, Town([Item(1, 4)]));
        var run = tracker.Store!.Data.Runs.Single();
        Check.Equal(-1, run.Costs.Single().Count);
    }

    [Test]
    public void NamesComeFromTheLoc()
    {
        ItemNames.Forget();
        TestGame.UseLoc(key => key switch { "item_base|name|100300" => "FE", "item_base|name|1" => "Orb", _ => null }, null);
        try
        {
            var (tracker, _) = Make();
            Tick(tracker, Map([Item(1, 1), Item(2, 1, 1)]));
            Check.Equal("Orb", tracker.Store!.Data.Names[1]);
            Check.That(!tracker.Store.Data.Names.ContainsKey(2), "unknown name left out");
        }
        finally
        {
            TestGame.UseLoc(null, null);
            ItemNames.Forget();
        }
    }

    [Test]
    public void EmptyPriceTableRecordsNothing()
    {
        var (tracker, _) = Make();
        Check.That(!Tick(tracker, Map([Item(1, 1)]), prices: new Dictionary<int, Price>()), "false");
        Check.That(tracker.Store is null || tracker.Store.Data.Totals.Length == 0, "no totals point");
    }

    [Test]
    public void EmptyInventoryRecordsNothing()
    {
        var (tracker, _) = Make();
        Check.That(!Tick(tracker, Map([])), "false");
        Check.That(tracker.Store is null || tracker.Store.Data.Totals.Length == 0, "no totals point");
    }

    [Test]
    public void NullInventoryMidRunKeepsTheBaseline()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Town([Item(1, 5)]));
        clock.Advance(1);
        Tick(tracker, Map([Item(1, 5)]));
        var baseline = tracker.Last;
        clock.Advance(60);
        Check.That(!Tick(tracker, Map(null)), "null read writes nothing");
        Check.That(ReferenceEquals(baseline, tracker.Last), "last unchanged");
        clock.Advance(60);
        Tick(tracker, Town([Item(1, 7)]));
        var run = tracker.Store!.Data.Runs.Single();
        Check.Equal(2, run.Loot.Single().Count);
    }

    [Test]
    public void UnchangedStashTabIsNotStoredAgain()
    {
        var (tracker, clock) = Make();
        Tick(tracker, WithStash(Map([Item(1, 1)]), 1, [Item(2, 3)]));
        clock.Advance(10);
        Check.That(!Tick(tracker, WithStash(Map([Item(1, 1)]), 1, [Item(2, 3)])), "same tab, nothing added");
        clock.Advance(10);
        Check.That(Tick(tracker, WithStash(Map([Item(1, 1)]), 1, [Item(2, 4)])), "changed tab");
    }

    [Test]
    public void DoubleSwitchKeepsBothRetired()
    {
        var (tracker, _) = Make();
        Tick(tracker, Map([Item(1, 1)]));
        Tick(tracker, Map([Item(1, 1)]), character: "B");
        Tick(tracker, Map([Item(1, 1)]), character: "C");
        Check.Equal(2, tracker.Retired.Length);
        tracker.Release(tracker.Retired[0]);
        Check.Equal("B-12", tracker.Retired.Single().Key);
    }

    [Test]
    public void StashingInTownIsNotAMapCost()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Town([Item(1, 5), Item(2, 1, 1)]));
        clock.Advance(2);
        Tick(tracker, Town([Item(2, 1, 1)]));
        clock.Advance(2);
        Tick(tracker, Map([Item(2, 1, 1)]));
        clock.Advance(60);
        Tick(tracker, Town([Item(2, 1, 1)]));
        Check.Equal(0, tracker.Store!.Data.Runs.Single().Costs.Length);
    }

    [Test]
    public void ConsumedCompassBeforeTheMapStillCosts()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Town([Item(1, 5), Item(2, 1, 1)]));
        clock.Advance(2);
        Tick(tracker, Town([Item(1, 5), Item(2, 1, 1)]));
        clock.Advance(2);
        Tick(tracker, Map([Item(2, 1, 1)]));
        clock.Advance(60);
        Tick(tracker, Town([Item(2, 1, 1)]));
        var cost = tracker.Store!.Data.Runs.Single().Costs.Single();
        Check.Equal(1, cost.BaseId);
        Check.Equal(-5, cost.Count);
    }

    [Test]
    public void SwitchingBackReusesTheRetiredStore()
    {
        var opened = 0;
        var (tracker, clock) = Make();
        tracker.Open = _ =>
        {
            opened++;
            return new WealthStore();
        };
        Tick(tracker, Map([Item(1, 1)]), character: "Hero");
        var hero = tracker.Store;
        clock.Advance(1);
        Tick(tracker, Map([Item(1, 1)]), character: "Other");
        clock.Advance(1);
        Tick(tracker, Map([Item(1, 1)]), character: "Hero");
        Check.That(ReferenceEquals(hero, tracker.Store), "same store");
        Check.Equal(2, opened);
        Check.Equal(1, tracker.Retired.Length);
    }

    [Test]
    public void OpenRunFollowsTheMap()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Town([Item(1, 1)]));
        Check.That(tracker.Store!.Data.Open is null, "town");
        clock.Advance(60);
        Tick(tracker, Map([Item(1, 1)]));
        Check.Equal("Map A", tracker.Store.Data.Open!.AreaName);
        clock.Advance(60);
        Tick(tracker, Town([Item(1, 3)]));
        Check.That(tracker.Store.Data.Open is null, "back in town");
        clock.Advance(60);
        Tick(tracker, Map([Item(1, 3)]));
        Check.That(tracker.Store.Data.Open is not null, "reopened");
        tracker.Reset();
        Check.That(tracker.Store.Data.Open is null && tracker.Store.Data.Live is null, "reset");
    }

    [Test]
    public void LiveUpdatesBetweenSnapshotsWithoutNewTotals()
    {
        var (tracker, clock) = Make();
        Tick(tracker, Town([Item(1, 1)]));
        clock.Advance(60);
        Tick(tracker, Map([Item(1, 1)]));
        var totals = tracker.Store!.Data.Totals.Length;
        Check.That(tracker.Store.Data.Live is null, "no live yet");
        clock.Advance(5);
        Check.That(!Tick(tracker, Map([Item(1, 1)])), "unchanged bag");
        clock.Advance(5);
        Check.That(Tick(tracker, Map([Item(1, 4)])), "changed bag");
        Check.Equal(4, tracker.Store.Data.Live!.Lines.Single().Count);
        Check.Equal(totals, tracker.Store.Data.Totals.Length);
        clock.Advance(5);
        Check.That(!Tick(tracker, Map([Item(1, 4)])), "same again");
        clock.Advance(60);
        Tick(tracker, Town([Item(1, 4)]));
        Check.That(tracker.Store.Data.Live is null, "town clears live");
    }

    [Test]
    public void IconsComeFromItems()
    {
        TestGame.UseItems(id => id == 1 ? new ItemInfo(1, 0, 0, 0, 0, 0, 0, 0, false, "UI_Item_One") : null, null);
        try
        {
            var (tracker, _) = Make();
            Tick(tracker, Map([Item(1, 1), Item(2, 1, 1)]));
            Check.Equal("UI_Item_One", tracker.Store!.Data.Icons![1]);
            Check.That(!tracker.Store.Data.Icons.ContainsKey(2), "no icon left out");
        }
        finally
        {
            TestGame.UseItems(null, null);
        }
    }
}
