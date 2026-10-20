using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class ShareTests
{
    const long Now = 1_800_000_000;
    static readonly DateTimeOffset Exported = new(2026, 10, 19, 12, 30, 0, TimeSpan.Zero);

    static string TempDirectory() => Path.Combine(Path.GetTempPath(), "prices-share-" + Guid.NewGuid().ToString("N"));

    static LocalRow Row(long id, long price = 100, long first = Now, long last = Now, int quantity = 10, int baseId = 7) =>
        new() { GoodId = id, BaseId = baseId, Price = price, Quantity = quantity, FirstSeen = first, LastSeen = last };

    static LocalBook Book(params LocalRow[] rows)
    {
        var book = new LocalBook();
        book.Merge(rows, new Dictionary<int, string>());
        return book;
    }

    static string Json(LocalRow row) => JsonSerializer.Serialize(row, LocalBook.Options);

    static Price Priced(float median, string? updated, int? samples = null) => new(median, null, null, updated is null ? null : DateTimeOffset.Parse(updated), samples);

    static ShareContent Content(int season = 1601, IReadOnlyList<LocalRow>? rows = null, IReadOnlyDictionary<int, string>? affixes = null, IReadOnlyDictionary<int, Price>? titrack = null, IReadOnlyDictionary<int, Price>? etor = null) =>
        new(season, rows, affixes, titrack, etor);

    static Main Plugin(string directory, int? season = 1601, bool local = false)
    {
        var plugin = new Main { DataDirectory = directory, LogSeason = season, UnixClock = () => Now };
        plugin.Settings.UseLocal = local;
        plugin.Settings.Enabled = false;
        plugin.Settings.GroundEnabled = false;
        return plugin;
    }

    static string Written(Main plugin) => Directory.GetFiles(plugin.ShareDirectory).Single(name => !name.EndsWith(".tmp"));

    [Test]
    public void RoundTripKeepsRowsAffixTextsAndPrices()
    {
        var directory = TempDirectory();
        var rich = Row(1, 250, Now - 500, Now - 5);
        rich.Affixes = [new RowAffix(TradeAffixKind.Prefix, 5001, 2), new RowAffix(TradeAffixKind.Suffix, 5002, 3)];
        rich.Flags = ["FORGE_PRECISE"];
        rich.Sold = true;
        rich.Unidentified = true;
        var affixes = new Dictionary<int, string> { [5001] = "+#(1-5) Strength", [5002] = "# Life" };
        var titrack = new Dictionary<int, Price> { [3] = new(1.5f, 1f, 2f, Exported, 12, 4) };
        var etor = new Dictionary<int, Price> { [4] = new(9f, null, null, Exported) };
        var path = Path.Combine(directory, "out.json.gz");
        Share.Write(path, Content(rows: [rich, Row(2)], affixes: affixes, titrack: titrack, etor: etor), Exported);
        var (read, error) = Share.Read(path);
        Check.That(error is null && read is not null, "reads back: " + error);
        Check.Equal(1601, read!.Season);
        Check.Equal(Json(rich), Json(read.LocalRows![0]));
        Check.Equal(2, read.LocalRows.Count);
        Check.Equal("+#(1-5) Strength", read.Affixes![5001]);
        Check.Equal(titrack[3], read.Titrack![3]);
        Check.Equal(etor[4], read.Etor![4]);
        Directory.Delete(directory, true);
    }

    [Test]
    public void OnlyTheChosenDatabasesAreWritten()
    {
        var directory = TempDirectory();
        var seed = Book(Row(1));
        seed.Flush(directory, 1601);
        Directory.CreateDirectory(directory);
        PriceSync.Save(Path.Combine(directory, "prices-1601.json"), new PriceFile(1601, null, new Dictionary<int, Price> { [3] = Priced(2, "2026-10-01T00:00:00Z") }));
        var plugin = Plugin(directory);
        Check.That(new Settings().SharedDatabases.SequenceEqual([PriceSource.Local]), "Local only by default");
        Check.That(plugin.ExportShare([PriceSource.Local]).StartsWith("Exported Local"), "export result");
        var local = Share.Read(Written(plugin)).Content!;
        Check.That(local.LocalRows is { Count: 1 } && local.Titrack is null && local.Etor is null, "only Local");
        File.Delete(Written(plugin));
        plugin.ExportShare([PriceSource.Titrack]);
        var titrack = Share.Read(Written(plugin)).Content!;
        Check.That(titrack.LocalRows is null && titrack.Etor is null && titrack.Titrack!.ContainsKey(3), "only TITrack");
        Check.Equal("Pick a database to export", plugin.ExportShare([]));
        Check.Equal("Season not known yet", Plugin(directory, null).ExportShare([PriceSource.Local]));
        Directory.Delete(directory, true);
    }

    [Test]
    public void MergeAddsUnknownAndAppliesTheRulesToKnownRows()
    {
        var book = Book(Row(1, 100, 1000, 2000), Row(2, 100, 1000, 3000), Row(3, 100, 1000, 2000), Row(4, 100, 1000, 2000), Row(5, 100, 1000, 2000));
        var withAffixes = Row(4, 100, 1000, 2000);
        withAffixes.Affixes = [new RowAffix(TradeAffixKind.Prefix, 7, 1)];
        withAffixes.Flags = ["x"];
        withAffixes.Forged = true;
        var ours = book.Rows.Single(row => row.GoodId == 5);
        ours.Affixes = [new RowAffix(TradeAffixKind.Prefix, 1, 1)];
        var theirs = Row(5, 100, 1000, 2000);
        theirs.Affixes = [new RowAffix(TradeAffixKind.Prefix, 9, 4)];
        var incoming = new List<LocalRow>
        {
            Row(1, 500, 500, 2500, 3),
            Row(2, 500, 500, 2500, 3),
            Row(3, 100, 1000, 2000),
            withAffixes,
            theirs,
            Row(6, 60),
            Row(0, 10), Row(7, 10, quantity: 0), Row(8, -1),
        };
        incoming[0].Sold = true;
        var (added, updated, skipped) = book.Merge(incoming, new Dictionary<int, string>());
        Check.Equal(1, added);
        Check.Equal(3, updated);
        Check.Equal(3, skipped);
        var rows = book.Rows.ToDictionary(row => row.GoodId);
        Check.Equal(500L, rows[1].Price);
        Check.Equal(3, rows[1].Quantity);
        Check.Equal(2500L, rows[1].LastSeen);
        Check.Equal(500L, rows[1].FirstSeen);
        Check.That(rows[1].Sold, "sold sticks");
        Check.Equal(100L, rows[2].Price);
        Check.Equal(10, rows[2].Quantity);
        Check.Equal(3000L, rows[2].LastSeen);
        Check.Equal(500L, rows[2].FirstSeen);
        Check.Equal(1, rows[4].Affixes!.Length);
        Check.That(rows[4].Forged && rows[4].Flags!.Length == 1, "empty fields are filled");
        Check.Equal(1, rows[5].Affixes![0].Id);
        Check.Equal(6, book.Count);
        Check.That(!rows.ContainsKey(0) && !rows.ContainsKey(7) && !rows.ContainsKey(8), "invalid rows skipped");
        var again = book.Merge(incoming.Where(row => row.GoodId is 1 or 6), new Dictionary<int, string>());
        Check.Equal((0, 0, 0), again);
    }

    [Test]
    public void MergeAffixTextsKeepOursOnAClash()
    {
        var book = new LocalBook();
        book.Merge([Row(1)], new Dictionary<int, string> { [1] = "ours" });
        book.Merge([Row(2)], new Dictionary<int, string> { [1] = "theirs", [2] = "new" });
        Check.Equal("ours", book.AffixTexts[1]);
        Check.Equal("new", book.AffixTexts[2]);
    }

    [Test]
    public void MergedRowsSurviveAFlushAndReload()
    {
        var directory = TempDirectory();
        var book = Book(Row(1, 100, 1000, 2000), Row(2));
        book.Flush(directory, 1601);
        var version = book.Version;
        var loaded = new LocalBook();
        loaded.Load(directory, 1601);
        var changedRow = Row(1, 500, 900, 2500, 3);
        changedRow.Affixes = [new RowAffix(TradeAffixKind.Suffix, 4, 2)];
        var (added, updated, _) = loaded.Merge([changedRow, Row(9, 40)], new Dictionary<int, string> { [4] = "# Life" });
        Check.Equal((1, 1), (added, updated));
        Check.That(loaded.Version != version || loaded.Dirty, "marked for the file");
        loaded.Flush(directory, 1601);
        Check.That(!loaded.Dirty, "flushed");
        var again = new LocalBook();
        again.Load(directory, 1601);
        var rows = again.Rows.ToDictionary(row => row.GoodId);
        Check.Equal(3, rows.Count);
        Check.Equal(500L, rows[1].Price);
        Check.Equal(900L, rows[1].FirstSeen);
        Check.Equal(2500L, rows[1].LastSeen);
        Check.Equal(4, rows[1].Affixes![0].Id);
        Check.Equal("# Life", again.AffixTexts[4]);
        Check.Equal(40L, rows[9].Price);
        Directory.Delete(directory, true);
    }

    [Test]
    public void NewestUpdatedWinsAndANullUpdatedLoses()
    {
        var ours = new Dictionary<int, Price> { [1] = Priced(10, "2026-10-10T00:00:00Z"), [2] = Priced(20, "2026-10-10T00:00:00Z"), [3] = Priced(30, null), [4] = Priced(40, "2026-10-10T00:00:00Z"), [5] = Priced(50, null) };
        var theirs = new Dictionary<int, Price> { [1] = Priced(11, "2026-10-11T00:00:00Z"), [2] = Priced(21, "2026-10-09T00:00:00Z"), [3] = Priced(31, "2026-01-01T00:00:00Z"), [4] = Priced(41, null), [5] = Priced(51, null), [6] = Priced(61, null) };
        var (merged, changed) = PriceSources.Newest(ours, theirs);
        Check.Equal(11f, merged[1].Median);
        Check.Equal(20f, merged[2].Median);
        Check.Equal(31f, merged[3].Median);
        Check.Equal(40f, merged[4].Median);
        Check.Equal(50f, merged[5].Median);
        Check.Equal(61f, merged[6].Median);
        Check.Equal(3, changed);
        Check.Equal(10f, ours[1].Median);
        Check.Equal(0, PriceSources.Newest(ours, ours).Changed);
    }

    sealed class Stub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    const string Downloaded = "[{\"config_base_id\":1,\"price_fe_median\":50,\"price_fe_p10\":1,\"price_fe_p90\":9,\"updated_at\":\"2026-10-19T10:00:00+00:00\"},{\"config_base_id\":2,\"price_fe_median\":60,\"price_fe_p10\":null,\"price_fe_p90\":null,\"updated_at\":\"2026-10-19T10:00:00+00:00\"}]";

    [Test]
    public void AnImportSurvivesTheNextDownloadAndNeverBeatsNewerPrices()
    {
        var directory = TempDirectory();
        var plugin = Plugin(directory);
        var shared = Path.Combine(directory, "in.json.gz");
        Share.Write(shared, Content(titrack: new Dictionary<int, Price> { [1] = Priced(5, "2026-10-18T00:00:00Z"), [2] = Priced(99, "2026-10-20T00:00:00Z"), [9] = Priced(7, "2026-10-01T00:00:00Z") }), Exported);
        Check.That(plugin.ImportShare(shared).Contains("3 TITrack prices"), "imported");
        Check.That(File.Exists(Path.Combine(directory, "imported-titrack-1601.json")), "overlay file");
        for (var round = 0; round < 2; round++)
        {
            using var http = new HttpClient(new Stub(Downloaded));
            var rows = PriceSources.Sync(http, directory, [PriceSource.Titrack], 1601, null, true).Tables![PriceSource.Titrack];
            Check.Equal(50f, rows[1].Median);
            Check.Equal(99f, rows[2].Median);
            Check.Equal(7f, rows[9].Median);
        }
        var cached = PriceSources.Cached(directory, [PriceSource.Titrack], 1601, null)!.Value.Synced.Tables![PriceSource.Titrack];
        Check.Equal(99f, cached[2].Median);
        Check.Equal(7f, cached[9].Median);
        Directory.Delete(directory, true);
    }

    [Test]
    public void GzipAndPlainJsonBothImportAndTheCountsAreReported()
    {
        var directory = TempDirectory();
        var gz = Path.Combine(directory, "a.json.gz");
        Share.Write(gz, Content(rows: [Row(1), Row(2), Row(3, -5)], affixes: new Dictionary<int, string> { [1] = "x" }), Exported);
        var plain = Path.Combine(directory, "b.json");
        using (var zip = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress))
        using (var output = File.Create(plain)) zip.CopyTo(output);
        Check.That(File.ReadAllBytes(plain)[0] == (byte)'{', "plain json");
        var first = Plugin(TempDirectory());
        Check.Equal("Imported 2 new and 0 updated listings (1 skipped) (season 1601)", first.ImportShare(gz));
        var second = Plugin(TempDirectory());
        Check.That(second.ImportShare(plain).StartsWith("Imported 2 new"), "plain");
        Check.Equal("Imported 0 new and 0 updated listings (1 skipped) (season 1601)", second.ImportShare(plain));
        var book = new LocalBook();
        book.Load(second.DataDirectory, 1601);
        Check.Equal(2, book.Count);
        Check.Equal("x", book.AffixTexts[1]);
        Directory.Delete(directory, true);
        Directory.Delete(first.DataDirectory, true);
        Directory.Delete(second.DataDirectory, true);
    }

    [Test]
    public void ARoundTripThroughTwoPluginsKeepsEverything()
    {
        var one = Plugin(TempDirectory());
        var seed = Book(Row(1, 100, 10, 20), Row(2, 300));
        seed.Flush(one.DataDirectory, 1601);
        Check.That(one.ExportShare([PriceSource.Local]).StartsWith("Exported"), "exported");
        var two = Plugin(TempDirectory());
        Check.That(two.ImportShare(Written(one)).Contains("2 new"), "imported");
        var copy = new LocalBook();
        copy.Load(two.DataDirectory, 1601);
        Check.That(copy.Rows.OrderBy(row => row.GoodId).Select(Json).SequenceEqual(seed.Rows.OrderBy(row => row.GoodId).Select(Json)), "same rows");
        Directory.Delete(one.DataDirectory, true);
        Directory.Delete(two.DataDirectory, true);
    }

    [Test]
    public void BadFilesGiveAMessageAndChangeNothing()
    {
        var directory = TempDirectory();
        Directory.CreateDirectory(directory);
        var plugin = Plugin(directory);
        var good = Path.Combine(directory, "good.json.gz");
        Share.Write(good, Content(rows: [Row(1), Row(2)]), Exported);
        var bytes = File.ReadAllBytes(good);
        var bad = new Dictionary<string, byte[]>
        {
            ["garbage.json.gz"] = [1, 2, 3, 4, 5],
            ["html.json"] = "<html>nope</html>"u8.ToArray(),
            ["format.json"] = "{\"format\":2,\"season\":1601,\"databases\":{\"local\":{\"rows\":[{\"g\":1,\"n\":1}]}}}"u8.ToArray(),
            ["nothing.json"] = "{}"u8.ToArray(),
            ["array.json"] = "[]"u8.ToArray(),
            ["truncated.json.gz"] = bytes[..(bytes.Length / 2)],
            ["empty.json"] = [],
        };
        foreach (var (name, data) in bad)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, data);
            var result = plugin.ImportShare(path);
            Check.That(!result.StartsWith("Imported"), name + " -> " + result);
        }
        Check.That(plugin.ImportShare(Path.Combine(directory, "missing.json")).Length > 0, "missing file");
        Check.That(!File.Exists(Path.Combine(directory, "local-1601.jsonl")) && !File.Exists(Path.Combine(directory, "imported-titrack-1601.json")), "nothing written");
        Directory.Delete(directory, true);
    }

    [Test]
    public void AnImportIntoTheOpenSeasonGoesIntoTheLiveBook()
    {
        var directory = TempDirectory();
        var plugin = Plugin(directory, local: true);
        plugin.Tick();
        Check.That(plugin.Book is not null, "book open");
        var shared = Path.Combine(directory, "in.json.gz");
        Share.Write(shared, Content(rows: [Row(1), Row(2)]), Exported);
        Check.That(plugin.ImportShare(shared).Contains("2 new"), "imported");
        Check.Equal(2, plugin.Book!.Count);
        Check.That(!File.Exists(Path.Combine(directory, "local-1601.jsonl")) || new FileInfo(Path.Combine(directory, "local-1601.jsonl")).Length == 0, "the live book writes its own file later");
        Directory.Delete(directory, true);
    }

    [Test]
    public void AFileFromAnotherSeasonGoesIntoThatSeasonsFiles()
    {
        var directory = TempDirectory();
        var plugin = Plugin(directory, local: true);
        plugin.Tick();
        var shared = Path.Combine(directory, "in.json.gz");
        Share.Write(shared, Content(1500, [Row(1)], titrack: new Dictionary<int, Price> { [1] = Priced(4, "2026-10-01T00:00:00Z") }), Exported);
        var result = plugin.ImportShare(shared);
        Check.That(result.Contains("season 1500") && result.Contains("not the current season 1601"), result);
        Check.Equal(0, plugin.Book!.Count);
        var other = new LocalBook();
        other.Load(directory, 1500);
        Check.Equal(1, other.Count);
        Check.Equal(4f, PriceSync.Load(Path.Combine(directory, "imported-titrack-1500.json"))!.Rows[1].Median);
        Directory.Delete(directory, true);
    }

    [Test]
    public void ShareSettingsDefaultToLocalOnlyAndThePageDraws()
    {
        var settings = new Settings();
        Check.That(!settings.ShareTitrack && !settings.ShareEtor && settings.ShareLocal, "Local only");
        Check.That(TestFrame.Vertices(settings.DrawShare, inWindow: true) > 0, "share tab draws");
    }
}
