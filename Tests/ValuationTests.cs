using System;
using System.Collections.Generic;
using System.Linq;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class ValuationTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    static InventoryItem Item(int baseId, int count, InventoryBag bag = InventoryBag.Commodities, int slot = 0) =>
        new(bag, slot, "", baseId, 1, 0, 0, count, 100, 0);

    static Snapshot Value(InventoryItem[] items, Dictionary<int, Price> prices) =>
        Valuation.Value(items, prices, Now, 1, "Town", true);

    static Line[] Lines(params (int BaseId, int Count)[] counts) =>
        counts.Select(c => new Line(c.BaseId, c.Count, 0, 0)).ToArray();

    [Test]
    public void StackIsMedianTimesCount()
    {
        var snapshot = Value([Item(1, 5)], new() { [1] = new Price(8, null, null) });
        Check.Equal(40f, snapshot.Lines.Single().Value);
        Check.Equal(40f, snapshot.Total);
    }

    [Test]
    public void GearUsesLowEndElseMedian()
    {
        Check.Equal(3f, Value([Item(1, 1, InventoryBag.Equipment)], new() { [1] = new Price(8, 3, 12) }).Total);
        Check.Equal(8f, Value([Item(1, 1, InventoryBag.Equipment)], new() { [1] = new Price(8, null, null) }).Total);
    }

    [Test]
    public void FeIsOneToOne()
    {
        var snapshot = Value([Item(Valuation.FeBaseId, 250)], []);
        Check.Equal(250f, snapshot.Fe);
        Check.Equal(250f, snapshot.Total);
        Check.Equal(0, snapshot.Unpriced);
    }

    [Test]
    public void NoPricesIsAllUnpriced()
    {
        var snapshot = Value([Item(1, 2), Item(2, 1), Item(3, 9)], []);
        Check.Equal(3, snapshot.Unpriced);
        Check.Equal(0f, snapshot.Total);
    }

    [Test]
    public void SameItemInTwoSlotsIsOneLine()
    {
        var snapshot = Value([Item(1, 3, slot: 0), Item(1, 4, slot: 5)], new() { [1] = new Price(2, null, null) });
        var line = snapshot.Lines.Single();
        Check.Equal(7, line.Count);
        Check.Equal(14f, line.Value);
    }

    [Test]
    public void DiffIsSignedAndSkipsUnchanged()
    {
        var diff = Valuation.Diff([new Line(1, 5, 10, 50), new Line(2, 2, 0, 0)], [new Line(1, 3, 10, 30), new Line(2, 2, 0, 0), new Line(3, 1, 4, 4)]);
        Check.Equal(2, diff.Length);
        var a = diff.Single(line => line.BaseId == 1);
        Check.Equal(-2, a.Count);
        Check.Equal(-20f, a.Value);
        var c = diff.Single(line => line.BaseId == 3);
        Check.Equal(1, c.Count);
        Check.Equal(4f, c.Value);
    }

    [Test]
    public void DiffUsesTheLineUnitSoGearStaysAtLow()
    {
        var gear = Value([Item(1, 1, InventoryBag.Equipment)], new() { [1] = new Price(8, 3, 12) });
        var diff = Valuation.Diff([], gear.Lines).Single();
        Check.Equal(3f, diff.Value);
        Check.Equal(100f, Valuation.Diff([], [new Line(Valuation.FeBaseId, 100, 0, 100)]).Single().Value);
    }
}
