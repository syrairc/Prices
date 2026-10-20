using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmberHUD.Testing;

namespace Prices;

public sealed class SourcesTests
{
    sealed class Stub(Func<HttpRequestMessage, (HttpStatusCode Code, byte[] Body)> reply) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            var (code, body) = reply(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });
        }
    }

    static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return [.. bytes];
    }

    static byte[] Text(int field, string value) => [.. Varint((ulong)(field << 3 | 2)), .. Varint((ulong)value.Length), .. Encoding.UTF8.GetBytes(value)];

    static byte[] Float(int field, float value) => [.. Varint((ulong)(field << 3 | 5)), .. BitConverter.GetBytes(value)];

    static byte[] Item(string id, float price) => Wrap(2, [.. Text(1, id), .. Text(2, "1601"), .. Float(3, price), .. Text(4, "2026-10-19T15:00:00.000Z"), .. Varint(5 << 3 | 2), .. Varint(2), .. new byte[] { 2, 4 }]);

    static byte[] Wrap(int field, byte[] inner) => [.. Varint((ulong)(field << 3 | 2)), .. Varint((ulong)inner.Length), .. inner];

    static byte[] Snapshot(params (string Id, float Price)[] items) =>
        [.. Text(1, "1601"), .. items.SelectMany(item => Item(item.Id, item.Price)), .. Text(3, "full"), .. Text(4, "abc"), .. Varint(5 << 3), .. Varint((ulong)items.Length)];

    static Dictionary<int, Price> Table(params (int Id, float Median)[] rows) => rows.ToDictionary(row => row.Id, row => new Price(row.Median, null, null));

    static string TempPath() => Path.Combine(Path.GetTempPath(), "prices-sources-" + Guid.NewGuid().ToString("N"));

    [Test]
    public void ParseReadsIdAndPriceAndSkipsTheRest()
    {
        var rows = EtorSync.Parse(Snapshot(("5028", 12.5f), ("115", 0.25f), ("bad", 3f), ("200", 0f)));
        Check.Equal(2, rows.Count);
        var updated = DateTimeOffset.Parse("2026-10-19T15:00:00Z");
        Check.Equal(new Price(12.5f, null, null, updated), rows[5028]);
        Check.Equal(new Price(0.25f, null, null, updated), rows[115]);
    }

    [Test]
    public void ParseRejectsGarbage()
    {
        var failed = false;
        try
        {
            EtorSync.Parse("<html>not protobuf</html>"u8.ToArray());
        }
        catch (FormatException)
        {
            failed = true;
        }
        Check.That(failed, "html is not a snapshot");
    }

    [Test]
    public void FetchFallsThroughToTheNextHostAndAsksForProtobuf()
    {
        var first = EtorSync.Hosts[0];
        var stub = new Stub(request => request.RequestUri!.ToString().StartsWith(first) ? (HttpStatusCode.BadGateway, "error code: 502"u8.ToArray()) : (HttpStatusCode.OK, Snapshot(("7", 3f))));
        using var http = new HttpClient(stub);
        var rows = EtorSync.Fetch(http, 1601).GetAwaiter().GetResult();
        Check.Equal(1, rows.Count);
        Check.Equal(2, stub.Urls.Count);
        Check.That(stub.Urls[1].Contains("/api/prices-snapshot/1601?format=protobuf"), "snapshot url " + stub.Urls[1]);
    }

    [Test]
    public void CombineTakesTheFirstTableThatHasTheItem()
    {
        var merged = PriceSources.Combine([Table((1, 10), (2, 20)), Table((2, 99), (3, 30))]);
        Check.Equal(3, merged.Count);
        Check.Equal(10f, merged[1].Median);
        Check.Equal(20f, merged[2].Median);
        Check.Equal(30f, merged[3].Median);
    }

    static Synced Sync(Stub stub, string directory, PriceSource[] active)
    {
        using var http = new HttpClient(stub);
        return PriceSources.Sync(http, directory, active, 1601, null, true);
    }

    static (HttpStatusCode, byte[]) Reply(HttpRequestMessage request) =>
        request.RequestUri!.Host.StartsWith("qhjulyngunwiculnharg") ? (HttpStatusCode.OK, "[{\"config_base_id\":1,\"price_fe_median\":50,\"price_fe_p10\":1,\"price_fe_p90\":9,\"updated_at\":\"2026-10-19T10:00:00+00:00\"},{\"config_base_id\":2,\"price_fe_median\":60,\"price_fe_p10\":null,\"price_fe_p90\":null,\"updated_at\":\"2026-10-19T10:00:00+00:00\"}]"u8.ToArray())
        : (HttpStatusCode.OK, Snapshot(("2", 6f), ("3", 7f)));

    [Test]
    public void SyncPriorityDecidesWhoWinsAnItem()
    {
        var directory = TempPath();
        var etorFirst = Sync(new Stub(Reply), directory, [PriceSource.Etor, PriceSource.Titrack]);
        Check.Equal(3, etorFirst.File!.Rows.Count);
        Check.Equal(6f, etorFirst.File.Rows[2].Median);
        Check.Equal(50f, etorFirst.File.Rows[1].Median);
        Check.Equal("ETor, TITrack", etorFirst.Databases);
        var titrackFirst = Sync(new Stub(Reply), directory, [PriceSource.Titrack, PriceSource.Etor]);
        Check.Equal(60f, titrackFirst.File!.Rows[2].Median);
        Check.Equal(7f, titrackFirst.File.Rows[3].Median);
        Directory.Delete(directory, true);
    }

    [Test]
    public void SyncAsksOnlyTheEnabledDatabases()
    {
        var directory = TempPath();
        var stub = new Stub(Reply);
        var etorOnly = Sync(stub, directory, [PriceSource.Etor]);
        Check.Equal(2, etorOnly.File!.Rows.Count);
        Check.That(stub.Urls.All(url => !url.Contains("supabase")), "no TITrack request");
        stub.Urls.Clear();
        var titrackOnly = Sync(stub, directory, [PriceSource.Titrack]);
        Check.Equal(2, titrackOnly.File!.Rows.Count);
        Check.That(stub.Urls.All(url => url.Contains("supabase")), "no ETor request");
        Directory.Delete(directory, true);
    }

    [Test]
    public void SyncKeepsTheOtherDatabaseWhenOneFails()
    {
        var directory = TempPath();
        var down = Sync(new Stub(request => request.RequestUri!.Host.StartsWith("qhjulyngunwiculnharg") ? Reply(request) : (HttpStatusCode.BadGateway, [])), directory, [PriceSource.Etor, PriceSource.Titrack]);
        Check.Equal(2, down.File!.Rows.Count);
        Check.That(down.Error is not null && down.Error.StartsWith("ETor"), "error names ETor: " + down.Error);
        Directory.Delete(directory, true);
    }

    [Test]
    public void SyncFallsBackToTheCachedEtorFile()
    {
        var directory = TempPath();
        Sync(new Stub(Reply), directory, [PriceSource.Etor]);
        var offline = Sync(new Stub(_ => (HttpStatusCode.BadGateway, [])), directory, [PriceSource.Etor]);
        Check.Equal(2, offline.File!.Rows.Count);
        Check.That(offline.Error is not null, "still reports the failure");
        Directory.Delete(directory, true);
    }

    [Test]
    public void CachedCombinesTheEnabledCaches()
    {
        var directory = TempPath();
        Sync(new Stub(Reply), directory, [PriceSource.Etor, PriceSource.Titrack]);
        var cached = PriceSources.Cached(directory, [PriceSource.Etor], 1601, null)!.Value;
        Check.Equal(2, cached.Synced.File!.Rows.Count);
        Check.Equal(6f, cached.Synced.File.Rows[2].Median);
        Check.Equal("cached", cached.Synced.Source);
        Check.That(PriceSources.Cached(directory, [PriceSource.Etor], 1701, null) is null, "no cache for another season");
        Directory.Delete(directory, true);
    }

    [Test]
    public void SettingsOrderAndActive()
    {
        var settings = new Settings { UseTitrack = true, UseEtor = true, UseLocal = false, Priority = [PriceSource.Titrack] };
        Check.That(settings.Order().SequenceEqual([PriceSource.Titrack, PriceSource.Etor, PriceSource.Local]), "missing sources are appended");
        Check.That(settings.Active.SequenceEqual([PriceSource.Titrack, PriceSource.Etor]), "only enabled ones, in order");
        Check.That(new Settings { Priority = [PriceSource.Etor, PriceSource.Etor] }.Order().Length == 3, "duplicates collapse");
    }

    [Test]
    public void SyncKeepsEachDatabaseTableForTheViewer()
    {
        var directory = TempPath();
        var synced = Sync(new Stub(Reply), directory, [PriceSource.Etor, PriceSource.Titrack]);
        Check.Equal(2, synced.Tables![PriceSource.Etor].Count);
        Check.Equal(2, synced.Tables[PriceSource.Titrack].Count);
        Check.Equal(50f, synced.Tables[PriceSource.Titrack][1].Median);
        var cached = PriceSources.Cached(directory, [PriceSource.Etor, PriceSource.Titrack], 1601, null)!.Value;
        Check.Equal(2, cached.Synced.Tables![PriceSource.Titrack].Count);
        Directory.Delete(directory, true);
    }

    [Test]
    public void ViewerRowsAreSortedByBaseId()
    {
        var rows = Settings.Rows(new Dictionary<int, Price> { [9] = new(1, null, null), [3] = new(2, 1, 4) });
        Check.Equal(3, rows[0].BaseId);
        Check.Equal(4f, rows[0].High ?? 0);
        Check.Equal(9, rows[1].BaseId);
    }

    [Test]
    public void CombineSkipsThinPricesSoALowerDatabaseCanPriceTheItem()
    {
        var titrack = new Dictionary<int, Price> { [1] = new(10, null, null, null, 2), [2] = new(20, null, null, null, 50), [3] = new(30, null, null, null, 1) };
        var etor = new Dictionary<int, Price> { [1] = new(11, null, null), [4] = new(44, null, null) };
        var merged = PriceSources.Combine([titrack, etor], 5);
        Check.Equal(11f, merged[1].Median);
        Check.Equal(20f, merged[2].Median);
        Check.That(!merged.ContainsKey(3), "thin and nowhere else");
        Check.Equal(44f, merged[4].Median);
        Check.Equal(10f, PriceSources.Combine([titrack, etor])[1].Median);
        Check.Equal(10f, PriceSources.Combine([titrack, etor], 2)[1].Median);
    }
}
