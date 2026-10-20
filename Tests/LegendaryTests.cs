using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class LegendaryTests
{
    const long Now = 1_800_000_000;
    const int Sp = 110103;
    const int BaseId = 4518;

    static TradeGood Gear(long id, long price, int special = Sp, bool unidentified = false, string status = "Available", params int[] affixIds) =>
        new(id, 100300, price, status, new InventoryItem(InventoryBag.Commodities, 0, "id", BaseId, 0, 0, 100, 1, 1, 0))
        {
            SpecialId = special,
            Unidentified = unidentified,
            Affixes = [.. affixIds.Select((a, i) => new TradeAffix(i % 2 == 0 ? TradeAffixKind.Prefix : TradeAffixKind.Suffix, a, 0, [1f], "x"))],
        };

    static LegendaryPrices Book(params TradeGood[] goods)
    {
        var book = new LocalBook();
        book.Add(goods, Now);
        return book.Legendary(Now, LocalBook.Recent);
    }

    static InventoryItem Bag(int special, bool unidentified) =>
        new(InventoryBag.Equipment, 0, "id", BaseId, 0, 0, 100, 1, 1, 0) { SpecialId = special, Unidentified = unidentified };

    static Main Plugin(params TradeGood[] goods)
    {
        var plugin = new Main { DataDirectory = Path.Combine(Path.GetTempPath(), "prices-leg-" + Guid.NewGuid().ToString("N")), UnixClock = () => Now };
        plugin.Settings.UseTitrack = false;
        plugin.Settings.UseEtor = false;
        plugin.Settings.UseLocal = true;
        plugin.Settings.Enabled = false;
        plugin.Settings.GroundEnabled = false;
        plugin.Settings.Season = 1601;
        TestGame.Set(GameState.Empty with { TradeGoods = goods });
        plugin.Tick();
        return plugin;
    }

    [Test]
    public void AMajorityPriceBeatsTheMinorityInBothDirections()
    {
        var cheap = new List<TradeGood>();
        for (var i = 0; i < 900; i++) cheap.Add(Gear(i + 1, 1));
        for (var i = 0; i < 30; i++) cheap.Add(Gear(1000 + i, 200));
        Check.Equal(1f, Book([.. cheap]).Of(Sp, false, [])!.Value.Median);
        var dear = new List<TradeGood>();
        for (var i = 0; i < 900; i++) dear.Add(Gear(i + 1, 200));
        for (var i = 0; i < 30; i++) dear.Add(Gear(1000 + i, 1));
        var book = new LocalBook();
        book.Add(dear, Now);
        Check.Equal(200f, book.Legendary(Now, LocalBook.Recent).Of(Sp, false, [])!.Value.Median);
        Check.Equal(200f, book.Legendary(Now, LocalBook.Recent).Of(Sp, false, [])!.Value.Low!.Value);
        Check.Equal(1f, book.Legendary(Now, LocalBook.Recent, excludeOutliers: false).Of(Sp, false, [])!.Value.Low!.Value);
    }

    [Test]
    public void TheOutlierSettingRebuildsTheLocalPrices()
    {
        var goods = new List<TradeGood>();
        for (var i = 0; i < 90; i++) goods.Add(Gear(i + 1, 200));
        for (var i = 0; i < 10; i++) goods.Add(Gear(1000 + i, 1));
        var plugin = Plugin([.. goods]);
        Check.Equal(200f, plugin.Legendary!.Of(Sp, false, [])!.Value.Median);
        plugin.Settings.ExcludeOutliers = false;
        plugin.UnixClock = () => Now + 10;
        plugin.Tick();
        Check.Equal(1f, plugin.Legendary!.Of(Sp, false, [])!.Value.Median);
        plugin.Dispose();
    }

    [Test]
    public void PickFencesOutliersAndSkipsSoldOldAndCommodityRows()
    {
        var goods = new List<TradeGood>();
        for (var i = 0; i < 20; i++) goods.Add(Gear(i + 1, 100 + i));
        goods.Add(Gear(100, 50_000));
        goods.Add(Gear(101, 1, status: "Sold"));
        var book = new LocalBook();
        book.Add([Gear(200, 1)], Now - 90_000);
        book.Add(goods, Now);
        book.Add([new TradeGood(300, 100300, 2, "Available", new InventoryItem(InventoryBag.Commodities, 0, "id", BaseId, 0, 0, 0, 100, 100, 0)) { SpecialId = Sp }], Now);
        var price = book.Legendary(Now, LocalBook.Recent).Of(Sp, false, [])!.Value;
        Check.Equal(20, price.Submissions);
        Check.That(price.Median >= 100 && price.Median <= 120, price.Median.ToString());
        Check.That(price.High < 200, "outlier fenced " + price.High);
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(Now), price.Updated);
        Check.Equal(1, price.Devices);
    }

    [Test]
    public void UnidentifiedNeverFallsBackToIdentified()
    {
        var onlyIdentified = Book(Gear(1, 100));
        Check.That(onlyIdentified.Of(Sp, true, []) is null, "no unidentified price");
        Check.Equal(100f, onlyIdentified.Of(Sp, false, [])!.Value.Median);
        var both = Book(Gear(1, 100), Gear(2, 300, unidentified: true));
        Check.Equal(300f, both.Of(Sp, true, [])!.Value.Median);
        Check.Equal(100f, both.Of(Sp, false, [])!.Value.Median);
        Check.That(both.Of(999, false, []) is null, "unknown special id");
        Check.Equal(2, both.Count);
        Check.Equal(1, both.Entries.Count(entry => entry.Unidentified));
    }

    [Test]
    public void IdentifiedPrefersAVariantWithThreeRows()
    {
        var goods = new List<TradeGood> { Gear(1, 100), Gear(2, 100), Gear(3, 100) };
        for (var i = 0; i < 3; i++) goods.Add(Gear(10 + i, 900, affixIds: [51210311, 51210321]));
        goods.Add(Gear(20, 5000, affixIds: [7]));
        var prices = Book([.. goods]);
        Check.Equal(900f, prices.Of(Sp, false, [51210321, 51210311])!.Value.Median);
        Check.That(prices.Of(Sp, false, [7])!.Value.Median < 900, "one row is not a variant, falls back to the special id");
        Check.That(prices.Of(Sp, false, [99])!.Value.Median < 900, "unseen modifiers fall back");
    }

    [Test]
    public void KeyIgnoresOrderBaseAttributesAndImplicitsButNotModifiers()
    {
        Check.Equal(LegendaryPrices.Key(Sp, [1, 2]), LegendaryPrices.Key(Sp, [2, 1, 2]));
        Check.That(LegendaryPrices.Key(Sp, [1, 2]) != LegendaryPrices.Key(Sp, [1, 3]), "other modifier");
        Check.That(LegendaryPrices.Key(Sp, [1, 2]) != LegendaryPrices.Key(Sp + 1, [1, 2]), "other special id");
        var row = new LocalRow
        {
            SpecialId = Sp,
            Affixes = [new(TradeAffixKind.Prefix, 2, 0), new(TradeAffixKind.BaseAttribute, 50, 0), new(TradeAffixKind.Suffix, 1, 0), new(TradeAffixKind.Implicit, 60, 0)],
        };
        Check.Equal(LegendaryPrices.Key(Sp, [1, 2]), row.VariantKey);
    }

    [Test]
    public void TagTextAndTotals()
    {
        var plugin = Plugin(Gear(1, 100), Gear(2, 400, unidentified: true));
        plugin.Prices = new Dictionary<int, Price> { [BaseId] = new(3, null, null) };
        Check.Equal("100", plugin.TagFor(BaseId, 1, true, Sp, false, [], []));
        Check.Equal("?400", plugin.TagFor(BaseId, 1, true, Sp, true, [], []));
        Check.Equal("3.0", plugin.TagFor(BaseId, 1, true, 999, false, [], []));
        Check.Equal("3.0", plugin.TagFor(BaseId, 1, true), "a plain item of the same base id keeps the base price");
        InventoryItem[] items = [Bag(Sp, false), Bag(Sp, true), Bag(999, false), Bag(0, false)];
        Check.Equal(100.0 + 400 + 3 + 3, Tags.Total(items, plugin.Prices, plugin.Legendary));
        Check.Equal(12.0, Tags.Total(items, plugin.Prices));
    }

    [Test]
    public void GearShowsARangeLikeOtherGear()
    {
        var plugin = Plugin(Gear(1, 100), Gear(2, 200), Gear(3, 300), Gear(4, 400));
        var text = plugin.TagFor(BaseId, 1, true, Sp, false, [], []);
        Check.That(text is not null && text.Contains('-'), text ?? "null");
    }

    [Test]
    public void CacheDoesNotLeakBetweenLegendaryAndPlainItems()
    {
        var plugin = Plugin(Gear(1, 100));
        plugin.Prices = new Dictionary<int, Price> { [BaseId] = new(3, null, null) };
        Check.Equal("3.0", plugin.TagFor(BaseId, 1, false));
        Check.Equal("100", plugin.TagFor(BaseId, 1, false, Sp, false, [], []));
        Check.Equal("3.0", plugin.TagFor(BaseId, 1, false));
        Check.Equal("100", plugin.TagFor(BaseId, 1, false, Sp, false, [], []));
    }

    [Test]
    public void MinSamplesAndIgnoreOlderApply()
    {
        var plugin = Plugin(Gear(1, 100), Gear(2, 120));
        plugin.Settings.MinSamples = 3;
        plugin.Tick();
        Check.Equal(0, plugin.Legendary!.Count);
        plugin.Settings.MinSamples = 0;
        plugin.Tick();
        Check.Equal(1, plugin.Legendary!.Count);
        plugin.Settings.IgnoreOlderThanDays = 1;
        plugin.UnixClock = () => Now + 3 * 86400;
        plugin.Tick();
        Check.Equal(0, plugin.Legendary!.Count);
    }

    [Test]
    public void LocalOffHasNoLegendaryPrices()
    {
        var plugin = Plugin(Gear(1, 100));
        Check.That(plugin.Legendary is not null, "on");
        plugin.Settings.UseLocal = false;
        plugin.Tick();
        Check.That(plugin.Legendary is null, "off");
        Check.That(plugin.Settings.Legendaries is null, "settings follow");
    }
}
