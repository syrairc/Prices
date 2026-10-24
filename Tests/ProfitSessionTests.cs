using System;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class ProfitSessionTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    static Run MakeRun(double startSeconds, double seconds, float value, Line[]? costs = null, bool unpriced = false) =>
        new(T0.AddSeconds(startSeconds), T0.AddSeconds(startSeconds + seconds), "Map A", 0, value, false, unpriced, [new Line(1, 1, value, value)], costs ?? []);

    static Snapshot Last(float total = 100, float fe = 5) => new(T0, 1, "Town", true, total, fe, 0, []);

    static ProfitSession Make(double nowSeconds = 0) => new(() => T0.AddSeconds(nowSeconds));

    [Test]
    public void SignedAndPlainText()
    {
        Check.Equal("+74.9", ProfitText.Signed(74.9f, false));
        Check.Equal("-3.0", ProfitText.Signed(-3f, false));
        Check.Equal("-", ProfitText.Signed(null, false));
        Check.Equal("+10*", ProfitText.Signed(10f, true));
        Check.Equal("0", ProfitText.Signed(0.01f, false));
        Check.Equal("0*", ProfitText.Signed(0.01f, true));
        Check.Equal("8.4k", ProfitText.Plain(8412f));
        Check.Equal("0", ProfitText.Plain(0f));
    }

    [Test]
    public void EmptySessionHasNoRates()
    {
        var view = Make().View(null, Last(), [])!;
        Check.Equal(0, view.Runs);
        Check.Equal(TimeSpan.Zero, view.Time);
        Check.That(view.PerMap is null && view.PerHour is null && view.LastMap is null && view.Profit is null, "all null");
    }

    [Test]
    public void NoSnapshotNoView()
    {
        Check.That(Make().View(null, null, []) is null, "null");
    }

    [Test]
    public void RunsSumIntoProfitAndRates()
    {
        var now = T0;
        var session = new ProfitSession(() => now);
        session.Add(MakeRun(0, 60, 60));
        session.Add(MakeRun(100, 60, 120));
        now = T0.AddSeconds(200);
        var view = session.View(null, Last(), [])!;
        Check.Equal(2, view.Runs);
        Check.Equal(180f, view.Profit!.Value);
        Check.Equal(90f, view.PerMap!.Value);
        Check.Equal(5400f, view.PerHour!.Value);
        Check.Equal(120f, view.LastMap!.Value);
    }

    [Test]
    public void MapCostsSubtract()
    {
        var session = Make();
        session.Add(MakeRun(0, 60, 60, [new Line(2, -1, 10, -10)]));
        Check.Equal(50f, session.View(null, Last(), [])!.LastMap!.Value);
    }

    [Test]
    public void ThisMapIsGrossLootOfTheOpenRun()
    {
        var session = Make();
        Check.That(session.View(null, Last(), [])!.ThisMap is null, "no open run");
        var open = MakeRun(0, 30, 40, [new Line(2, -1, 10, -10)]);
        Check.Equal(40f, session.View(open, Last(), [])!.ThisMap!.Value);
        Check.That(Make(0).View(MakeRun(-10, 5, 40), Last(), [])!.ThisMap is null, "open run from before the session");
        Check.That(!session.View(open, Last(), [])!.ThisMapUnpriced, "priced open run");
        Check.That(session.View(MakeRun(0, 30, 40, unpriced: true), Last(), [])!.ThisMapUnpriced, "unpriced open run");
    }

    [Test]
    public void RunFromBeforeTheSessionIsIgnoredAndRestartDropsRuns()
    {
        var session = Make();
        session.Add(MakeRun(-10, 5, 40));
        Check.Equal(0, session.View(null, Last(), [])!.Runs);
        session.Add(MakeRun(0, 5, 40));
        Check.Equal(1, session.View(null, Last(), [])!.Runs);
        session.Restart();
        Check.Equal(0, session.View(null, Last(), [])!.Runs);
    }

    [Test]
    public void UnpricedFlagsTheSession()
    {
        var session = Make();
        session.Add(MakeRun(0, 5, 10));
        Check.That(!session.View(null, Last(), [])!.Unpriced, "clean");
        session.Add(MakeRun(10, 5, 10, unpriced: true));
        Check.That(session.View(null, Last(), [])!.Unpriced, "flagged");
    }

    [Test]
    public void WealthAddsStashTabs()
    {
        var tabs = new[] { new StashTab(1, T0, [new Line(1, 1, 10, 10), new Line(2, 1, 5, 5)]), new StashTab(2, T0, [new Line(3, 1, 7, 7)]) };
        var view = Make().View(null, Last(100, 8), tabs)!;
        Check.Equal(122f, view.Wealth);
        Check.Equal(8f, view.Fe);
    }
}
