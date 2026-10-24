using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Prices;

public static class SampleWealth
{
    public const string Key = "Sample-1";
    public const int Fe = Valuation.FeBaseId;
    public static readonly DateTimeOffset Begin = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Now = Until(Begin);

    static DateTimeOffset Until(DateTimeOffset begin) => begin.AddMinutes(200 * 45 + 20);

    public static readonly Economy[] Economies =
    [
        new(false, false, false),
        new(true, false, false),
        new(true, true, false),
        new(true, true, true),
    ];

    static readonly string[] Names = ["", "Divine Orb", "Chaos Orb", "Compass of Echoes", "Beacon of Ruin", "Ember Core", "Frost Shard", "Void Idol", "Silver Coin", "Ancient Key", "Gilded Fang", "Glass Dial", "Mirror Shard"];
    static readonly string[] Maps = ["Ashen Hollow", "Drowned Gate", "Vault of Glass", "Cinder Reach", "Nightmare Marrow", "Pale Cistern"];

    static float Price(int baseId, int step)
    {
        if (baseId == Fe) return 1;
        var basePrice = 2 + baseId * baseId * 0.7f;
        return MathF.Round(basePrice * (1 + 0.25f * MathF.Sin(step / 40f + baseId)), 2);
    }

    static Line Make(int baseId, int count, int step) => new(baseId, count, Price(baseId, step), count * Price(baseId, step));

    static Snapshot Snap(DateTimeOffset at, bool town, string area, params Line[] lines) =>
        new(at, town ? 1 : 10 + Array.IndexOf(Maps, area), area, town, lines.Sum(l => l.Value), lines.Where(l => l.BaseId == Fe).Sum(l => l.Value), 0, lines);

    public static string Dashboard()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "--plugins");
        var plugins = at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1], AppContext.BaseDirectory) : Path.Combine(AppContext.BaseDirectory, "Plugins");
        return Path.Combine(plugins, "Prices", "dashboard");
    }

    public static ProfileData Profile() => Build(Begin).Data;

    public static WealthStore Build(DateTimeOffset begin)
    {
        var now = Until(begin);
        var rng = new Random(7);
        var store = new WealthStore();
        var names = Enumerable.Range(1, 12).ToDictionary(i => i, i => Names[i]);
        names[Fe] = "Flame Elementium";
        var fe = 5000;
        for (var i = 0; i < 3000; i++)
        {
            var step = i;
            fe += rng.Next(-5, 15);
            var bag = new[] { Make(Fe, fe, step) }.Concat(Enumerable.Range(1, 6).Select(id => Make(id, 1 + (id * 7 + i / 10) % 9, step))).ToArray();
            var tabs = i == 2999 ? Tabs(begin, step) : [];
            store.AddSnapshot(Snap(begin.AddMinutes(i * 3), i % 4 == 0, "Hideout", bag), tabs, names);
        }
        for (var k = 0; k < 200; k++)
        {
            var start = begin.AddMinutes(k * 45);
            var end = start.AddSeconds(180 + rng.Next(0, 300));
            var step = k * 15;
            var loot = new List<Line> { Make(Fe, rng.Next(k % 17 == 0 ? 0 : 20, 120), step) };
            foreach (var id in Enumerable.Range(1, 12).OrderBy(_ => rng.Next()).Take(2 + rng.Next(3))) loot.Add(Make(id, 1 + rng.Next(4), step));
            var costs = k % 3 == 0 ? new[] { Make(3, -1, step), Make(4, -1, step) } : [];
            var gross = loot.Sum(l => l.Value);
            var net = k % 29 == 5 ? -40f : gross;
            if (k % 29 == 5) loot = [Make(Fe, -40, step)];
            store.AddRun(new Run(start, end, Maps[k % Maps.Length], 1000 + k, 1000 + k + net, k == 0, k % 23 == 7, loot.ToArray(), costs));
        }
        var open = Snap(now.AddMinutes(-6), false, "Cinder Reach", Make(Fe, 8000, 3000), Make(1, 2, 3000), Make(3, 2, 3000), Make(4, 1, 3000));
        var current = Snap(now, false, "Cinder Reach", Make(Fe, 8131, 3000), Make(1, 2, 3000), Make(3, 1, 3000), Make(4, 1, 3000), Make(8, 5, 3000), Make(6, 2, 3000));
        store.AddSnapshot(current, [], names, Enumerable.Range(1, 12).ToDictionary(i => i, i => "UI_Item_Sample_" + i));
        store.SetOpen(open);
        store.SetLive(Snap(now, false, "Cinder Reach", Make(Fe, 8140, 3000), Make(1, 2, 3000), Make(3, 1, 3000), Make(4, 1, 3000), Make(8, 6, 3000), Make(6, 2, 3000), Make(9, 1, 3000)));
        return store;
    }

    static StashTab[] Tabs(DateTimeOffset begin, int step) =>
        Enumerable.Range(0, 4).Select(t => new StashTab(t, begin.AddMinutes(3000 * 3 - t * 90), Enumerable.Range(1, 12).Where(id => id % 4 == t).Select(id => Make(id, 3 + id, step)).ToArray())).ToArray();

    public static string Write(string directory, string dashboard, DateTimeOffset? begin = null)
    {
        Directory.CreateDirectory(directory);
        var store = Build(begin ?? DateTimeOffset.UtcNow.AddMinutes(-(200 * 45 + 25)));
        store.Save(directory, Key);
        foreach (var file in Directory.EnumerateFiles(dashboard, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(directory, Path.GetRelativePath(dashboard, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, true);
        }
        return directory;
    }
}
