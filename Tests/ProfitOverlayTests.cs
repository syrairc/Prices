using System;
using EmberHUD.Testing;

namespace Prices;

public sealed class ProfitOverlayTests
{
    static readonly ProfitView Known = new(TimeSpan.FromMinutes(72), 9, 12.4f, -3f, 8.2f, 120f, 74.9f, true, 8412f, 5f);

    [Test]
    public void FullRowsForKnownView()
    {
        var rows = ProfitOverlay.FullRows(Known);
        Check.Equal("1h 12m, 9 runs", rows[0].Value);
        Check.That(rows[1].Divider && rows[7].Divider);
        Check.Equal("+12.4", rows[2].Value);
        Check.Equal(ProfitTone.Positive, rows[2].Tone);
        Check.Equal("-3.0*", rows[3].Value);
        Check.Equal(ProfitTone.Negative, rows[3].Tone);
        Check.Equal("8.4k", rows[8].Value);
        Check.Equal(ProfitTone.Plain, rows[8].Tone);
        Check.Equal("5.0", rows[9].Value);
    }

    [Test]
    public void NoViewShowsDashes()
    {
        foreach (var row in ProfitOverlay.FullRows(null))
            if (!row.Divider) Check.Equal("-", row.Value);
        foreach (var row in ProfitOverlay.CompactRows(null)) Check.Equal("-", row.Value);
    }

    [Test]
    public void CompactRowsAndShortSession()
    {
        var rows = ProfitOverlay.CompactRows(Known with { PerHour = null });
        Check.Equal("-", rows[0].Value);
        Check.Equal("+8.2*", rows[1].Value);
        Check.Equal("8.4k", rows[2].Value);
        Check.Equal("5m", ProfitOverlay.Duration(TimeSpan.FromMinutes(5)));
    }
}
