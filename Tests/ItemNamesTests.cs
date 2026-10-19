using System.Collections.Generic;
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
}
