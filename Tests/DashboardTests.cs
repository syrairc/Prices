using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmberHUD.Testing;

namespace Prices;

public sealed class DashboardTests
{
    static string Dashboard() => SampleWealth.Dashboard();

    static string Text(string name) => File.ReadAllText(Path.Combine(Dashboard(), name));

    static string[] Scripts() => Regex.Matches(Text("index.html"), "src=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();

    static string[] Own() => Scripts().Where(s => !s.StartsWith("wealth-") && !s.StartsWith("vendor/")).ToArray();

    [Test]
    public void IndexLoadsDataScriptsBeforeApp()
    {
        var scripts = Scripts();
        var app = Array.IndexOf(scripts, "app.js");
        Check.That(app > 0);
        Check.That(Array.IndexOf(scripts, "wealth-index.js") is var index and >= 0 && index < app);
        Check.That(Array.IndexOf(scripts, "wealth-icons.js") is var icons and >= 0 && icons < app);
        Check.That(Array.IndexOf(scripts, "vendor/htm.js") is var htm and >= 0 && htm < app);
        Check.That(Array.IndexOf(scripts, "vendor/react-dom.production.min.js") > Array.IndexOf(scripts, "vendor/react.production.min.js"));
    }

    [Test]
    public void NoExternalUrls()
    {
        foreach (var name in new[] { "index.html" }.Concat(Own()))
            Check.That(!Regex.IsMatch(Text(name), "https?://"), name);
    }

    [Test]
    public void FilesExistInPluginFolder()
    {
        foreach (var src in Scripts().Where(s => !s.StartsWith("wealth-")))
            Check.That(File.Exists(Path.Combine(Dashboard(), src)), src);
        Check.That(File.Exists(Path.Combine(Dashboard(), "vendor", "LICENSES.txt")));
    }

    [Test]
    public void StoreOutputHasTheKeysAppReads()
    {
        var json = JsonSerializer.Serialize(SampleWealth.Profile(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var js = string.Concat(Own().Select(Text));
        void Has(JsonElement element, params string[] keys)
        {
            foreach (var key in keys)
            {
                Check.That(element.TryGetProperty(key, out _), "json " + key);
                Check.That(Regex.IsMatch(js, @"\." + key + @"\b|\b" + key + @"\s*:"), "app " + key);
            }
        }
        Has(root, "runs", "bag", "priceHistory", "names", "open", "live", "icons");
        Has(root.GetProperty("runs")[0], "start", "end", "areaName", "partial", "unpriced", "loot", "costs");
        Has(root.GetProperty("runs")[0].GetProperty("loot")[0], "baseId", "count", "unit", "value");
        Has(root.GetProperty("bag"), "total");
        Has(root.GetProperty("bag"), "at", "inTown", "lines");
        Has(root.GetProperty("open"), "lines");
        Check.That(root.GetProperty("priceHistory").EnumerateObject().First().Value[0].GetArrayLength() == 2);
    }

    [Test]
    public void StatsJsHoldsTheRulings()
    {
        var js = Text("stats.js");
        Check.That(js.Contains("0.875"));
        Check.That(js.Contains("100300"));
        Check.That(js.Contains("module.exports"));
    }

    [Test]
    public void AppMathMatchesStats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prices-dash-" + Guid.NewGuid().ToString("N"));
        try
        {
            SampleWealth.Write(directory, Dashboard(), SampleWealth.Begin);
            var data = Path.Combine(directory, SampleWealth.Key + ".json");
            var script = "const m=require(process.argv[1]);const d=JSON.parse(require('fs').readFileSync(process.argv[2],'utf8'));" +
                "const now=Date.parse(process.argv[3]);const ecos=JSON.parse(process.argv[4]);" +
                "const lastEnd=Date.parse(d.runs[d.runs.length-1].end);" +
                "console.log(JSON.stringify({sum:ecos.map(e=>m.summarise(d.runs,e,now)),roll:ecos.map(e=>m.rolling(d.runs,e,10).pop().v)," +
                "diff:m.diffLines(d.open.lines,(d.live||d.bag).lines)}));";
            var economies = JsonSerializer.Serialize(SampleWealth.Economies.Select(e => new { tradeTax = e.TradeTax, mapCosts = e.MapCosts, wallClock = e.WallClock }));
            var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in new[] { "-e", script, Path.Combine(directory, "stats.js"), data, SampleWealth.Now.ToString("O"), economies }) info.ArgumentList.Add(arg);
            string output;
            var errors = new System.Text.StringBuilder();
            try
            {
                using var process = Process.Start(info)!;
                process.ErrorDataReceived += (_, e) => errors.AppendLine(e.Data);
                process.BeginErrorReadLine();
                output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Check.Equal(0, process.ExitCode, errors.ToString());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("AppMathMatchesStats skipped: node not found");
                return;
            }
            var result = JsonDocument.Parse(output).RootElement;
            var results = result.GetProperty("sum").EnumerateArray().ToArray();
            var rolling = result.GetProperty("roll").EnumerateArray().ToArray();
            var profile = SampleWealth.Profile();
            var runs = profile.Runs;
            var lastEnd = runs[^1].End;
            for (var i = 0; i < SampleWealth.Economies.Length; i++)
            {
                var expected = Stats.Summarise(runs, SampleWealth.Economies[i], SampleWealth.Now);
                var got = results[i];
                Near(expected.NetWorth, got.GetProperty("netWorth").GetDouble());
                Near(expected.FePerHour, got.GetProperty("fePerHour").GetDouble());
                Near(expected.FePerMap, got.GetProperty("fePerMap").GetDouble());
                Check.Equal(expected.Runs, got.GetProperty("runs").GetInt32());
                Near((float)expected.AverageRun.TotalSeconds, got.GetProperty("averageRunSec").GetDouble());
                Near((float)expected.TotalTime.TotalSeconds, got.GetProperty("totalSec").GetDouble());
                Near(Stats.Summarise(runs[^10..], SampleWealth.Economies[i], lastEnd).FePerHour, rolling[i].GetDouble());
            }
            var after = (profile.Live ?? profile.Bag)!.Lines;
            var prices = new Dictionary<int, Price>();
            foreach (var line in profile.Open!.Lines.Concat(after)) prices[line.BaseId] = new Price(line.Unit, null, null);
            var expectedDiff = Valuation.Diff(profile.Open.Lines, after, prices).OrderBy(l => l.BaseId).ToArray();
            var diff = result.GetProperty("diff").EnumerateArray().OrderBy(e => e.GetProperty("baseId").GetInt32()).ToArray();
            Check.Equal(expectedDiff.Length, diff.Length);
            for (var i = 0; i < diff.Length; i++)
            {
                Check.Equal(expectedDiff[i].BaseId, diff[i].GetProperty("baseId").GetInt32());
                Check.Equal(expectedDiff[i].Count, diff[i].GetProperty("count").GetInt32());
                Near(expectedDiff[i].Value, diff[i].GetProperty("value").GetDouble());
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    static void Near(float expected, double actual) =>
        Check.That(Math.Abs(expected - actual) <= Math.Max(0.01, Math.Abs(expected) * 1e-4), $"{expected} vs {actual}");

    [Test]
    public void SampleWritesPageAndData()
    {
        var directory = SampleWealth.Write(Path.Combine(Path.GetTempPath(), "ember-wealth-sample"), Dashboard());
        Check.That(File.Exists(Path.Combine(directory, "index.html")));
        Check.That(File.Exists(Path.Combine(directory, "wealth-Sample-1.js")));
        Check.That(File.ReadAllText(Path.Combine(directory, "wealth-index.js")).Contains("Sample-1"));
        var data = SampleWealth.Profile();
        Check.Equal(3000, data.Totals.Length);
        Check.Equal(200, data.Runs.Length);
        Check.Equal(4, data.Tabs.Length);
        Check.That(data.PriceHistory.Count > 0 && data.Open is not null);
    }

    [Test]
    public void TrackerStoresTheOpenRun()
    {
        var store = new WealthStore();
        var open = new Snapshot(SampleWealth.Now, 10, "Map", false, 0, 0, 0, []);
        store.SetOpen(open);
        Check.Equal(open, store.Data.Open);
        store.SetOpen(null);
        Check.That(store.Data.Open is null);
    }

    [Test]
    public void IconScriptMapsTheItemsIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prices-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check.That(Main.IconScript(directory).Contains("WEALTH_ICON_INDEX = {}"), "absent index");
            Directory.CreateDirectory(Path.Combine(directory, "Items"));
            File.WriteAllText(Path.Combine(directory, "Items", "index.json"), """{"icons":{"UI_Item_One":{"file":"UI_Item_One.png","pak":"x"}}}""");
            var script = Main.IconScript(directory);
            Check.That(script.Contains("\"UI_Item_One\":\"UI_Item_One.png\""));
            Check.That(script.Contains("WEALTH_ICON_DIR"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
