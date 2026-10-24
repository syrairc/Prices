using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EmberHUD;
using EmberHUD.Testing;
using Hexa.NET.ImGui;

namespace Prices;

public sealed class CompareDrawTests
{
    const uint Green = 0x6BD65C;
    const uint Red = 0x4F57E5;
    static readonly Vector2 CardMin = new(100, 100);
    static readonly Vector2 CardMax = new(300, 160);
    static readonly Vector2 OnCard = new(150, 120);
    static readonly Vector2 OffCard = new(600, 600);

    static GameState State(params CompareRow[] rows) => GameState.Empty with
    {
        Trade = new TradePage(1, false, 8, [new TradeListing(0, "Sword", 1, 10, null, CardMin, CardMax)], ""),
        Compare = rows.Length == 0 ? null : new EquipCompare(rows),
    };

    static Main Plugin(Vector2 mouse)
    {
        var plugin = new Main { Cursor = () => mouse };
        plugin.Settings.ProfitFull = false;
        return plugin;
    }

    static List<(Vector2 Pos, uint Rgb)> Render(Main plugin, GameState state, long now)
    {
        TestGame.Set(state);
        plugin.Clock = () => now;
        var found = new List<(Vector2, uint)>();
        TestFrame.Run(() =>
        {
            var list = ImGui.GetBackgroundDrawList();
            var before = list.VtxBuffer.Size;
            plugin.Render();
            for (var i = before; i < list.VtxBuffer.Size; i++) found.Add((list.VtxBuffer[i].Pos, list.VtxBuffer[i].Col & 0xFFFFFF));
        });
        return found;
    }

    static List<(Vector2 Pos, uint Rgb)> Settled(Main plugin, GameState state)
    {
        Render(plugin, state, 0);
        Render(plugin, state, CompareCache.DefaultSettleMs);
        return Render(plugin, state, CompareCache.DefaultSettleMs + 16);
    }

    static Vector2[] Of(List<(Vector2 Pos, uint Rgb)> vertices, uint rgb, bool left) =>
        [.. vertices.Where(v => v.Rgb == rgb && (v.Pos.X < 200) == left).Select(v => v.Pos)];

    [Test]
    public void LabelFormat()
    {
        Check.Equal("+16.4%", Main.CompareLabel(16.4f));
        Check.Equal("-4.4%", Main.CompareLabel(-4.4f));
        Check.Equal("0%", Main.CompareLabel(0));
        Check.Equal("+3.0%", Main.CompareLabel(3));
        Check.Equal("+999%", Main.CompareLabel(999));
        Check.Equal("0%", Main.CompareLabel(-0.04f));
        Check.Equal("0%", Main.CompareLabel(0.04f));
        Check.Equal("-0.1%", Main.CompareLabel(-0.1f));
    }

    [Test]
    public void SecondRowIsExactlyOneLineHeightBelowAndTheLastSitsOnTheCorner()
    {
        var one = Settled(Plugin(OnCard), State(new CompareRow(5, null)));
        var two = Settled(Plugin(OnCard), State(new CompareRow(5, null), new CompareRow(-2, null)));
        var height = 0f;
        TestFrame.Run(() => height = Draw.MeasureText("+5.0%", new Settings().CompareText).Y);
        Check.That(height > 0, "measured");
        var single = Of(one, Green, left: true).Min(at => at.Y);
        var first = Of(two, Green, left: true).Min(at => at.Y);
        var second = Of(two, Red, left: true).Min(at => at.Y);
        Check.That(System.Math.Abs(second - first - height) < 0.01f, $"step {second - first} vs line height {height}");
        Check.That(System.Math.Abs(second - single) < 0.01f, "last row where a single row sits");
    }

    [Test]
    public void TopCornerFirstRowSitsOnTheCornerAndTheRestBelow()
    {
        var one = new Settings();
        var plugin = Plugin(OnCard);
        plugin.Settings.CompareDamageCorner = Corner.TopLeft;
        var single = Settled(plugin, State(new CompareRow(5, null)));
        var two = Settled(plugin, State(new CompareRow(5, null), new CompareRow(-2, null)));
        var height = 0f;
        TestFrame.Run(() => height = Draw.MeasureText("+5.0%", one.CompareText).Y);
        var first = Of(two, Green, left: true).Min(at => at.Y);
        var second = Of(two, Red, left: true).Min(at => at.Y);
        Check.That(System.Math.Abs(first - Of(single, Green, left: true).Min(at => at.Y)) < 0.01f, "first row on the corner");
        Check.That(System.Math.Abs(second - first - height) < 0.01f, "second row one line below");
    }

    static GameState WithTooltip(Vector2 min, Vector2 max) => State(new CompareRow(5, -3)) with { Compare = new EquipCompare([new CompareRow(5, -3)]) { Min = min, Max = max } };

    [Test]
    public void NumbersOverlappingTheTooltipAreSkippedPerNumber()
    {
        var drawn = Settled(Plugin(OnCard), WithTooltip(new Vector2(250, 100), new Vector2(400, 160)));
        Check.That(Of(drawn, Green, left: true).Length > 0, "damage outside the tooltip still draws");
        Check.Equal(0, Of(drawn, Red, left: false).Length);
    }

    [Test]
    public void NumbersOutsideTheTooltipDraw()
    {
        var drawn = Settled(Plugin(OnCard), WithTooltip(new Vector2(500, 500), new Vector2(600, 600)));
        Check.That(Of(drawn, Green, left: true).Length > 0 && Of(drawn, Red, left: false).Length > 0, "both drawn");
    }

    [Test]
    public void UnknownTooltipRectSkipsNothing()
    {
        var drawn = Settled(Plugin(OnCard), State(new CompareRow(5, -3)));
        Check.That(Of(drawn, Green, left: true).Length > 0 && Of(drawn, Red, left: false).Length > 0, "both drawn");
    }

    [Test]
    public void RectHelper()
    {
        var tip = new EquipCompare([]) { Min = new Vector2(10, 10), Max = new Vector2(20, 20) };
        Check.That(CompareRects.Hit(tip, new Vector2(15, 15), new Vector2(30, 30)), "overlap");
        Check.That(!CompareRects.Hit(tip, new Vector2(20, 10), new Vector2(30, 20)), "touching edge");
        Check.That(!CompareRects.Hit(tip, new Vector2(30, 30), new Vector2(40, 40)), "apart");
        Check.That(!CompareRects.Hit(null, new Vector2(15, 15), new Vector2(30, 30)), "null");
        Check.That(!CompareRects.Hit(new EquipCompare([]), new Vector2(-5, -5), new Vector2(5, 5)), "unknown");
    }

    [Test]
    public void EachSideMovesByItsOwnOffset()
    {
        var state = State(new CompareRow(5, 5));
        var before = Settled(Plugin(OnCard), state);
        var plugin = Plugin(OnCard);
        (plugin.Settings.CompareDamageX, plugin.Settings.CompareDamageY, plugin.Settings.CompareSurvivalX, plugin.Settings.CompareSurvivalY) = (7, -3, -5, 4);
        var after = Settled(plugin, state);
        static Vector2 Corner(List<(Vector2 Pos, uint Rgb)> v, bool left) => new(Of(v, Green, left).Min(at => at.X), Of(v, Green, left).Min(at => at.Y));
        Check.Equal(new Vector2(7, -3), Corner(after, true) - Corner(before, true));
        Check.Equal(new Vector2(-5, 4), Corner(after, false) - Corner(before, false));
    }

    [Test]
    public void SettingsDefaults()
    {
        var settings = new Settings();
        Check.That(settings.CompareEnabled, "enabled");
        Check.Equal(Corner.BottomLeft, settings.CompareDamageCorner);
        Check.Equal(Corner.BottomRight, settings.CompareSurvivalCorner);
        Check.Equal(0, settings.CompareDamageX + settings.CompareDamageY + settings.CompareSurvivalX + settings.CompareSurvivalY);
        Check.That(settings.CompareText.ShadowEnabled, "shadow");
        Check.Equal(50, settings.CompareHoverMs);
    }

    [Test]
    public void SettingsTabDraws()
    {
        Check.That(TestFrame.Vertices(new Settings().DrawCompare, inWindow: true) > 0, "compare tab draws");
    }

    [Test]
    public void DamageBottomLeftSurvivalBottomRightInsideTheCard()
    {
        var drawn = Settled(Plugin(OnCard), State(new CompareRow(5, -3)));
        var damage = Of(drawn, Green, left: true);
        var survival = Of(drawn, Red, left: false);
        Check.That(damage.Length > 0 && survival.Length > 0, "both drawn");
        Check.That(Of(drawn, Green, left: false).Length == 0 && Of(drawn, Red, left: true).Length == 0, "each on its own side");
        foreach (var at in damage.Concat(survival)) Check.That(at.X >= CardMin.X && at.X <= CardMax.X && at.Y >= CardMin.Y && at.Y <= CardMax.Y, "inside the card");
        Check.That(damage.Min(at => at.Y) > (CardMin.Y + CardMax.Y) / 2, "damage low on the card");
    }

    [Test]
    public void BottomCornersReadTopToBottomInRowOrder()
    {
        var drawn = Settled(Plugin(OnCard), State(new CompareRow(5, -3), new CompareRow(-2, 4)));
        Check.That(Of(drawn, Green, left: true).Max(at => at.Y) <= Of(drawn, Red, left: true).Min(at => at.Y) + 1, "damage: first row (green) above the second (red)");
        Check.That(Of(drawn, Red, left: false).Max(at => at.Y) <= Of(drawn, Green, left: false).Min(at => at.Y) + 1, "survival: first row (red) above the second (green)");
    }

    [Test]
    public void TopCornersStackDownwards()
    {
        var plugin = Plugin(OnCard);
        plugin.Settings.CompareDamageCorner = Corner.TopLeft;
        var drawn = Settled(plugin, State(new CompareRow(5, null), new CompareRow(-2, null)));
        Check.That(Of(drawn, Green, left: true).Min(at => at.Y) < Of(drawn, Red, left: true).Min(at => at.Y), "second row below the first");
    }

    [Test]
    public void OneNumberDrawsOneText()
    {
        var drawn = Settled(Plugin(OnCard), State(new CompareRow(5, null)));
        Check.That(Of(drawn, Green, left: true).Length > 0, "damage");
        Check.Equal(0, drawn.Count(v => v.Pos.X >= 200));
    }

    [Test]
    public void DisabledDrawsAndCachesNothing()
    {
        var plugin = Plugin(OnCard);
        plugin.Settings.CompareEnabled = false;
        var state = State(new CompareRow(5, -3));
        Check.Equal(0, Settled(plugin, state).Count);
        plugin.Settings.CompareEnabled = true;
        Check.Equal(0, Render(plugin, state, CompareCache.DefaultSettleMs * 3).Count);
    }

    [Test]
    public void NeverHoveredDrawsNothing()
    {
        Check.Equal(0, Settled(Plugin(OffCard), State(new CompareRow(5, -3))).Count);
    }

    [Test]
    public void LeavingTheTradeHouseDropsTheCache()
    {
        var plugin = Plugin(OnCard);
        var state = State(new CompareRow(5, -3));
        Check.That(Settled(plugin, state).Count > 0, "drawn");
        Render(plugin, state with { Trade = null }, 1000);
        Check.Equal(0, Render(plugin, state with { Compare = null }, 1016).Count);
    }
}
