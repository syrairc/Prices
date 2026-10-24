using System;
using System.Collections.Generic;
using System.Linq;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class RunTrackerTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    static readonly Dictionary<int, Price> Prices = new() { [1] = new Price(10, null, null), [2] = new Price(4, null, null), [3] = new Price(2, null, null) };

    static Line[] Lines(params (int BaseId, int Count)[] counts) =>
        counts.Select(c => new Line(c.BaseId, c.Count, 0, 0)).ToArray();

    static Snapshot Town(double seconds = 0, float total = 0, params (int BaseId, int Count)[] lines) =>
        new(T0.AddSeconds(seconds), 1, "Town", true, total, 0, 0, Lines(lines));

    static Snapshot Map(double seconds = 0, float total = 0, int areaId = 10, string name = "Map A", int unpriced = 0, params (int BaseId, int Count)[] lines) =>
        new(T0.AddSeconds(seconds), areaId, name, false, total, 0, unpriced, Lines(lines));

    [Test]
    public void TownToMapOpensRun()
    {
        var tracker = new RunTracker();
        var town = Town();
        var map = Map(5);
        Check.That(tracker.OnSnapshot(town, Prices) is null);
        Check.That(tracker.OnSnapshot(map, Prices) is null);
        Check.That(ReferenceEquals(map, tracker.Open));
        Check.That(ReferenceEquals(town, tracker.Before));
    }

    [Test]
    public void MapToTownClosesRun()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Town(), Prices);
        tracker.OnSnapshot(Map(0, 100), Prices);
        var run = tracker.OnSnapshot(Town(60, 130), Prices)!;
        Check.Equal(30f, run.Delta);
        Check.Equal("Map A", run.AreaName);
        Check.That(tracker.Open is null);
    }

    [Test]
    public void LootIsTheDiffOfTheLines()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Town(), Prices);
        tracker.OnSnapshot(Map(0, 0, lines: [(1, 5)]), Prices);
        var run = tracker.OnSnapshot(Town(60, 0, (1, 3), (3, 1)), Prices)!;
        Check.Equal(2, run.Loot.Length);
        Check.Equal(-2, run.Loot.Single(line => line.BaseId == 1).Count);
        Check.Equal(1, run.Loot.Single(line => line.BaseId == 3).Count);
    }

    [Test]
    public void CostsAreTheDropBetweenBeforeAndOpen()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Town(0, 0, (2, 3)), Prices);
        tracker.OnSnapshot(Map(5, 0, lines: [(2, 2)]), Prices);
        var run = tracker.OnSnapshot(Town(60), Prices)!;
        var cost = run.Costs.Single();
        Check.Equal(2, cost.BaseId);
        Check.Equal(-1, cost.Count);
    }

    [Test]
    public void MapToMapClosesAndReopens()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Town(), Prices);
        tracker.OnSnapshot(Map(0), Prices);
        var second = Map(60, 0, 11, "Map B");
        var run = tracker.OnSnapshot(second, Prices)!;
        Check.Equal("Map A", run.AreaName);
        Check.That(ReferenceEquals(second, tracker.Open));
    }

    [Test]
    public void ShortRunIsDropped()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Town(), Prices);
        tracker.OnSnapshot(Map(0), Prices);
        Check.That(tracker.OnSnapshot(Town(8), Prices) is null);
        Check.That(tracker.Open is null);
    }

    [Test]
    public void FirstSnapshotInMapIsPartial()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Map(0, 0, lines: [(2, 2)]), Prices);
        var run = tracker.OnSnapshot(Town(60), Prices)!;
        Check.That(run.Partial);
        Check.Equal(0, run.Costs.Length);
    }

    [Test]
    public void TimerSnapshotInSameAreaDoesNotClose()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Town(), Prices);
        var first = Map(0);
        tracker.OnSnapshot(first, Prices);
        Check.That(tracker.OnSnapshot(Map(30), Prices) is null);
        Check.That(ReferenceEquals(first, tracker.Open));
    }

    [Test]
    public void UnpricedRunIsFlagged()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Town(), Prices);
        tracker.OnSnapshot(Map(0), Prices);
        Check.That(tracker.OnSnapshot(new Snapshot(T0.AddSeconds(60), 1, "Town", true, 0, 0, 2, []), Prices)!.Unpriced);
    }

    [Test]
    public void ResetDropsTheOpenRun()
    {
        var tracker = new RunTracker();
        tracker.OnSnapshot(Town(), Prices);
        tracker.OnSnapshot(Map(0), Prices);
        tracker.Reset();
        Check.That(tracker.Open is null);
        tracker.OnSnapshot(Map(20), Prices);
        Check.That(tracker.OnSnapshot(Town(80), Prices)!.Partial);
    }
}
