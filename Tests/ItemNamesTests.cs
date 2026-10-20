using System.Collections.Generic;
using EmberHUD;
using EmberHUD.Testing;

namespace Prices;

public sealed class ItemNamesTests
{
    static void Use(Dictionary<string, string> table)
    {
        ItemNames.Forget();
        TestGame.UseLoc(key => table.GetValueOrDefault(key), null);
    }

    [Test]
    public void ItemBaseWinsThenItemGold()
    {
        Use(new() { ["item_base|name|100300"] = "Flame Elementium", ["item_base|name|1"] = "Base", ["item_gold|name|1"] = "Gold", ["item_gold|name|2"] = "Boots" });
        Check.Equal("Base", ItemNames.Of(1));
        Check.Equal("Boots", ItemNames.Of(2));
        Check.Equal("", ItemNames.Of(3));
        TestGame.UseLoc(null, null);
    }

    [Test]
    public void NoGameIsNotRememberedAsNoName()
    {
        var table = new Dictionary<string, string>();
        Use(table);
        Check.Equal("", ItemNames.Of(5));
        table["item_base|name|100300"] = "Flame Elementium";
        table["item_base|name|5"] = "Netherrealm resonance";
        Check.Equal("Netherrealm resonance", ItemNames.Of(5));
        TestGame.UseLoc(null, null);
    }

    [Test]
    public void RarityIsUniqueThenBaseThenUnknown()
    {
        TestGame.UseItems(id => id == 5080 ? new ItemInfo(5080, 2, 0, 0, 0, 0, 0, 0, false) : null, id => id == 112258 ? new ItemInfo(3610, 1, 0, 0, 0, 0, 0, 0, true) : null);
        Check.Equal(2, ItemNames.Rarity(5080));
        Check.Equal(100, ItemNames.Rarity(112258));
        Check.Equal(0, ItemNames.Rarity(1));
        TestGame.UseItems(null, null);
        Check.Equal(0, ItemNames.Rarity(5080));
    }
}
