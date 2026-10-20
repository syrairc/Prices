using System.Linq;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class LocalListingsTests
{
    const long Now = 1_800_000_000;

    static TradeGood Gear(long id, int baseId, long price, TradeAffix[]? affixes = null, string status = "Available", int stackMax = 1, int count = 1) =>
        new(id, 100300, price, status, new InventoryItem(InventoryBag.Commodities, 0, "id", baseId, 0, 0, 0, count, stackMax, 0)) { Affixes = affixes ?? [], ItemLevel = 80 };

    static TradeAffix Affix(TradeAffixKind kind, int id, int tier, string text) => new(kind, id, tier, [1f], text);

    static LocalBook Book()
    {
        var book = new LocalBook();
        book.Add(
        [
            Gear(1, 10, 50, [Affix(TradeAffixKind.Prefix, 1, 2, "<p>+22(20-24)</p>% Melee Damage"), Affix(TradeAffixKind.Suffix, 2, 2, "<p>+5(4-6)</p> Strength"), Affix(TradeAffixKind.BaseAttribute, 3, 1, "<p>+9(8-10)</p> Armor")]),
            Gear(2, 11, 70, [Affix(TradeAffixKind.Prefix, 1, 2, "<p>+23(20-24)</p>% Melee Damage")]),
            Gear(3, 12, 5),
            Gear(4, 13, 9, [Affix(TradeAffixKind.Prefix, 1, 2, "<p>+23(20-24)</p>% Melee Damage")], status: "Sold"),
            Gear(5, 14, 100, stackMax: 100, count: 50),
        ], Now);
        return book;
    }

    [Test]
    public void VersionBumpsOnRowsStatusPriceAndLoad()
    {
        var book = new LocalBook();
        var start = book.Version;
        book.Add([Gear(1, 10, 50)], Now);
        Check.That(book.Version > start, "new row");
        var after = book.Version;
        book.Add([Gear(1, 10, 50)], Now + 1);
        Check.Equal(after, book.Version);
        book.Add([Gear(1, 10, 60)], Now + 2);
        Check.That(book.Version > after, "price");
        after = book.Version;
        book.Add([Gear(1, 10, 60, status: "Sold")], Now + 3);
        Check.That(book.Version > after, "sold");
        after = book.Version;
        book.Purge(Now + 100);
        Check.That(book.Version > after, "purge");
    }

    [Test]
    public void RowModelNameTiersFlagsAndSearch()
    {
        var book = Book();
        var row = ListingRow.From(book.Rows.Single(r => r.GoodId == 1), book.AffixTexts);
        Check.Equal("#10", row.Name);
        Check.Equal("T2 x2 | base: T1 x1", row.TierText);
        Check.Equal("-", ListingRow.TierMix(""));
        Check.Equal("T2 x1", ListingRow.TierMix("A:2x1;B:"));
        Check.Equal("base: T1 x2, T2 x1", ListingRow.TierMix("A:;B:1x2,2x1"));
        Check.Equal("Purple", ListingRow.RareName(3));
        Check.Equal("Orange", ListingRow.RareName(100));
        Check.Equal("7", ListingRow.RareName(7));
        Check.That(row.Search.Contains("melee damage") && row.Search.Contains("strength") && !row.Search.Contains("#("), "search has affix text");
        var sold = ListingRow.From(book.Rows.Single(r => r.GoodId == 4), book.AffixTexts);
        Check.Equal("Sold", sold.FlagsText);
        Check.That(sold.Search.Contains("sold"), "flags searchable");
        var flagged = new LocalRow { GoodId = 9, Unidentified = true, Corrode = true, Forged = true, EnhanceCount = 3, Flags = ["FORGE_PRECISE"] };
        Check.Equal("Unidentified, Corroded, Forged, Enhance 3, FORGE_PRECISE", ListingRow.From(flagged, book.AffixTexts).FlagsText);
    }

    [Test]
    public void NativeRarityFillsOnlyMissingCommodityRarity()
    {
        TestGame.UseItems(id => id == 5080 ? new ItemInfo(5080, 3, 0, 0, 0, 0, 0, 0, false) : null, null);
        var book = new LocalBook();
        var texts = book.AffixTexts;
        Check.Equal(3, ListingRow.From(new LocalRow { GoodId = 1, BaseId = 5080, Commodity = true }, texts).Rare);
        Check.Equal("Purple", ListingRow.From(new LocalRow { GoodId = 1, BaseId = 5080, Commodity = true }, texts).RareLabel);
        Check.Equal(2, ListingRow.From(new LocalRow { GoodId = 2, BaseId = 5080, Commodity = true, Rare = 2 }, texts).Rare);
        Check.Equal(0, ListingRow.From(new LocalRow { GoodId = 3, BaseId = 5080 }, texts).Rare);
        TestGame.UseItems(null, null);
    }

    [Test]
    public void CommodityAndSoldFilters()
    {
        var listings = new LocalListings();
        var book = Book();
        listings.Rebuild(book);
        Check.Equal(3, listings.Shown.Count);
        listings.HideSold = false;
        listings.Rebuild(book);
        Check.Equal(4, listings.Shown.Count);
        listings.IncludeCommodities = true;
        listings.Rebuild(book);
        Check.Equal(5, listings.Shown.Count);
        listings.Rebuild(null);
        Check.Equal(0, listings.Shown.Count);
    }

    [Test]
    public void TierOptionsAreCountedOrderedAndNarrow()
    {
        var listings = new LocalListings();
        listings.HideSold = false;
        listings.Rebuild(Book());
        Check.Equal("Any", listings.TierLabels[0]);
        Check.Equal("T2 x1 (2)", listings.TierLabels[1]);
        Check.That(listings.TierLabels.Contains("(no tiers) (1)"), "no tiers");
        Check.That(listings.TierLabels.Contains("T2 x2 | base: T1 x1 (1)"), "mix");
        Check.Equal(4, listings.TierLabels.Count);
        listings.SelectTier("");
        Check.Equal(1, listings.Shown.Count);
        listings.SelectTier("A:2x1;B:");
        Check.Equal(2, listings.Shown.Count);
        listings.SelectTier(null);
        Check.Equal(4, listings.Shown.Count);
    }

    [Test]
    public void SelectionByGoodIdAndDetails()
    {
        var listings = new LocalListings();
        var book = Book();
        listings.Rebuild(book);
        Check.That(listings.Selected is null, "nothing selected");
        listings.SelectedGoodId = 1;
        var selected = listings.Selected!;
        var lines = listings.Details(selected, book.AffixTexts).ToArray();
        Check.Equal("T2  +#(20-24)% Melee Damage", lines[2]);
        Check.Equal("T2  +#(4-6) Strength", lines[3]);
        Check.Equal("T1  +#(8-10) Armor", lines[4]);
        listings.IncludeCommodities = true;
        listings.Rebuild(book);
        Check.Equal(1L, listings.Selected!.GoodId);
    }

    [Test]
    public void ListingsViewDraws()
    {
        var listings = new LocalListings();
        var book = Book();
        listings.SelectedGoodId = 1;
        Check.That(TestFrame.Vertices(() => listings.Draw(book), inWindow: true) > 0, "listings draw");
        Check.That(TestFrame.Vertices(() => listings.Draw(null), inWindow: true) > 0, "empty draws");
    }
}
