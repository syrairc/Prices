using System;
using System.Collections.Generic;
using System.Linq;
using EmberHUD;

namespace Prices;

public sealed record Run(DateTimeOffset Start, DateTimeOffset End, string AreaName, float StartTotal, float EndTotal, bool Partial, bool Unpriced, Line[] Loot, Line[] Costs)
{
    public float Delta => EndTotal - StartTotal;
}

public sealed class RunTracker
{
    static readonly TimeSpan MinRun = TimeSpan.FromSeconds(9);

    Snapshot? last;

    public Snapshot? Open { get; private set; }
    public Snapshot? Before { get; private set; }

    public Run? OnSnapshot(Snapshot s, IReadOnlyDictionary<int, Price> prices)
    {
        Run? closed = null;
        if (Open is { } open && (s.InTown || s.AreaId != open.AreaId || s.AreaName != open.AreaName))
        {
            closed = Close(open, s, prices);
            Open = null;
        }
        if (s.InTown) Before = s;
        else if (Open is null)
        {
            Before = last;
            Open = s;
        }
        last = s;
        return closed;
    }

    public void Reset()
    {
        Open = null;
        Before = null;
        last = null;
    }

    Run? Close(Snapshot open, Snapshot end, IReadOnlyDictionary<int, Price> prices)
    {
        if (end.At - open.At < MinRun) return null;
        var partial = Before is null;
        var costs = Before is { InTown: true } before
            ? Valuation.Diff(before.Lines, open.Lines, prices).Where(line => line.Count < 0).ToArray()
            : [];
        return new Run(open.At, end.At, open.AreaName, open.Total, end.Total, partial, end.Unpriced > 0, Valuation.Diff(open.Lines, end.Lines, prices), costs);
    }
}
