using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using EmberHUD.Testing;

namespace Prices;

public sealed class WealthStoreTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    static readonly IReadOnlyDictionary<int, string> NoNames = new Dictionary<int, string>();

    static Snapshot Snap(int minutes, float total, params Line[] lines) =>
        new(Now.AddMinutes(minutes), 1, "Town", true, total, 0, 0, lines);

    static Run Run(int minutes) =>
        new(Now.AddMinutes(minutes), Now.AddMinutes(minutes + 5), "Map", 10, 20, false, false, [], []);

    static StashTab Tab(int index, params Line[] lines) => new(index, Now, lines);

    static string Dir() => Path.Combine(Path.GetTempPath(), "prices-wealth-" + Guid.NewGuid().ToString("N"));

    [Test]
    public void SaveWritesJsonScriptAndIndex()
    {
        var directory = Dir();
        try
        {
            var store = new WealthStore();
            store.AddSnapshot(Snap(0, 5, new Line(1, 2, 2.5f, 5)), [Tab(0, new Line(2, 1, 4, 4))], new Dictionary<int, string> { [1] = "Orb" });
            store.AddRun(Run(0));
            store.Save(directory, "Hero-12");
            var loaded = JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(Path.Combine(directory, "Hero-12.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Check.Equal(1, loaded.Runs.Length);
            Check.Equal(1, loaded.Totals.Length);
            Check.Equal("Orb", loaded.Names[1]);
            var json = File.ReadAllText(Path.Combine(directory, "Hero-12.json"));
            var script = File.ReadAllText(Path.Combine(directory, "wealth-Hero-12.js"));
            const string Head = "(window.WEALTH = window.WEALTH || {})[\"Hero-12\"] = ";
            Check.That(script.StartsWith(Head));
            Check.That(script.EndsWith(';'));
            Check.Equal(json, script[Head.Length..^1]);
            var index = File.ReadAllText(Path.Combine(directory, "wealth-index.js"));
            Check.That(index.StartsWith("window.WEALTH_INDEX = ["));
            Check.That(index.Contains("\"key\":\"Hero-12\""));
            Check.That(index.Contains("\"character\":\"Hero\""));
            Check.That(index.Contains("\"season\":\"12\""));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void OnlyBagKeepsLines()
    {
        var store = new WealthStore();
        store.AddSnapshot(Snap(0, 5, new Line(1, 1, 5, 5)), [], NoNames);
        store.AddSnapshot(Snap(1, 9, new Line(2, 1, 9, 9)), [], NoNames);
        Check.Equal(2, store.Data.Totals.Length);
        Check.Equal(9f, store.Data.Totals[1].Total);
        Check.Equal(2, store.Data.Bag!.Lines.Single().BaseId);
    }

    [Test]
    public void TotalsSplitBagAndStash()
    {
        var store = new WealthStore();
        store.AddSnapshot(Snap(0, 5, new Line(1, 1, 5, 5)), [Tab(0, new Line(2, 1, 7, 7)), Tab(1, new Line(3, 1, 3, 3))], NoNames);
        var point = store.Data.Totals.Single();
        Check.Equal(5f, point.Bags);
        Check.Equal(10f, point.Stash);
        Check.Equal(15f, point.Total);
    }

    [Test]
    public void CapsDropOldest()
    {
        var store = new WealthStore();
        for (var i = 0; i < 3001; i++) store.AddSnapshot(Snap(i, i), [], NoNames);
        Check.Equal(3000, store.Data.Totals.Length);
        Check.Equal(1f, store.Data.Totals[0].Bags);
        for (var i = 0; i < 1001; i++) store.AddRun(Run(i));
        Check.Equal(1000, store.Data.Runs.Length);
        Check.Equal(Now.AddMinutes(1), store.Data.Runs[0].Start);
        var priced = new WealthStore();
        for (var i = 1; i <= 61; i++) priced.AddSnapshot(Snap(i, i, new Line(7, 1, i, i)), [], NoNames);
        var history = priced.Data.PriceHistory[7];
        Check.Equal(60, history.Length);
        Check.Equal(2f, history[0][1]);
        Check.Equal(61f, history[^1][1]);
    }

    [Test]
    public void UnchangedPriceIsNotStoredTwice()
    {
        var store = new WealthStore();
        store.AddSnapshot(Snap(0, 3, new Line(7, 1, 3, 3)), [], NoNames);
        store.AddSnapshot(Snap(1, 3, new Line(7, 1, 3, 3)), [Tab(0, new Line(7, 4, 3, 12))], NoNames);
        Check.Equal(1, store.Data.PriceHistory[7].Length);
        store.AddSnapshot(Snap(2, 4, new Line(7, 1, 4, 4)), [], NoNames);
        Check.Equal(2, store.Data.PriceHistory[7].Length);
        Check.Equal((float)Now.AddMinutes(2).ToUnixTimeSeconds(), store.Data.PriceHistory[7][1][0]);
    }

    [Test]
    public void SetTabReplacesSameIndexOnly()
    {
        var store = new WealthStore();
        store.SetTab(Tab(0, new Line(1, 1, 1, 1)));
        store.SetTab(Tab(1, new Line(2, 1, 1, 1)));
        store.SetTab(Tab(0, new Line(3, 1, 1, 1)));
        Check.Equal(2, store.Data.Tabs.Length);
        Check.Equal(3, store.Data.Tabs.Single(t => t.Index == 0).Lines.Single().BaseId);
        Check.Equal(2, store.Data.Tabs.Single(t => t.Index == 1).Lines.Single().BaseId);
    }

    [Test]
    public void ClearEmptiesEverything()
    {
        var store = new WealthStore();
        store.AddSnapshot(Snap(0, 5, new Line(1, 1, 5, 5)), [Tab(0, new Line(2, 1, 4, 4))], new Dictionary<int, string> { [1] = "Orb" });
        store.AddRun(Run(0));
        store.Clear();
        Check.Equal(0, store.Data.Runs.Length);
        Check.Equal(0, store.Data.Totals.Length);
        Check.Equal(0, store.Data.Tabs.Length);
        Check.Equal(0, store.Data.PriceHistory.Count);
        Check.Equal(0, store.Data.Names.Count);
        Check.That(store.Data.Bag is null);
    }

    [Test]
    public void BadFileIsRenamedAndStartsEmpty()
    {
        var directory = Dir();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Hero-12.json"), "{ not json");
            var store = WealthStore.Load(directory, "Hero-12");
            Check.Equal(0, store.Data.Runs.Length);
            Check.That(File.Exists(Path.Combine(directory, "Hero-12.json.bad")));
            Check.That(!File.Exists(Path.Combine(directory, "Hero-12.json")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void MissingFileStartsEmptyAndRoundTrips()
    {
        var directory = Dir();
        try
        {
            Check.Equal(0, WealthStore.Load(directory, "Hero-12").Data.Totals.Length);
            var store = new WealthStore();
            store.AddSnapshot(Snap(0, 5, new Line(1, 1, 5, 5)), [Tab(2, new Line(2, 1, 4, 4))], NoNames);
            store.Save(directory, "Hero-12");
            var loaded = WealthStore.Load(directory, "Hero-12");
            Check.Equal(1, loaded.Data.Totals.Length);
            Check.Equal(2, loaded.Data.Tabs.Single().Index);
            Check.Equal(5f, loaded.Data.PriceHistory[1][0][1]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void SaveLeavesNoTempFile()
    {
        var directory = Dir();
        try
        {
            new WealthStore().Save(directory, "Hero-12");
            Check.Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
            Check.Equal(3, Directory.GetFiles(directory).Length);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void UnsafeKeyCharactersAreReplaced()
    {
        var directory = Dir();
        try
        {
            new WealthStore().Save(directory, "a/b:c*d-12");
            Check.That(File.Exists(Path.Combine(directory, "a_b_c_d-12.json")));
            Check.That(File.Exists(Path.Combine(directory, "wealth-a_b_c_d-12.js")));
            Check.Equal(0, WealthStore.Load(directory, "a/b:c*d-12").Data.Runs.Length);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
