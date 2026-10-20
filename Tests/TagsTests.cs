using System.Collections.Generic;
using System.Numerics;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class TagsTests
{
    static InventoryItem Item(InventoryBag bag, int baseId, int count) => new(bag, 0, "id", baseId, 0, 0, 0, count, 0, 0);

    [Test]
    public void ValueIsTheStackMedianOrZero()
    {
        var prices = new Dictionary<int, Price> { [1] = new(2.5f, null, null) };
        Check.Equal(10.0, Tags.Value(Item(InventoryBag.Commodities, 1, 4), prices));
        Check.Equal(0.0, Tags.Value(Item(InventoryBag.Commodities, 99, 4), prices));
    }

    [Test]
    public void ValueCanTakeEitherEnd()
    {
        var prices = new Dictionary<int, Price> { [1] = new(5, 2, 9), [2] = new(5, null, null) };
        Check.Equal(8.0, Tags.Value(Item(InventoryBag.Commodities, 1, 4), prices, null, Tags.LowEnd));
        Check.Equal(36.0, Tags.Value(Item(InventoryBag.Commodities, 1, 4), prices, null, Tags.HighEnd));
        Check.Equal(20.0, Tags.Value(Item(InventoryBag.Commodities, 2, 4), prices, null, Tags.LowEnd));
    }

    [Test]
    public void TotalSumsMedianTimesCountSkippingUnpriced()
    {
        var prices = new Dictionary<int, Price> { [1] = new(2.5f, null, null), [2] = new(100, 50, 300) };
        Check.Equal(0.0, Tags.Total([], prices));
        Check.Equal(2.5 * 4 + 100, Tags.Total([Item(InventoryBag.Commodities, 1, 4), Item(InventoryBag.Equipment, 2, 1), Item(InventoryBag.Other, 99, 7)], prices));
    }

    [Test]
    public void FormatMatchesTheSpec()
    {
        Check.Equal("0.4", Tags.Format(0.4));
        Check.Equal("3.2", Tags.Format(3.2));
        Check.Equal("86", Tags.Format(86));
        Check.Equal("420", Tags.Format(420));
        Check.Equal("1.4k", Tags.Format(1400));
        Check.Equal("12k", Tags.Format(12000));
        Check.Equal("1.2M", Tags.Format(1_200_000));
    }

    [Test]
    public void FormatRoundsBeforePickingTheBand()
    {
        Check.Equal("10", Tags.Format(9.96));
        Check.Equal("1k", Tags.Format(999.6));
        Check.Equal("1M", Tags.Format(999_960));
        Check.Equal("0.1", Tags.Format(0.05));
        Check.That(Tags.Format(0.04) is null && Tags.Format(0) is null, "under 0.05 has no text");
    }

    [Test]
    public void WorthlessItemHasNoTag()
    {
        var prices = new Dictionary<int, Price> { [7] = new(0.01f, null, null) };
        Check.That(Tags.Text(Item(InventoryBag.Commodities, 7, 1), prices) is null, "tiny price, no tag");
    }

    [Test]
    public void StackShowsWholeStackValue()
    {
        var prices = new Dictionary<int, Price> { [7] = new(8, null, null) };
        Check.Equal("1.4k", Tags.Text(Item(InventoryBag.Commodities, 7, 177), prices));
    }

    [Test]
    public void GearShowsRange()
    {
        var gear = Item(InventoryBag.Equipment, 7, 1);
        Check.Equal("12-80", Tags.Text(gear, new Dictionary<int, Price> { [7] = new(30, 12, 80) }));
        Check.Equal("30", Tags.Text(gear, new Dictionary<int, Price> { [7] = new(30, 12, 12) }));
        Check.Equal("30", Tags.Text(gear, new Dictionary<int, Price> { [7] = new(30, null, 80) }));
    }

    [Test]
    public void UnknownItemHasNoTag()
    {
        Check.That(Tags.Text(Item(InventoryBag.Commodities, 9, 1), new Dictionary<int, Price>()) is null, "no price, no tag");
    }

    [Test]
    public void AnchorPerCorner()
    {
        var min = Vector2.Zero;
        var max = new Vector2(94, 135);
        var tag = new Vector2(30, 12);
        Check.Equal(new Vector2(3, 3), Tags.Anchor(Corner.TopLeft, min, max, tag));
        Check.Equal(new Vector2(61, 3), Tags.Anchor(Corner.TopRight, min, max, tag));
        Check.Equal(new Vector2(3, 120), Tags.Anchor(Corner.BottomLeft, min, max, tag));
        Check.Equal(new Vector2(61, 120), Tags.Anchor(Corner.BottomRight, min, max, tag));
    }

    static InventoryItem Rare(InventoryBag bag, int rareValue) => Item(bag, 1, 1) with { RareValue = rareValue };

    [Test]
    public void RarityTogglesHideOnlyTheirFrameOnGear()
    {
        var settings = new Settings { ShowGrey = false, ShowOrange = false };
        Check.That(!settings.Shows(Rare(InventoryBag.Equipment, 1)) && !settings.Shows(Rare(InventoryBag.Equipment, 100)), "grey and orange gear hidden");
        Check.That(settings.Shows(Rare(InventoryBag.Equipment, 2)) && settings.Shows(Rare(InventoryBag.Equipment, 3)) && settings.Shows(Rare(InventoryBag.Equipment, 4)), "other gear shown");
        Check.That(settings.Shows(Rare(InventoryBag.Equipment, 0)) && settings.Shows(Rare(InventoryBag.Equipment, 7)), "unknown values always shown");
        Check.That(settings.Shows(Rare(InventoryBag.Commodities, 1)) && settings.Shows(Rare(InventoryBag.Skills, 100)) && settings.Shows(Rare(InventoryBag.Other, 1)), "rarity never hides non-gear");
    }

    [Test]
    public void BagTogglesHideTheirNonGear()
    {
        var settings = new Settings { ShowCommodities = false, ShowSkills = false, ShowOther = false };
        Check.That(!settings.Shows(Rare(InventoryBag.Commodities, 3)) && !settings.Shows(Rare(InventoryBag.Skills, 3)) && !settings.Shows(Rare(InventoryBag.Other, 3)), "bags hidden");
        Check.That(settings.Shows(Rare(InventoryBag.Equipment, 3)), "gear follows the rarity toggles only");
    }
}
