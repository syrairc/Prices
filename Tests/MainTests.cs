using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class MainTests
{
    static readonly GameUi Open = new([new GamePanel("Bag", PanelKind.Inventory, default, new Vector2(900, 600), false)]);

    static BagView Bag(int cells)
    {
        var views = new BagCell[cells];
        for (var i = 0; i < cells; i++) views[i] = new BagCell(i, new Vector2(10 + i * 100, 10), new Vector2(104 + i * 100, 145));
        return new BagView(InventoryBag.Commodities, new Vector2(0, 0), new Vector2(900, 600), views);
    }

    static InventoryItem Item(int slot, int baseId) => new(InventoryBag.Commodities, slot, "id", baseId, 0, 0, 0, 5, 100, 0);

    static Main Plugin() => new() { Prices = new Dictionary<int, Price> { [1] = new(8, null, null) } };

    static int Vertices(Main plugin, GameState state)
    {
        TestGame.Set(state);
        return TestFrame.Vertices(plugin.Render);
    }

    static GameState State(int cells, params InventoryItem[] items) => GameState.Empty with { Ui = Open, Bag = Bag(cells), Inventory = items };

    [Test]
    public void DrawsOneTagPerPricedVisibleCell()
    {
        var plugin = Plugin();
        var one = Vertices(plugin, State(3, Item(0, 1), Item(2, 99)));
        var two = Vertices(plugin, State(3, Item(0, 1), Item(1, 1), Item(2, 99)));
        Check.That(one > 0, "one priced item draws a tag");
        Check.Equal(one * 2, two);
    }

    [Test]
    public void NoInventoryDrawsNothing()
    {
        Check.Equal(0, Vertices(Plugin(), State(3) with { Inventory = null }));
    }

    [Test]
    public void ClosedBagDrawsNothing()
    {
        var plugin = Plugin();
        Check.Equal(0, Vertices(plugin, State(3, Item(0, 1)) with { Ui = GameUi.Empty }));
        plugin.Settings.Enabled = false;
        Check.Equal(0, Vertices(plugin, State(3, Item(0, 1))));
    }

    [Test]
    public void AsksForTheInventoryOnlyWhileTheBagIsOpen()
    {
        var asked = typeof(Game).GetProperty("InventoryAsked", BindingFlags.NonPublic | BindingFlags.Static)!;
        asked.SetValue(null, 0L);
        Vertices(Plugin(), State(3, Item(0, 1)) with { Ui = GameUi.Empty });
        Check.Equal(0L, (long)asked.GetValue(null)!);
        Vertices(Plugin(), State(3, Item(0, 1)));
        Check.That((long)asked.GetValue(null)! != 0, "asked while open");
        asked.SetValue(null, 0L);
    }

    [Test]
    public void SettingsPageDraws()
    {
        Check.That(TestFrame.Vertices(new Settings().Draw, inWindow: true) > 0, "settings page draws");
    }
}
