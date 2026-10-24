using System;
using System.Collections.Generic;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class StatsTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    static readonly Dictionary<int, Price> Prices = [];
    static readonly Economy Plain = new(false, false, false);

    static Run MakeRun(double startSeconds, double seconds, float value, Line[]? loot = null, Line[]? costs = null) =>
        new(T0.AddSeconds(startSeconds), T0.AddSeconds(startSeconds + seconds), "Map A", 0, value, false, false, loot ?? [new Line(1, 1, value, value)], costs ?? []);

    [Test]
    public void PerHourAndPerMapFromRuns()
    {
        var summary = Stats.Summarise([MakeRun(0, 60, 60), MakeRun(100, 60, 120)], Plain, T0);
        Check.Equal(2, summary.Runs);
        Check.Equal(180f, summary.NetWorth);
        Check.Equal(90f, summary.FePerMap);
        Check.Equal(5400f, summary.FePerHour);
        Check.Equal(TimeSpan.FromSeconds(60), summary.AverageRun);
        Check.Equal(TimeSpan.FromSeconds(120), summary.TotalTime);
    }

    [Test]
    public void TradeTaxCutsNonFeOnly()
    {
        var run = MakeRun(0, 60, 200, [new Line(Valuation.FeBaseId, 100, 1, 100), new Line(1, 10, 10, 100)]);
        Check.Equal(187.5f, Stats.RunValue(run, new Economy(true, false, false), Prices));
        Check.Equal(200f, Stats.RunValue(run, Plain, Prices));
    }

    [Test]
    public void MapCostsSubtractWhenOn()
    {
        var run = MakeRun(0, 60, 50, costs: [new Line(2, -2, 10, -20)]);
        Check.Equal(30f, Stats.RunValue(run, new Economy(false, true, false), Prices));
        Check.Equal(50f, Stats.RunValue(run, Plain, Prices));
    }

    [Test]
    public void WallClockCountsTownTime()
    {
        var runs = new[] { MakeRun(0, 60, 10), MakeRun(120, 60, 10) };
        Check.Equal(TimeSpan.FromSeconds(120), Stats.Summarise(runs, Plain, T0).TotalTime);
        Check.Equal(TimeSpan.FromSeconds(180), Stats.Summarise(runs, new Economy(false, false, true), T0).TotalTime);
    }

    [Test]
    public void NoRunsIsAllZero()
    {
        var summary = Stats.Summarise([], new Economy(true, true, true), T0);
        Check.Equal(0, summary.Runs);
        Check.Equal(0f, summary.NetWorth);
        Check.Equal(0f, summary.FePerHour);
        Check.Equal(0f, summary.FePerMap);
        Check.Equal(TimeSpan.Zero, summary.AverageRun);
        Check.Equal(TimeSpan.Zero, summary.TotalTime);
    }
}
