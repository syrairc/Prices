using System.Numerics;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class CompareCacheTests
{
    static TradeListing Listing(int slot, long price = 10, string name = "Sword") => new(slot, name, 1, price, null, new Vector2(slot * 100, 0), new Vector2(slot * 100 + 90, 50));

    static TradePage Page(int page, params TradeListing[] listings) => new(page, false, 8, listings, "");

    static EquipCompare Compare(float damage, float? survival = null) => new([new CompareRow(damage, survival)]);

    static Vector2 On(int slot) => new(slot * 100 + 10, 10);

    [Test]
    public void HoveredSlotHitMissAndEdges()
    {
        var listings = new[] { Listing(0), Listing(1) };
        Check.Equal(1, CompareCache.HoveredSlot(listings, On(1)));
        Check.That(CompareCache.HoveredSlot(listings, new Vector2(95, 10)) is null, "gap");
        Check.Equal(0, CompareCache.HoveredSlot(listings, new Vector2(0, 0)));
        Check.That(CompareCache.HoveredSlot(listings, new Vector2(90, 10)) is null, "max x excluded");
        Check.That(CompareCache.HoveredSlot(listings, new Vector2(10, 50)) is null, "max y excluded");
        Check.That(CompareCache.HoveredSlot([], On(0)) is null, "empty");
    }

    [Test]
    public void CommitsOnlyAfterSettle()
    {
        var cache = new CompareCache();
        var page = Page(1, Listing(0));
        cache.Feed(page, Compare(5), On(0), 0);
        cache.Feed(page, Compare(5), On(0), CompareCache.DefaultSettleMs - 1);
        Check.That(cache.Rows(1, page.Listings[0]) is null, "not yet");
        cache.Feed(page, Compare(5), On(0), CompareCache.DefaultSettleMs);
        var rows = cache.Rows(1, page.Listings[0])!;
        Check.Equal(1, rows.Length);
        Check.Equal(5f, rows[0].Damage);
    }

    [Test]
    public void MovingToAnotherSlotRestartsTheTimer()
    {
        var cache = new CompareCache { SettleMs = 300 };
        var page = Page(1, Listing(0), Listing(1));
        cache.Feed(page, Compare(5), On(0), 0);
        cache.Feed(page, Compare(5), On(1), 200);
        cache.Feed(page, Compare(5), On(1), 400);
        Check.That(cache.Rows(1, page.Listings[0]) is null, "first slot");
        Check.That(cache.Rows(1, page.Listings[1]) is null, "second slot");
        cache.Feed(page, Compare(5), On(1), 500);
        Check.That(cache.Rows(1, page.Listings[1]) is not null, "second slot settled");
    }

    [Test]
    public void NullCompareOrChangedValuesRestartTheTimer()
    {
        var cache = new CompareCache { SettleMs = 300 };
        var page = Page(1, Listing(0));
        cache.Feed(page, Compare(5), On(0), 0);
        cache.Feed(page, null, On(0), 200);
        cache.Feed(page, Compare(5), On(0), 400);
        cache.Feed(page, Compare(5), On(0), 600);
        Check.That(cache.Rows(1, page.Listings[0]) is null, "null reset the timer");
        cache.Feed(page, Compare(6), On(0), 650);
        cache.Feed(page, Compare(6), On(0), 800);
        Check.That(cache.Rows(1, page.Listings[0]) is null, "values changed");
        cache.Feed(page, Compare(6), On(0), 950);
        Check.Equal(6f, cache.Rows(1, page.Listings[0])![0].Damage);
        var two = new EquipCompare([new CompareRow(1, 2), new CompareRow(3, 4)]);
        var other = Page(1, Listing(1));
        cache.Feed(other, Compare(1), On(1), 1000);
        cache.Feed(other, two, On(1), 1200);
        cache.Feed(other, two, On(1), 1400);
        Check.That(cache.Rows(1, other.Listings[0]) is null, "row count changed");
        cache.Feed(other, two, On(1), 1500);
        Check.Equal(2, cache.Rows(1, other.Listings[0])!.Length);
    }

    [Test]
    public void RowsNeedTheSameListingAndPage()
    {
        var cache = new CompareCache();
        var page = Page(1, Listing(0));
        cache.Feed(page, Compare(5), On(0), 0);
        cache.Feed(page, Compare(5), On(0), 300);
        Check.That(cache.Rows(1, Listing(0)) is not null, "same");
        Check.That(cache.Rows(1, Listing(0, price: 11)) is null, "price");
        Check.That(cache.Rows(1, Listing(0, name: "Axe")) is null, "name");
        Check.That(cache.Rows(1, Listing(0) with { Quantity = 2 }) is null, "quantity");
        Check.That(cache.Rows(2, Listing(0)) is null, "page");
        Check.That(cache.Rows(1, Listing(1)) is null, "slot");
    }

    [Test]
    public void NullPageAndClearEmptyTheCache()
    {
        var cache = new CompareCache();
        var page = Page(1, Listing(0));
        cache.Feed(page, Compare(5), On(0), 0);
        cache.Feed(page, Compare(5), On(0), 300);
        cache.Feed(null, null, default, 400);
        Check.That(cache.Rows(1, page.Listings[0]) is null, "page null");
        cache.Feed(page, Compare(5), On(0), 0);
        cache.Feed(page, Compare(5), On(0), 300);
        cache.Clear();
        Check.That(cache.Rows(1, page.Listings[0]) is null, "clear");
        cache.Feed(page, Compare(5), On(0), 500);
        cache.Feed(null, null, default, 600);
        cache.Feed(page, Compare(5), On(0), 800);
        Check.That(cache.Rows(1, page.Listings[0]) is null, "pending cleared by page null");
    }

    [Test]
    public void ReHoverAfterSettleReplacesTheEntry()
    {
        var cache = new CompareCache();
        var page = Page(1, Listing(0));
        cache.Feed(page, Compare(5), On(0), 0);
        cache.Feed(page, Compare(5), On(0), 300);
        cache.Feed(page, null, default, 400);
        Check.Equal(5f, cache.Rows(1, page.Listings[0])![0].Damage);
        cache.Feed(page, Compare(9), On(0), 500);
        Check.Equal(5f, cache.Rows(1, page.Listings[0])![0].Damage);
        cache.Feed(page, Compare(9), On(0), 800);
        Check.Equal(9f, cache.Rows(1, page.Listings[0])![0].Damage);
    }

    static EquipCompare Versioned(float damage, long version) => new([new CompareRow(damage, null)]) { Version = version };

    [Test]
    public void AStaleCompareIsNeverCommittedAndTheFirstNewVersionIs()
    {
        var cache = new CompareCache();
        var page = Page(1, Listing(0), Listing(1));
        cache.Feed(page, Versioned(5, 7), On(0), 0);
        cache.Feed(page, Versioned(5, 7), On(0), 1000);
        Check.That(cache.Rows(1, page.Listings[0]) is not null, "first card commits");
        cache.Feed(page, Versioned(5, 7), On(1), 2000);
        cache.Feed(page, Versioned(5, 7), On(1), 60000);
        Check.That(cache.Rows(1, page.Listings[1]) is null, "stale never commits");
        cache.Feed(page, Versioned(8, 9), On(1), 60100);
        cache.Feed(page, Versioned(8, 9), On(1), 60100 + CompareCache.DefaultSettleMs);
        Check.Equal(8f, cache.Rows(1, page.Listings[1])![0].Damage);
    }

    [Test]
    public void AStaleCompareNeverLandsOnTheNewCardEvenWithZeroSettle()
    {
        var cache = new CompareCache { SettleMs = 0 };
        var page = Page(1, Listing(0), Listing(1));
        cache.Feed(page, Versioned(5, 7), On(0), 0);
        cache.Feed(page, Versioned(5, 7), On(1), 10);
        cache.Feed(page, Versioned(5, 7), On(1), 20);
        Check.That(cache.Rows(1, page.Listings[1]) is null, "A's numbers not on B");
    }

    [Test]
    public void NullThenTheOldVersionIsStillStale()
    {
        var cache = new CompareCache { SettleMs = 0 };
        var page = Page(1, Listing(0), Listing(1));
        cache.Feed(page, Versioned(5, 7), On(0), 0);
        cache.Feed(page, null, On(1), 10);
        cache.Feed(page, Versioned(5, 7), On(1), 20);
        Check.That(cache.Rows(1, page.Listings[1]) is null, "old version after null");
    }

    [Test]
    public void ADifferentVersionOnEnteringIsFresh()
    {
        var cache = new CompareCache { SettleMs = 0 };
        var page = Page(1, Listing(0), Listing(1));
        cache.Feed(page, Versioned(5, 7), On(0), 0);
        cache.Feed(page, Versioned(6, 8), On(1), 10);
        Check.Equal(6f, cache.Rows(1, page.Listings[1])![0].Damage);
    }

    [Test]
    public void ReHoveringTheSameCardNeedsANewVersion()
    {
        var cache = new CompareCache { SettleMs = 0 };
        var page = Page(1, Listing(0));
        cache.Feed(page, Versioned(5, 7), On(0), 0);
        cache.Feed(page, null, new Vector2(-50, 10), 10);
        cache.Feed(page, Versioned(5, 7), On(0), 20);
        cache.Feed(page, Versioned(6, 8), On(0), 30);
        Check.Equal(6f, cache.Rows(1, page.Listings[0])![0].Damage);
    }

    [Test]
    public void ClearForgetsTheBaseline()
    {
        var cache = new CompareCache { SettleMs = 0 };
        var page = Page(1, Listing(0), Listing(1));
        cache.Feed(page, Versioned(5, 7), On(0), 0);
        cache.Clear();
        cache.Feed(page, Versioned(5, 7), On(1), 10);
        Check.That(cache.Rows(1, page.Listings[1]) is not null, "no baseline after clear");
    }

    [Test]
    public void SettleTimeIsAdjustable()
    {
        var cache = new CompareCache { SettleMs = 50 };
        var page = Page(1, Listing(0));
        cache.Feed(page, Compare(5), On(0), 0);
        cache.Feed(page, Compare(5), On(0), 49);
        Check.That(cache.Rows(1, page.Listings[0]) is null, "not yet");
        cache.Feed(page, Compare(5), On(0), 50);
        Check.That(cache.Rows(1, page.Listings[0]) is not null, "settled");
        var instant = new CompareCache { SettleMs = 0 };
        instant.Feed(page, Compare(5), On(0), 0);
        Check.That(instant.Rows(1, page.Listings[0]) is not null, "zero settles at once");
    }
}
