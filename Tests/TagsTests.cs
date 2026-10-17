using System.Collections.Generic;
using System.Numerics;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class TagsTests
{
    static InventoryItem Item(InventoryBag bag, int baseId, int count) => new(bag, 0, "id", baseId, 0, 0, 0, count, 0, 0);

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

    [Test]
    public void RarityTogglesHideOnlyTheirFrame()
    {
        var settings = new Settings { ShowGrey = false, ShowOrange = false };
        Check.That(!settings.Shows(1) && !settings.Shows(100), "grey and orange hidden");
        Check.That(settings.Shows(2) && settings.Shows(3) && settings.Shows(4), "others shown");
        Check.That(settings.Shows(0) && settings.Shows(7), "unknown values always shown");
    }
}
