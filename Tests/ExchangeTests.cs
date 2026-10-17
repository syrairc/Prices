using System;
using EmberHUD.Testing;

namespace Prices;

public sealed class ExchangeTests
{
    static readonly DateTime Now = new(2026, 10, 17, 16, 0, 0);

    static Search? Feed(ExchangeParser parser, DateTime now, params string[] lines)
    {
        Search? last = null;
        foreach (var line in lines) last = parser.Feed(line, now) ?? last;
        return last;
    }

    static string[] Request(int synId, int refer) =>
    [
        $"[2026.01.26-19.35.20:148][572]GameLog: Display: [Game] ----Socket SendMessage STT----XchgSearchPrice----SynId = {synId}",
        "[2026.01.26-19.35.20:148][572]GameLog: Display: [Game] ",
        "+filters+1+key [4]",
        $"|       | +refer [{refer}]",
        "+typ3 [63]",
        "[2026.01.26-19.35.20:148][572]GameLog: Display: [Game] ----Socket SendMessage End----",
    ];

    static Search Searched(int synId, params string[] response)
    {
        var parser = new ExchangeParser();
        Check.That(Feed(parser, Now, Request(synId, 5028)) is null, "a request alone is no search");
        return Feed(parser, Now, response) ?? throw new InvalidOperationException("no search parsed");
    }

    static void Prices(Search search, params double[] expected)
    {
        Check.Equal(5028, search.BaseId);
        Check.Equal(expected.Length, search.PricesFe.Length);
        for (var i = 0; i < expected.Length; i++) Check.Equal(expected[i], search.PricesFe[i]);
    }

    [Test]
    public void ParsesSearchRequest()
    {
        var parser = new ExchangeParser();
        Feed(parser, Now, Request(23444, 5028));
        var search = Feed(parser, Now, "----Socket RecvMessage STT----XchgSearchPrice----SynId = 23444", "+prices+1+currency [100300]", "|      | +unitPrices+1 [2.0]", "----Socket RecvMessage End----");
        Check.Equal(5028, search?.BaseId ?? 0);
    }

    [Test]
    public void ParsesResponseNewFormat()
    {
        Prices(Searched(13366,
            "[2026.01.28-09.58.21:887][818]GameLog: Display: [Game] ----Socket RecvMessage STT----XchgSearchPrice----SynId = 13366",
            "[2026.01.28-09.58.21:887][818]GameLog: Display: [Game] ",
            "+errCode",
            "+prices+1+unitPrices+1 [15.0]",
            "|      | |          +2 [15.5]",
            "|      | |          +3 [15.6]",
            "|      | +currency [100300]",
            "[2026.01.28-09.58.21:887][818]GameLog: Display: [Game] ----Socket RecvMessage End----"), 15.0, 15.5, 15.6);
    }

    [Test]
    public void ParsesResponseOldFormat()
    {
        Prices(Searched(23444,
            "[2026.01.26-19.35.20:391][607]GameLog: Display: [Game] ----Socket RecvMessage STT----XchgSearchPrice----SynId = 23444",
            "[2026.01.26-19.35.20:391][607]GameLog: Display: [Game] ",
            "+errCode",
            "+prices+1+currency [100300]",
            "|      | +unitPrices+1 [0.02]",
            "|      | |          +2 [0.021]",
            "|      | |          +3 [0.022]",
            "|      +2+currency [100200]",
            "|      | +unitPrices+1 [100.0]",
            "[2026.01.26-19.35.20:391][607]GameLog: Display: [Game] ----Socket RecvMessage End----"), 0.02, 0.021, 0.022);
    }

    [Test]
    public void IgnoresNonFeCurrency()
    {
        var parser = new ExchangeParser();
        Feed(parser, Now, Request(100, 5028));
        var search = Feed(parser, Now, "----Socket RecvMessage STT----XchgSearchPrice----SynId = 100", "+errCode", "+prices+1+unitPrices+1 [100.0]", "|      | +currency [100200]", "----Socket RecvMessage End----");
        Check.That(search is null, "no FE prices, no search");
    }

    [Test]
    public void ParsesMessagesInSequence()
    {
        var parser = new ExchangeParser();
        Feed(parser, Now, "----Socket SendMessage STT----XchgSearchPrice----SynId = 1", "+filters+1+key [4]", "|       | +refer [1001]", "----Socket SendMessage End----");
        Feed(parser, Now, "----Socket SendMessage STT----XchgSearchPrice----SynId = 2", "+filters+1+key [4]", "|       | +refer [2002]", "----Socket SendMessage End----");
        string[] Response(int synId) => [$"----Socket RecvMessage STT----XchgSearchPrice----SynId = {synId}", "+prices+1+currency [100300]", "|      | +unitPrices+1 [3.0]", "----Socket RecvMessage End----"];
        Check.Equal(2002, Feed(parser, Now, Response(2))?.BaseId ?? 0);
        Check.Equal(1001, Feed(parser, Now, Response(1))?.BaseId ?? 0);
    }

    [Test]
    public void PricesAreSortedAscending()
    {
        Prices(Searched(7, "----Socket RecvMessage STT----XchgSearchPrice----SynId = 7", "+prices+1+currency [100300]", "|      | +unitPrices+1 [3.0]", "|      | |          +2 [1.0]", "|      | |          +3 [2.0]", "----Socket RecvMessage End----"), 1.0, 2.0, 3.0);
    }

    [Test]
    public void RequestWithoutReferIsIgnored()
    {
        var parser = new ExchangeParser();
        Feed(parser, Now, "----Socket SendMessage STT----XchgSearchPrice----SynId = 9", "+filters+1+key [4]", "----Socket SendMessage End----");
        Check.That(Feed(parser, Now, "----Socket RecvMessage STT----XchgSearchPrice----SynId = 9", "+prices+1+currency [100300]", "|      | +unitPrices+1 [3.0]", "----Socket RecvMessage End----") is null, "no refer, no search");
    }

    [Test]
    public void ResponseWithoutRequestIsIgnored()
    {
        Check.That(Feed(new ExchangeParser(), Now, "----Socket RecvMessage STT----XchgSearchPrice----SynId = 5", "+prices+1+currency [100300]", "|      | +unitPrices+1 [3.0]", "----Socket RecvMessage End----") is null, "unpaired response");
    }

    [Test]
    public void PendingRequestExpiresAfterFiveMinutes()
    {
        var parser = new ExchangeParser();
        Feed(parser, Now, Request(6, 5028));
        Check.That(Feed(parser, Now.AddMinutes(5.1), "----Socket RecvMessage STT----XchgSearchPrice----SynId = 6", "+prices+1+currency [100300]", "|      | +unitPrices+1 [3.0]", "----Socket RecvMessage End----") is null, "stale request");
    }

    [Test]
    public void ReferencePriceIsTitrackP10()
    {
        double[] Range(int n)
        {
            var values = new double[n];
            for (var i = 0; i < n; i++) values[i] = i + 1;
            return values;
        }
        Check.Equal(1.0, Exchange.ReferencePrice(Range(10)));
        Check.Equal(2.0, Exchange.ReferencePrice(Range(20)));
        Check.Equal(10.0, Exchange.ReferencePrice(Range(100)));
        Check.Equal(5.0, Exchange.ReferencePrice([5.0]));
        Check.Equal(0.0, Exchange.ReferencePrice([]));
        Check.Equal(0.02002002002002, Exchange.ReferencePrice([0.02002002002002, 0.02002002002002, 0.02049622437972, 0.021021021021021, 0.021021021021021, 0.021021021021021, 0.021021021021021, 0.021021021021021, 0.021021021021021, 0.021021021021021]));
    }

    [Test]
    public void SeasonOfReadsBothForms()
    {
        Check.Equal(1601, Exchange.SeasonOf("+player+SeasonId [1601]") ?? 0);
        Check.Equal(1601, Exchange.SeasonOf("|      +SeasonId [1601]") ?? 0);
        Check.That(Exchange.SeasonOf("|       +HasSeasonId [false]") is null, "HasSeasonId");
        Check.That(Exchange.SeasonOf("|      |      +SeasonId [1601]") is null, "nested");
        Check.That(Exchange.SeasonOf("some other line") is null, "other text");
    }

    [Test]
    public void LogPathFromExe()
    {
        Check.Equal(@"C:\G\Game\UE_game\Torchlight\Saved\Logs\UE_game.log", Exchange.LogPath(@"C:\G\Game\UE_game\Binaries\Win64\torchlight_infinite.exe"));
    }
}
