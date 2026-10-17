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

    static readonly GameUi StashPage = new([new GamePanel("Stash", PanelKind.Stash, default, new Vector2(900, 600), false)]);

    static GameState StashState(int cells, params InventoryItem[] items)
    {
        var views = new BagCell[cells];
        for (var i = 0; i < cells; i++) views[i] = new BagCell(i, new Vector2(10 + i * 100, 10), new Vector2(104 + i * 100, 145));
        return GameState.Empty with { Ui = StashPage, Stash = new StashView(0, new Vector2(0, 0), new Vector2(900, 600), views), StashItems = items };
    }

    [Test]
    public void DrawsOneTagPerPricedStashCell()
    {
        var plugin = Plugin();
        var one = Vertices(plugin, StashState(3, Item(0, 1), Item(2, 99)));
        var two = Vertices(plugin, StashState(3, Item(0, 1), Item(1, 1), Item(2, 99)));
        Check.That(one > 0, "one priced item draws a tag");
        Check.Equal(one * 2, two);
    }

    [Test]
    public void StashTagsMatchBagTags()
    {
        var plugin = Plugin();
        Check.Equal(Vertices(plugin, State(3, Item(0, 1), Item(1, 1))), Vertices(plugin, StashState(3, Item(0, 1), Item(1, 1))));
    }

    [Test]
    public void GapCellDrawsNothing()
    {
        var plugin = Plugin();
        var one = Vertices(plugin, StashState(3, Item(0, 1)));
        Check.Equal(one * 2, Vertices(plugin, StashState(3, Item(0, 1), Item(2, 1))));
    }

    [Test]
    public void NoStashItemsDrawsNothing()
    {
        Check.Equal(0, Vertices(Plugin(), StashState(3) with { StashItems = null }));
    }

    [Test]
    public void StashClosedDrawsNothing()
    {
        Check.Equal(0, Vertices(Plugin(), StashState(3, Item(0, 1)) with { Ui = GameUi.Empty }));
    }

    [Test]
    public void StashAsksForTheInventory()
    {
        var plugin = Plugin();
        TestGame.Set(StashState(3, Item(0, 1)));
        Check.That(TestGame.InventoryAskedDuring(() => TestFrame.Vertices(plugin.Render)), "asked while the stash is open");
    }

    static GroundItem Ground(int rareValue, bool equipment = true, int baseId = 1) =>
        new(1, new Vector2(100, 200), new Vector2(300, 230), Vector3.Zero, true, [], []) { BaseId = baseId, RareValue = rareValue, Equipment = equipment };

    static GameState GroundState(params GroundItem[] items) => GameState.Empty with { GroundItems = items };

    [Test]
    public void GroundTagsOnlyLegendaryByDefault()
    {
        var plugin = Plugin();
        Check.Equal(0, Vertices(plugin, GroundState(Ground(1), Ground(2), Ground(3), Ground(4))));
        Check.That(Vertices(plugin, GroundState(Ground(100))) > 0, "legendary tagged");
    }

    [Test]
    public void GroundRarityChoicesApply()
    {
        var plugin = Plugin();
        plugin.Settings.GroundPurple = true;
        Check.That(Vertices(plugin, GroundState(Ground(3))) > 0, "purple on");
        plugin.Settings.GroundOrange = false;
        Check.Equal(0, Vertices(plugin, GroundState(Ground(100))));
    }

    [Test]
    public void GroundNonEquipmentIsOffByDefault()
    {
        var plugin = Plugin();
        Check.Equal(0, Vertices(plugin, GroundState(Ground(100, equipment: false))));
        plugin.Settings.GroundNonEquipment = true;
        Check.That(Vertices(plugin, GroundState(Ground(100, equipment: false))) > 0, "non-equipment on");
    }

    [Test]
    public void GroundUnpricedFullScreenAndDisabledDrawNothing()
    {
        var plugin = Plugin();
        Check.Equal(0, Vertices(plugin, GroundState(Ground(100, baseId: 99))));
        Check.Equal(0, Vertices(plugin, GroundState(Ground(100)) with { Ui = new GameUi([new GamePanel("Map", PanelKind.WorldMap, default, new Vector2(900, 600), true)]) }));
        Check.Equal(0, Vertices(plugin, GroundState()));
        plugin.Settings.GroundEnabled = false;
        Check.Equal(0, Vertices(plugin, GroundState(Ground(100))));
    }

    [Test]
    public void AsksForGroundItemsOnlyWhenEnabled()
    {
        var plugin = Plugin();
        Check.That(TestGame.GroundItemsAskedDuring(plugin.Tick), "enabled");
        plugin.Settings.GroundEnabled = false;
        Check.That(!TestGame.GroundItemsAskedDuring(plugin.Tick), "disabled");
    }

    [Test]
    public void SettingsPageDraws()
    {
        Check.That(TestFrame.Vertices(new Settings().Draw, inWindow: true) > 0, "settings page draws");
        Check.That(TestFrame.Vertices(new Settings().DrawGround, inWindow: true) > 0, "ground tab draws");
    }
}
