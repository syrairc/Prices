using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class LocalTests
{
    const long Now = 1_800_000_000;

    static TradeGood Good(long id, int baseId, long price, int count = 1, string status = "Available", int priceType = 100300, int stackMax = 100, TradeAffix[]? affixes = null) =>
        new(id, priceType, price, status, new InventoryItem(InventoryBag.Commodities, 0, "id", baseId, 0, 0, 0, count, stackMax, 0)) { Affixes = affixes ?? [] };

    static string TempDirectory() => Path.Combine(Path.GetTempPath(), "prices-local-" + Guid.NewGuid().ToString("N"));

    static string FileOf(string directory) => Path.Combine(directory, "local-1601.jsonl");

    [Test]
    public void NewKnownAndSoldListings()
    {
        var book = new LocalBook();
        Check.That(book.Add([Good(1, 10, 100, 100)], Now), "new row");
        Check.Equal(1, book.Count);
        Check.That(!book.Add([Good(1, 10, 100, 100)], Now), "same list at the same time changes nothing");
        Check.That(book.Add([Good(1, 10, 100, 100)], Now + 5), "seen again");
        var row = book.Rows.Single();
        Check.Equal(Now, row.FirstSeen);
        Check.Equal(Now + 5, row.LastSeen);
        Check.That(book.Add([Good(1, 10, 120, 100)], Now + 6), "price change");
        Check.Equal(120L, book.Rows.Single().Price);
        Check.That(book.Add([Good(1, 10, 120, 100, "Sold")], Now + 7), "sold");
        Check.That(book.Rows.Single().Sold, "marked sold");
        Check.That(book.Add([Good(1, 10, 120, 100)], Now + 8) && book.Rows.Single().Sold, "an available read does not unsell it");
    }

    [Test]
    public void OnlyFeListingsAreStoredAndAbsenceIsNotEvidence()
    {
        var book = new LocalBook();
        book.Add([Good(1, 10, 100, 100), Good(2, 10, 5, 1, priceType: 100200)], Now);
        Check.Equal(1, book.Count);
        book.Add([Good(3, 10, 100, 100)], Now + 10);
        Check.Equal(2, book.Count);
        Check.That(book.Rows.All(row => !row.Sold), "row 1 absent from the later list is not gone");
    }

    [Test]
    public void AffixTextIsStrippedAndStoredOnce()
    {
        var book = new LocalBook();
        var affix = new TradeAffix(TradeAffixKind.Prefix, 5001, 2, [22f], "<p>+22(20-24) <e id=7>Strength</e></p>");
        var other = new TradeAffix(TradeAffixKind.Prefix, 5001, 2, [23f], "something else");
        book.Add([Good(1, 10, 5, affixes: [affix]), Good(2, 10, 6, affixes: [other])], Now);
        Check.Equal(1, book.AffixTexts.Count);
        Check.Equal("+22(20-24) Strength", book.AffixTexts[5001]);
        Check.Equal(23f, book.Rows.Single(row => row.GoodId == 2).Affixes![0].Rolls![0]);
    }

    [Test]
    public void AffixTextKeepsTheRangeButNotTheRoll()
    {
        var book = new LocalBook();
        book.Add([Good(1, 10, 5, affixes: [new TradeAffix(TradeAffixKind.Prefix, 7, 2, [55f], "<p>+55(46-55)</p>% Gear <e id=1>Physical</e> Damage"), new TradeAffix(TradeAffixKind.BaseAttribute, 8, 2, [154f, 160f], "<p>154</p> - <p>160</p> Physical Damage"), new TradeAffix(TradeAffixKind.Suffix, 9, 2, [-15f], "<p>-15(-15--14)</p>% additional Attack Damage")])], Now);
        Check.Equal("+{0}(46-55)% Gear Physical Damage", book.AffixTexts[7]);
        Check.Equal("{0} - {1} Physical Damage", book.AffixTexts[8]);
        Check.Equal("{0}(-15--14)% additional Attack Damage", book.AffixTexts[9]);
    }

    [Test]
    public void PricePickIgnoresTheOutlier()
    {
        var book = new LocalBook();
        var goods = new List<TradeGood>();
        for (var i = 0; i < 30; i++) goods.Add(Good(i + 1, 10, (long)((0.32 + i * 0.01) * 100), 100));
        goods.Add(Good(100, 10, 49950, 100));
        book.Add(goods, Now);
        var price = book.Prices(Now, LocalBook.Recent)[10];
        Check.That(price.Median >= 0.32f && price.Median <= 0.36f, price.Median.ToString());
        Check.That(price.High < 1f, "outlier not in High " + price.High);
        Check.Equal(30, price.Submissions);
        Check.Equal(1, price.Devices);
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(Now), price.Updated);
    }

    [Test]
    public void OnlyRecentUnsoldCommodityRowsArePriced()
    {
        var book = new LocalBook();
        book.Add([Good(1, 10, 50, 100)], Now - 90_000);
        book.Add([Good(2, 11, 50, 100), Good(3, 11, 60, 100, "Sold"), Good(4, 12, 900, 1, stackMax: 1)], Now);
        var prices = book.Prices(Now, LocalBook.Recent);
        Check.That(!prices.ContainsKey(10), "older than the window");
        Check.Equal(1, prices[11].Submissions);
        Check.That(!prices.ContainsKey(12), "gear not priced");
        Check.Equal(4, book.Count);
        Check.Equal(3, book.BaseCount);
    }

    [Test]
    public void PurgeRemovesOldRowsAndUnusedAffixes()
    {
        var book = new LocalBook();
        var affix = new TradeAffix(TradeAffixKind.Suffix, 9, 1, [1f], "x");
        book.Add([Good(1, 10, 5, affixes: [affix])], Now - 1000);
        book.Add([Good(2, 10, 5)], Now);
        Check.Equal(1, book.Purge(Now - 500));
        Check.Equal(1, book.Count);
        Check.Equal(0, book.AffixTexts.Count);
        Check.Equal(0, book.Purge(Now - 500));
    }

    [Test]
    public void RoundTripWithAppendedUpdatesAndABadTail()
    {
        var directory = TempDirectory();
        var book = new LocalBook();
        var affix = new TradeAffix(TradeAffixKind.Prefix, 5, 1, [3f, 4f], "<p>a</p>");
        book.Add([Good(1, 10, 100, 50, affixes: [affix]), Good(2, 10, 80, 40)], Now);
        book.Flush(directory, 1601);
        book.Add([Good(1, 10, 100, 50, "Sold"), Good(3, 11, 7, 7)], Now + 100);
        book.Flush(directory, 1601);
        var lines = File.ReadAllLines(FileOf(directory));
        Check.Equal(1 + 3 + 1, lines.Length);

        File.AppendAllText(FileOf(directory), "{\"r\":{\"g\":9,\"b\"");
        var loaded = new LocalBook();
        loaded.Load(directory, 1601);
        Check.Equal(3, loaded.Count);
        var first = loaded.Rows.Single(row => row.GoodId == 1);
        Check.That(first.Sold, "update line applied");
        Check.Equal(Now + 100, first.LastSeen);
        Check.Equal(4f, first.Affixes![0].Rolls![1]);
        Check.Equal("a", loaded.AffixTexts[5]);
        Check.Equal(50, first.Quantity);

        loaded.Flush(directory, 1601);
        var compacted = File.ReadAllLines(FileOf(directory));
        Check.Equal(1 + 3, compacted.Length);
        Check.That(compacted.All(line => line.EndsWith('}')), "bad tail gone");
        Directory.Delete(directory, true);
    }

    [Test]
    public void CompactsWhenUpdatesOutnumberRowsAndAfterPurge()
    {
        var directory = TempDirectory();
        var book = new LocalBook();
        book.Add([Good(1, 10, 100, 50)], Now);
        book.Flush(directory, 1601);
        for (var i = 1; i <= 3; i++)
        {
            book.Add([Good(1, 10, 100 + i, 50)], Now + i);
            book.Flush(directory, 1601);
        }
        Check.That(File.ReadAllLines(FileOf(directory)).Length <= 2, "rewritten once updates passed the rows");

        book.Add([Good(2, 10, 5)], Now + 5000);
        book.Flush(directory, 1601);
        Check.Equal(1, book.Purge(Now + 4));
        book.Flush(directory, 1601);
        Check.Equal(1, File.ReadAllLines(FileOf(directory)).Length);
        Check.That(!File.Exists(FileOf(directory) + ".tmp"), "temp file moved");
        Directory.Delete(directory, true);
    }

    [Test]
    public void MissingFileAndBrokenDirectoryDoNotThrow()
    {
        var book = new LocalBook();
        book.Load(TempDirectory(), 1601);
        book.Add([Good(1, 10, 5)], Now);
        var blocker = Path.GetTempFileName();
        book.Flush(blocker, 1601);
        Check.That(book.Dirty, "still dirty after a failed flush");
        File.Delete(blocker);
    }

    static Main Plugin(bool useLocal = true)
    {
        var plugin = new Main { DataDirectory = TempDirectory(), UnixClock = () => Now };
        plugin.Settings.UseTitrack = false;
        plugin.Settings.UseEtor = false;
        plugin.Settings.UseLocal = useLocal;
        plugin.Settings.Enabled = false;
        plugin.Settings.GroundEnabled = false;
        plugin.Settings.Season = 1601;
        return plugin;
    }

    static GameState Goods(params TradeGood[] goods) => GameState.Empty with { TradeGoods = goods };

    [Test]
    public void TickFeedsTheBookAndPublishesTheLocalTable()
    {
        var plugin = Plugin();
        TestGame.Set(Goods(Good(1, 10, 500, 100), Good(2, 10, 600, 100)));
        plugin.Tick();
        Check.Equal(2, plugin.Book!.Count);
        Check.That(plugin.Settings.Tables.ContainsKey(PriceSource.Local), "database tab table");
        Check.Equal(5f, plugin.Prices[10].Median);
        Check.That(plugin.Settings.Status.Contains("Local 2 rows"), plugin.Settings.Status);
        plugin.Dispose();
        Check.That(File.Exists(Path.Combine(plugin.DataDirectory, "local-1601.jsonl")), "flushed");
    }

    [Test]
    public void LocalOffReadsAndWritesNothing()
    {
        var plugin = Plugin(useLocal: false);
        TestGame.Set(Goods(Good(1, 10, 500, 100)));
        plugin.Tick();
        plugin.Dispose();
        Check.That(plugin.Book is null, "no book");
        Check.Equal(0, plugin.Prices.Count);
        Check.That(!Directory.Exists(plugin.DataDirectory), "nothing written");
    }

    [Test]
    public void BookReloadsFromDiskOnTheNextStart()
    {
        var first = Plugin();
        TestGame.Set(Goods(Good(1, 10, 500, 100)));
        first.Tick();
        first.Dispose();
        var second = Plugin();
        second.DataDirectory = first.DataDirectory;
        TestGame.Set(GameState.Empty);
        second.Tick();
        Check.Equal(1, second.Book!.Count);
        Check.Equal(5f, second.Prices[10].Median);
        second.Dispose();
    }

    [Test]
    public void PurgeNowUsesTheSetting()
    {
        var plugin = Plugin();
        TestGame.Set(Goods(Good(1, 10, 500, 100)));
        plugin.Tick();
        Check.Equal(0, plugin.PurgeNow());
        plugin.Settings.PurgeAfterDays = 1;
        plugin.UnixClock = () => Now + 3 * 86400;
        Check.Equal(1, plugin.PurgeNow());
        plugin.Dispose();
    }

    [Test]
    public void TheSettingsButtonPurgesThroughTheHook()
    {
        var plugin = Plugin();
        plugin.OnEnable();
        TestGame.Set(Goods(Good(1, 10, 500, 100)));
        plugin.Tick();
        plugin.Settings.PurgeAfterDays = 1;
        plugin.UnixClock = () => Now + 3 * 86400;
        Check.Equal(1, plugin.Settings.PurgeLocal!());
        plugin.Dispose();
    }

    [Test]
    public void IgnoreOlderThanDaysRebuildsWithoutADownload()
    {
        var plugin = Plugin();
        TestGame.Set(Goods(Good(1, 10, 500, 100)));
        plugin.Tick();
        Check.That(plugin.Prices.ContainsKey(10), "priced");
        plugin.UnixClock = () => Now + 3 * 86400;
        plugin.Settings.IgnoreOlderThanDays = 1;
        TestGame.Set(GameState.Empty);
        plugin.Tick();
        Check.That(!plugin.Prices.ContainsKey(10), "too old now");
        plugin.Settings.IgnoreOlderThanDays = 0;
        plugin.Tick();
        Check.That(plugin.Prices.ContainsKey(10), "back");
        plugin.Dispose();
    }
}
