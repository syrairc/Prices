using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class PriceSyncTests
{
    sealed class Stub(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request), Encoding.UTF8, "application/json") });
        }
    }

    const string Seasons = """[{"season_id":1601,"item_count":1392,"latest_update":"2026-10-17T15:40:00.06924+00:00"},{"season_id":1541,"item_count":28,"latest_update":"2026-09-13T16:15:00.081012+00:00"},{"season_id":1501,"item_count":2210,"latest_update":"2026-10-17T15:40:00.06924+00:00"}]""";

    static readonly DateTimeOffset Now = new(2026, 10, 17, 16, 0, 0, TimeSpan.Zero);

    static PriceFile File(string? newest, params (int Id, float Median)[] rows)
    {
        var table = new Dictionary<int, Price>();
        foreach (var (id, median) in rows) table[id] = new Price(median, null, null);
        return new PriceFile(1601, newest, table);
    }

    static string TempPath() => Path.Combine(Path.GetTempPath(), "prices-test-" + Guid.NewGuid().ToString("N"));

    static string Page(int first, int count)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < count; i++)
            sb.Append(i > 0 ? "," : "").Append($$"""{"config_base_id":{{first + i}},"price_fe_median":2,"price_fe_p10":1,"price_fe_p90":3,"updated_at":"2026-10-17T14:00:00+00:00"}""");
        return sb.Append(']').ToString();
    }

    static string Reply(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("get_available_seasons") ? Seasons : Page(1, 2);

    static Main Syncing(Stub stub, long[]? clock = null) => new()
    {
        DataDirectory = TempPath(),
        Http = new HttpClient(stub),
        Job = new BackgroundJob<Synced>(inline: true),
        Clock = () => clock?[0] ?? 0,
    };

    sealed class Silent : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => new TaskCompletionSource<HttpResponseMessage>().Task;
    }

    static void Clean(params Main[] plugins)
    {
        foreach (var plugin in plugins)
            if (Directory.Exists(plugin.DataDirectory)) Directory.Delete(plugin.DataDirectory, true);
    }

    [Test]
    public void PricesUrlHasSeasonPagingAndSince()
    {
        var url = PriceSync.PricesUrl(1601, "2026-10-17T14:45:00+00:00", 1000);
        foreach (var part in new[] { "season_id=eq.1601", "offset=1000", "limit=1000", "updated_at=gt.2026-10-17T14:45:00%2B00:00" })
            Check.That(url.Contains(part), part + " in " + url);
        Check.That(url.StartsWith(PriceSync.Base + "/rest/v1/aggregated_prices?"), "base and table");
        Check.That(!PriceSync.PricesUrl(1601, null, 0).Contains("updated_at=gt"), "no since, no filter");
    }

    [Test]
    public void ParseReadsRowsAndNewest()
    {
        const string json = """[{"config_base_id":115,"price_fe_median":1,"price_fe_p10":null,"price_fe_p90":null,"updated_at":"2026-10-17T13:20:00.110608+00:00"},{"config_base_id":118,"price_fe_median":5.5,"price_fe_p10":4,"price_fe_p90":9,"updated_at":"2026-10-17T15:40:00.06924+00:00"}]""";
        var (rows, newest) = PriceSync.Parse(json);
        Check.Equal(2, rows.Count);
        Check.Equal(new Price(1, null, null), rows[115]);
        Check.Equal(new Price(5.5f, 4, 9), rows[118]);
        Check.Equal("2026-10-17T15:40:00.06924+00:00", newest);
    }

    [Test]
    public void MergeUpsertsByIdAndKeepsTheRest()
    {
        var merged = PriceSync.Merge(File("2026-10-17T10:00:00+00:00", (1, 5), (2, 7)), File(null, (2, 9), (3, 1)).Rows, "2026-10-17T12:00:00+00:00");
        Check.Equal(3, merged.Rows.Count);
        Check.Equal(5f, merged.Rows[1].Median);
        Check.Equal(9f, merged.Rows[2].Median);
        Check.Equal(1f, merged.Rows[3].Median);
        Check.Equal("2026-10-17T12:00:00+00:00", merged.Newest);
        Check.Equal("2026-10-17T12:00:00+00:00", PriceSync.Merge(File("2026-10-17T12:00:00+00:00"), [], "2026-10-17T10:00:00+00:00").Newest);
        Check.Equal("2026-10-17T10:00:00+00:00", PriceSync.Merge(File(null), [], "2026-10-17T10:00:00+00:00").Newest);
    }

    [Test]
    public void LatestSeasonPicksTheHighestRecentlyActive()
    {
        Check.Equal(1601, PriceSync.LatestSeason(Seasons, Now));
        Check.Equal(null, PriceSync.LatestSeason(Seasons, Now.AddDays(30)));
    }

    [Test]
    public void SaveThenLoadRoundTrips()
    {
        var directory = TempPath();
        var path = Path.Combine(directory, "Config", "prices-1601.json");
        var file = new PriceFile(1601, "2026-10-17T12:00:00+00:00", new Dictionary<int, Price> { [1] = new(5, null, null), [2] = new(7.5f, 6, 9) });
        PriceSync.Save(path, file);
        var back = PriceSync.Load(path)!;
        Check.Equal(1601, back.Season);
        Check.Equal(file.Newest, back.Newest);
        Check.Equal(2, back.Rows.Count);
        Check.Equal(new Price(7.5f, 6, 9), back.Rows[2]);
        Check.Equal(null, PriceSync.Load(path + ".missing"));
        Directory.Delete(directory, true);
    }

    [Test]
    public void RefreshPagesUntilAShortPageAndSendsSinceForADelta()
    {
        var delta = false;
        var stub = new Stub(request => delta ? Page(1, 1) : request.RequestUri!.Query.Contains("offset=0") ? Page(1, 1000) : Page(1001, 1));
        using var http = new HttpClient(stub);
        var full = PriceSync.Refresh(http, null, 1601).GetAwaiter().GetResult();
        Check.Equal(1001, full.Rows.Count);
        Check.Equal(2, stub.Urls.Count);
        Check.That(stub.Urls[1].Contains("offset=1000"), "second page offset");
        Check.That(!stub.Urls[0].Contains("updated_at=gt"), "full fetch has no since");

        stub.Urls.Clear();
        delta = true;
        PriceSync.Refresh(http, full, 1601).GetAwaiter().GetResult();
        Check.Equal(1, stub.Urls.Count);
        Check.That(stub.Urls[0].Contains("updated_at=gt.2026-10-17T14:00:00%2B00:00"), "delta since");
    }

    [Test]
    public void BrokenJsonKeepsTheOldTable()
    {
        Check.Throws<JsonException>(() => PriceSync.Parse("[{"));
        using var http = new HttpClient(new Stub(_ => "[{"));
        Check.Throws<JsonException>(() => PriceSync.Refresh(http, null, 1601).GetAwaiter().GetResult());

        var plugin = Syncing(new Stub(_ => "[{"));
        plugin.Prices = new Dictionary<int, Price> { [1] = new(8, null, null) };
        plugin.Settings.Season = 1601;
        plugin.Tick();
        plugin.Tick();
        Check.Equal(8f, plugin.Prices[1].Median);
        Check.That(plugin.Settings.Status.StartsWith("download failed: "), plugin.Settings.Status);
        Check.That(plugin.Settings.Status.Contains("1601"), "status names the season");
        Clean(plugin);
    }

    [Test]
    public void SeasonResolvesSettingThenLogThenServer()
    {
        var stub = new Stub(Reply);
        var plugin = Syncing(stub);
        plugin.Settings.Season = 1501;
        plugin.LogSeason = 1541;
        plugin.Tick();
        Check.That(stub.Urls.TrueForAll(url => !url.Contains("rpc")), "setting wins, no season lookup");
        Check.That(stub.Urls[0].Contains("season_id=eq.1501"), stub.Urls[0]);
        Clean(plugin);

        stub = new Stub(Reply);
        plugin = Syncing(stub);
        plugin.LogSeason = 1541;
        plugin.Tick();
        plugin.Tick();
        Check.That(stub.Urls[0].Contains("season_id=eq.1541"), "log season: " + stub.Urls[0]);
        Check.That(plugin.Settings.Status.Contains("1541") && plugin.Settings.Status.Contains("game log"), plugin.Settings.Status);
        Clean(plugin);

        stub = new Stub(Reply);
        plugin = Syncing(stub);
        plugin.Tick();
        plugin.Tick();
        Check.That(stub.Urls.Exists(url => url.Contains("rpc/get_available_seasons")), "asks the server");
        Check.That(stub.Urls.Exists(url => url.Contains("season_id=eq.1601")), "newest active season");
        Check.Equal(2, plugin.Prices.Count);
        Check.That(plugin.Settings.Status.Contains("1601") && plugin.Settings.Status.Contains("server"), plugin.Settings.Status);
        Clean(plugin);
    }

    [Test]
    public void RefreshRunsAgainAfterTheIntervalAndClampsToFiveMinutes()
    {
        var clock = new long[1];
        var stub = new Stub(Reply);
        var plugin = Syncing(stub, clock);
        plugin.Settings.Season = 1601;
        plugin.Settings.RefreshMinutes = 1;
        plugin.Tick();
        plugin.Tick();
        var first = stub.Urls.Count;
        clock[0] = 4 * 60_000;
        plugin.Tick();
        Check.Equal(first, stub.Urls.Count);
        clock[0] = 5 * 60_000;
        plugin.Tick();
        Check.That(stub.Urls.Count > first, "refreshes after 5 minutes");
        Clean(plugin);
    }

    [Test]
    public void OfflineAutoSeasonShowsTheCachedTable()
    {
        var plugin = Syncing(new Stub(_ => throw new HttpRequestException("offline")));
        PriceSync.Save(Path.Combine(plugin.DataDirectory, "prices-1601.json"), File(null, (7, 3)));
        plugin.Tick();
        plugin.Tick();
        Check.Equal(3f, plugin.Prices[7].Median);
        Check.That(plugin.Settings.Status.StartsWith("download failed: offline"), plugin.Settings.Status);
        Check.That(plugin.Settings.Status.Contains("1601") && plugin.Settings.Status.Contains("cached"), plugin.Settings.Status);
        Clean(plugin);
    }

    [Test]
    public void OfflineFallbackPicksTheNewestFileNotTheHighestSeason()
    {
        var plugin = Syncing(new Stub(_ => throw new HttpRequestException("offline")));
        var junk = Path.Combine(plugin.DataDirectory, "prices-2999.json");
        PriceSync.Save(junk, File(null, (9, 9)));
        MakeOld(junk);
        PriceSync.Save(Path.Combine(plugin.DataDirectory, "prices-1601.json"), File(null, (7, 3)));
        plugin.Tick();
        plugin.Tick();
        Check.That(plugin.Prices.ContainsKey(7) && !plugin.Prices.ContainsKey(9), "newest file wins");
        Clean(plugin);
    }

    static void MakeOld(string path) => System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));

    [Test]
    public void EnableLoadsTheCacheBeforeAnyNetwork()
    {
        var plugin = new Main { DataDirectory = TempPath(), Http = new HttpClient(new Silent()), Job = new BackgroundJob<Synced>(inline: true) };
        var old = Path.Combine(plugin.DataDirectory, "prices-1501.json");
        PriceSync.Save(old, new PriceFile(1501, null, new Dictionary<int, Price> { [5] = new(1, null, null) }));
        MakeOld(old);
        PriceSync.Save(Path.Combine(plugin.DataDirectory, "prices-1601.json"), File(null, (7, 3)));
        plugin.OnEnable();
        Check.Equal(3f, plugin.Prices[7].Median);
        Check.That(plugin.Settings.Status.Contains("1601"), plugin.Settings.Status);

        plugin.Settings.Season = 1501;
        plugin.OnEnable();
        Check.Equal(1f, plugin.Prices[5].Median);
        Clean(plugin);
    }

    [Test]
    public void ZeroRowFetchWithoutACacheSavesNothing()
    {
        var plugin = Syncing(new Stub(_ => "[]"));
        plugin.Settings.Season = 2999;
        plugin.Tick();
        plugin.Tick();
        Check.That(!Directory.Exists(plugin.DataDirectory) || Directory.GetFiles(plugin.DataDirectory).Length == 0, "no junk cache file");
        Clean(plugin);
    }

    [Test]
    public void NullMedianRowIsSkippedAndTheRestKept()
    {
        const string json = """[{"config_base_id":1,"price_fe_median":null,"price_fe_p10":null,"price_fe_p90":null,"updated_at":"2026-10-17T13:00:00+00:00"},{"config_base_id":2,"price_fe_median":4,"price_fe_p10":null,"price_fe_p90":null,"updated_at":"2026-10-17T12:00:00+00:00"}]""";
        var (rows, newest) = PriceSync.Parse(json);
        Check.Equal(1, rows.Count);
        Check.Equal(4f, rows[2].Median);
        Check.Equal("2026-10-17T13:00:00+00:00", newest);
    }

    [Test]
    public void RefreshStopsAtTheMaxPages()
    {
        var stub = new Stub(_ => Page(1, 1000));
        using var http = new HttpClient(stub);
        PriceSync.Refresh(http, null, 1601).GetAwaiter().GetResult();
        Check.Equal(PriceSync.MaxPages, stub.Urls.Count);
    }

    [Test]
    public void FailedRefreshOfAnotherSeasonWithoutACacheClearsTheTable()
    {
        var fail = false;
        var plugin = Syncing(new Stub(request => fail ? throw new HttpRequestException("down") : Reply(request)));
        plugin.Settings.Season = 1601;
        plugin.Tick();
        plugin.Tick();
        Check.Equal(2, plugin.Prices.Count);
        fail = true;
        plugin.Settings.Season = 1501;
        plugin.Tick();
        plugin.Tick();
        Check.Equal(0, plugin.Prices.Count);
        Check.That(plugin.Settings.Status.StartsWith("download failed: down"), plugin.Settings.Status);
        Clean(plugin);
    }

    [Test]
    public void FirstSyncIsFullAndReplacesTheCachedTableThenDeltas()
    {
        var clock = new long[1];
        var stub = new Stub(Reply);
        var plugin = Syncing(stub, clock);
        plugin.Settings.Season = 1601;
        PriceSync.Save(Path.Combine(plugin.DataDirectory, "prices-1601.json"), File("2026-10-17T10:00:00+00:00", (9, 4)));
        plugin.OnEnable();
        Check.That(plugin.Prices.ContainsKey(9), "cache shown first");
        plugin.Tick();
        plugin.Tick();
        Check.That(stub.Urls.TrueForAll(url => !url.Contains("updated_at=gt")), "first sync has no since");
        Check.Equal(2, plugin.Prices.Count);
        Check.That(!plugin.Prices.ContainsKey(9), "dropped row is gone");
        stub.Urls.Clear();
        clock[0] = 5 * 60_000;
        plugin.Tick();
        Check.That(stub.Urls.Count > 0 && stub.Urls.TrueForAll(url => url.Contains("updated_at=gt")), "later refresh is a delta");
        plugin.Tick();
        Check.Equal(2, plugin.Prices.Count);
        Clean(plugin);
    }

    [Test]
    public void ResultOfAnOldSeasonKeyIsDropped()
    {
        var plugin = Syncing(new Stub(Reply));
        plugin.Prices = new Dictionary<int, Price> { [99] = new(1, null, null) };
        plugin.Settings.Season = 1501;
        plugin.Tick();
        plugin.Settings.Season = 1601;
        plugin.Tick();
        Check.That(plugin.Prices.Count == 1 && plugin.Prices.ContainsKey(99), "1501 result not shown");
        plugin.Tick();
        Check.Equal(2, plugin.Prices.Count);
        Clean(plugin);
    }

    [Test]
    public void CachedStatusShowsTheFileTime()
    {
        var plugin = new Main { DataDirectory = TempPath(), Http = new HttpClient(new Silent()), Job = new BackgroundJob<Synced>(inline: true) };
        var path = Path.Combine(plugin.DataDirectory, "prices-1601.json");
        PriceSync.Save(path, File(null, (7, 3)));
        System.IO.File.SetLastWriteTime(path, new DateTime(2026, 10, 10, 8, 7, 0));
        plugin.OnEnable();
        Check.That(plugin.Settings.Status.Contains("updated 08:07"), plugin.Settings.Status);
        Clean(plugin);
    }
}
