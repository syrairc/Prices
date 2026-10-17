using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmberHUD.Testing;

namespace Prices;

public sealed class ContributeTests
{
    sealed class Stub(string reply) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    static readonly DateTime Now = new(2026, 10, 17, 16, 0, 0);

    static string TempFile() => Path.Combine(Path.GetTempPath(), "prices-log-" + Guid.NewGuid().ToString("N") + ".log");

    static void Write(string path, params string[] lines) => File.WriteAllText(path, string.Join("\n", lines) + "\n");

    static void Append(string path, params string[] lines) => File.AppendAllText(path, string.Join("\n", lines) + "\n");

    static Search Sample => new(5028, [1.5, 2.0, 2.5]);

    [Test]
    public void TailReadsOnlyNewLines()
    {
        var path = TempFile();
        try
        {
            Write(path, "one", "two");
            var tail = new LogTail(path);
            Check.Equal(2, tail.ReadNew().Count);
            Append(path, "three");
            var next = tail.ReadNew();
            Check.Equal(1, next.Count);
            Check.Equal("three", next[0]);
            Check.Equal(0, tail.ReadNew().Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void TailFromEndSkipsOldLines()
    {
        var path = TempFile();
        try
        {
            Write(path, "old");
            var tail = new LogTail(path);
            tail.SkipToEnd();
            Check.Equal(0, tail.ReadNew().Count);
            Append(path, "new");
            Check.Equal("new", tail.ReadNew()[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ShrunkFileRestartsFromZero()
    {
        var path = TempFile();
        try
        {
            Write(path, "one", "two", "three");
            var tail = new LogTail(path);
            Check.Equal(3, tail.ReadNew().Count);
            Write(path, "x");
            var lines = tail.ReadNew();
            Check.Equal(1, lines.Count);
            Check.Equal("x", lines[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void MissingLogIsEmpty()
    {
        Check.Equal(0, new LogTail(TempFile()).ReadNew().Count);
        Check.That(LogTail.LastSeason(TempFile(), 4 << 20) is null, "no file, no season");
    }

    [Test]
    public void LastSeasonFindsTheNewestLine()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "+player+SeasonId [1501]\nother\n|      +SeasonId [1601]\nmore\n", new UTF8Encoding(true));
            Check.Equal(1601, LogTail.LastSeason(path, 4 << 20) ?? 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void SubmitBodyMatchesTitrack()
    {
        var device = Guid.NewGuid().ToString();
        using var body = JsonDocument.Parse(Contribution.Body(device, new Search(5028, [1.5, 2.0, 2.5]), 1601));
        var root = body.RootElement;
        Check.Equal(5, root.EnumerateObject().Count());
        Check.Equal(device, root.GetProperty("p_device_id").GetString());
        Check.Equal(5028, root.GetProperty("p_config_base_id").GetInt32());
        Check.Equal(1601, root.GetProperty("p_season_id").GetInt32());
        Check.Equal(1.5, root.GetProperty("p_price_fe").GetDouble());
        var prices = root.GetProperty("p_prices_array");
        Check.Equal(JsonValueKind.Array, prices.ValueKind);
        Check.Equal(3, prices.GetArrayLength());
        Check.Equal(JsonValueKind.Number, prices[0].ValueKind);
        Check.Equal(1.5, prices[0].GetDouble());
        Check.Equal(2.5, prices[2].GetDouble());
    }

    sealed class Scratch : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "prices-test-" + Guid.NewGuid().ToString("N"));

        public Scratch() => System.IO.Directory.CreateDirectory(Directory);

        public string Log => Path.Combine(Directory, "UE_game.log");

        public Stub Stub { get; private set; } = null!;

        public Contribution Contribution(string reply = """{"success": true}""")
        {
            Stub = new Stub(reply);
            return new Contribution(new HttpClient(Stub), Log, Directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    const string SearchLines = """
----Socket SendMessage STT----XchgSearchPrice----SynId = 3
|       | +refer [5028]
----Socket SendMessage End----
----Socket RecvMessage STT----XchgSearchPrice----SynId = 3
+prices+1+currency [100300]
|      | +unitPrices+1 [4.0]
----Socket RecvMessage End----
""";

    static int SeasonSent(Stub stub, int index)
    {
        using var body = JsonDocument.Parse(stub.Bodies[index]);
        return body.RootElement.GetProperty("p_season_id").GetInt32();
    }

    [Test]
    public void NothingSentWhenOff()
    {
        using var scratch = new Scratch();
        var contribution = scratch.Contribution();
        contribution.Queue(Sample, 1601);
        contribution.Flush(() => false, Now);
        Check.Equal(0, scratch.Stub.Bodies.Count);
        contribution.Flush(() => true, Now);
        Check.Equal(1, scratch.Stub.Bodies.Count);
        Check.Equal(1, contribution.Sent);
    }

    [Test]
    public void NothingSentWithoutALogSeason()
    {
        using var scratch = new Scratch();
        var contribution = scratch.Contribution();
        contribution.Queue(Sample, 0);
        contribution.Flush(() => true, Now);
        Check.Equal(0, scratch.Stub.Bodies.Count);
    }

    [Test]
    public void SeasonOverrideNeverLabelsASubmission()
    {
        using var scratch = new Scratch();
        Write(scratch.Log, "start");
        var contribution = scratch.Contribution();
        contribution.Start();
        Append(scratch.Log, "+player+SeasonId [1601]", SearchLines);
        contribution.Poll(Now);
        contribution.Flush(() => true, Now);
        Check.Equal(1, scratch.Stub.Bodies.Count);
        Check.Equal(1601, SeasonSent(scratch.Stub, 0));
    }

    [Test]
    public void SeasonOverrideWithoutALogSeasonSendsNothing()
    {
        using var scratch = new Scratch();
        Write(scratch.Log, "start");
        var contribution = scratch.Contribution();
        contribution.Start();
        Append(scratch.Log, SearchLines);
        contribution.Poll(Now);
        Check.That(contribution.Found.TryDequeue(out _), "the price still updates locally");
        contribution.Flush(() => true, Now);
        Check.Equal(0, scratch.Stub.Bodies.Count);
    }

    [Test]
    public void SearchKeepsTheSeasonItWasMadeIn()
    {
        using var scratch = new Scratch();
        Write(scratch.Log, "start");
        var contribution = scratch.Contribution();
        contribution.Start();
        Append(scratch.Log, "+player+SeasonId [1601]", SearchLines, "+player+SeasonId [1]");
        contribution.Poll(Now);
        Check.Equal(1, contribution.Season ?? 0);
        contribution.Flush(() => true, Now);
        Check.Equal(1601, SeasonSent(scratch.Stub, 0));
    }

    [Test]
    public void PollTurnsSearchesIntoFoundAndQueued()
    {
        using var scratch = new Scratch();
        Write(scratch.Log, "+player+SeasonId [1601]", "old");
        var contribution = scratch.Contribution();
        contribution.Start();
        Check.Equal(1601, contribution.Season ?? 0);
        Append(scratch.Log, SearchLines);
        contribution.Poll(Now);
        Check.That(contribution.Found.TryDequeue(out var found), "search reaches the render side");
        Check.Equal(5028, found.BaseId);
        contribution.Flush(() => true, Now);
        Check.Equal(1, scratch.Stub.Bodies.Count);
    }

    [Test]
    public void OutboxKeepsTheNewest200()
    {
        using var scratch = new Scratch();
        var contribution = scratch.Contribution();
        for (var i = 1; i <= 205; i++) contribution.Queue(new Search(i, [1.0]), 1601);
        contribution.Flush(() => true, Now);
        Check.Equal(200, scratch.Stub.Bodies.Count);
        using var first = JsonDocument.Parse(scratch.Stub.Bodies[0]);
        Check.Equal(6, first.RootElement.GetProperty("p_config_base_id").GetInt32());
    }

    [Test]
    public void RateLimitPausesForAnHour()
    {
        using var scratch = new Scratch();
        var contribution = scratch.Contribution("""{"success": false, "error": "rate_limited", "rate_limited": true}""");
        contribution.Queue(Sample, 1601);
        contribution.Queue(Sample, 1601);
        contribution.Flush(() => true, Now);
        Check.Equal(1, scratch.Stub.Bodies.Count);
        contribution.Flush(() => true, Now.AddMinutes(59));
        Check.Equal(1, scratch.Stub.Bodies.Count);
        contribution.Flush(() => true, Now.AddMinutes(61));
        Check.Equal(2, scratch.Stub.Bodies.Count);
    }

    [Test]
    public void DeviceIdIsMadeOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prices-device-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Contribution.DeviceId(directory);
            Check.That(Guid.TryParse(first, out _), "a guid");
            Check.Equal(first, Contribution.DeviceId(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void FoundSearchUpdatesTheMedianInANewTable()
    {
        var old = new Dictionary<int, Price> { [5028] = new(9, 8, 10), [1] = new(3, null, null) };
        var found = new ConcurrentQueue<Search>();
        found.Enqueue(Sample);
        found.Enqueue(new Search(77, [4.0]));
        var merged = Main.Merged(old, found);
        Check.That(!ReferenceEquals(old, merged), "a new table");
        Check.Equal(9f, old[5028].Median);
        Check.Equal(new Price(1.5f, 8, 10), merged[5028]);
        Check.Equal(new Price(4f, null, null), merged[77]);
        Check.Equal(3f, merged[1].Median);
        Check.That(found.IsEmpty, "queue drained");
    }
}
