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
        Check.That(Array.IndexOf(scripts, "wealth.js") is var wealth and >= 0 && wealth < app && wealth > Array.IndexOf(scripts, "charts.js"));
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
            var expectedDiff = Valuation.Diff(profile.Open.Lines, after).OrderBy(l => l.BaseId).ToArray();
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

    [Test]
    public void WealthScreenReadsTabKeys()
    {
        var json = JsonSerializer.Serialize(SampleWealth.Profile(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var js = Text("wealth.js");
        void Has(JsonElement element, params string[] keys)
        {
            foreach (var key in keys)
            {
                Check.That(element.TryGetProperty(key, out _), "json " + key);
                Check.That(Regex.IsMatch(js, @"[\w\)\]]\." + key + @"\b"), "wealth.js reads ." + key);
            }
        }
        Has(root, "tabs", "bag", "totals");
        Has(root.GetProperty("tabs")[0], "index", "seenAt", "lines");
        Has(root.GetProperty("totals")[0], "at", "bags", "stash", "total");
        Check.That(js.Contains("window.WealthScreen"));
    }

    [Test]
    public void SampleHasStaleTab()
    {
        var data = SampleWealth.Profile();
        Check.Equal(4, data.Tabs.Length);
        var stale = data.Tabs.Where(t => SampleWealth.Now - t.SeenAt > TimeSpan.FromDays(1)).ToArray();
        Check.Equal(1, stale.Length);
        Check.Equal(SampleWealth.StaleTab, stale[0].Index);
        Check.Equal(SampleWealth.StaleSeenAt, stale[0].SeenAt);
        Check.That(data.Tabs.SelectMany(t => t.Lines).Any(l => l.BaseId == SampleWealth.Unpriced && l.Unit == 0));
        Check.That(data.Names.ContainsKey(SampleWealth.Unpriced));
    }

    [Test]
    public void WealthMathMatchesTheSample()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prices-dash-" + Guid.NewGuid().ToString("N"));
        try
        {
            SampleWealth.Write(directory, Dashboard(), SampleWealth.Begin);
            var later = SampleWealth.Now.AddDays(3);
            var script = "const m=require(process.argv[1]);const d=JSON.parse(require('fs').readFileSync(process.argv[2],'utf8'));" +
                "const now=Date.parse(process.argv[3]);const rows=m.buildRows(d);" +
                "const ex=new Set([rows[0].key]);const all=m.summarise(rows,new Set());const some=m.summarise(rows,ex);" +
                "console.log(JSON.stringify({rows:rows.length,keys:new Set(rows.map(r=>r.key)).size,all,some,first:rows[0].value," +
                "stale:d.tabs.filter(t=>m.isStale(t.seenAt,Date.parse(process.argv[4]))).map(t=>t.index),week:m.recent(d.totals,now).length," +
                "divine:m.findDivine(d),tabName:[m.tabName(m.BAGS),m.tabName(0),m.tabName(3)],ago:[m.ago(30000),m.ago(5*60000),m.ago(3*3600000),m.ago(50*3600000)]," +
                "slices:m.breakdown(rows,new Set(),3).map(s=>s.value)}));";
            var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in new[] { "-e", script, Path.Combine(directory, "wealth.js"), Path.Combine(directory, SampleWealth.Key + ".json"), later.ToString("O"), SampleWealth.Now.ToString("O") }) info.ArgumentList.Add(arg);
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
                Console.WriteLine("WealthMathMatchesTheSample skipped: node not found");
                return;
            }
            var result = JsonDocument.Parse(output).RootElement;
            var data = SampleWealth.Profile();
            var lines = data.Bag!.Lines.Concat(data.Tabs.SelectMany(t => t.Lines)).ToArray();
            Check.Equal(lines.Length, result.GetProperty("rows").GetInt32());
            Check.Equal(lines.Length, result.GetProperty("keys").GetInt32());
            Near(lines.Sum(l => l.Value), result.GetProperty("all").GetProperty("total").GetDouble());
            Check.Equal(lines.Count(l => l.Unit == 0 && l.BaseId != Valuation.FeBaseId), result.GetProperty("all").GetProperty("unpriced").GetInt32());
            Check.That(result.GetProperty("all").GetProperty("unpriced").GetInt32() >= 1);
            var first = result.GetProperty("first").GetDouble();
            Near((float)(result.GetProperty("all").GetProperty("total").GetDouble() - first), result.GetProperty("some").GetProperty("total").GetDouble());
            Check.Equal(lines.Length - 1, result.GetProperty("some").GetProperty("included").GetInt32());
            Check.Equal(SampleWealth.StaleTab, result.GetProperty("stale").EnumerateArray().Single().GetInt32());
            Check.Equal(data.Totals.Count(p => p.At >= later.AddDays(-7)), result.GetProperty("week").GetInt32());
            Check.That(result.GetProperty("week").GetInt32() is > 0 and < 3000);
            Near(data.Bag.Lines.First(l => l.BaseId == 1).Unit, result.GetProperty("divine").GetDouble());
            Check.Equal("Bags|Stash 1|Stash 4", string.Join('|', result.GetProperty("tabName").EnumerateArray().Select(e => e.GetString())));
            Check.Equal("just now|5m ago|3h ago|2d ago", string.Join('|', result.GetProperty("ago").EnumerateArray().Select(e => e.GetString())));
            var slices = result.GetProperty("slices").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            Check.Equal(4, slices.Length);
            Check.That(slices[0] >= slices[1] && slices[1] >= slices[2]);
            Near(lines.Sum(l => l.Value), slices.Sum());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void WealthLogicSortSearchDivineAndStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prices-dash-" + Guid.NewGuid().ToString("N"));
        try
        {
            SampleWealth.Write(directory, Dashboard(), SampleWealth.Begin);
            var script = "const m=require(process.argv[1]);const d=JSON.parse(require('fs').readFileSync(process.argv[2],'utf8'));" +
                "const nm=id=>d.names[id]||'#'+id;const rows=m.buildRows(d);" +
                "const by=(k,dir)=>m.sortRows(rows,k,dir,nm);" +
                "const tot=by('total',-1).map(r=>r.value);const qty=by('qty',1).map(r=>r.count);const names=by('name',1).map(r=>nm(r.baseId).toLowerCase());" +
                "const found=m.filterRows(rows,null,'  RELIC ',nm).map(r=>r.baseId);const inTab=m.filterRows(rows,m.BAGS,'',nm).length;" +
                "const saved=[rows[0].key,'9:99999'];const pruned=[...m.pruneExcluded(saved,rows)];" +
                "const list=m.tabList({bag:d.bag,tabs:[{index:0,seenAt:d.bag.at,lines:[]},{index:3,seenAt:d.bag.at,lines:[1]}]}).map(t=>[t.index,t.never,t.empty]);" +
                "console.log(JSON.stringify({tot,qty,names,found,inTab,bagRows:d.bag.lines.length,pruned,first:rows[0].key,k:[m.excludedKey('Hero-12'),m.excludedKey('Other-1')]," +
                "zero:[m.divines(0,3),m.divines(5,0),m.unitMode(0,'value'),m.unitMode(4,'value'),m.unitMode(4,'percent')],list}));";
            var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in new[] { "-e", script, Path.Combine(directory, "wealth.js"), Path.Combine(directory, SampleWealth.Key + ".json") }) info.ArgumentList.Add(arg);
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
                Console.WriteLine("WealthLogicSortSearchDivineAndStorage skipped: node not found");
                return;
            }
            var r = JsonDocument.Parse(output).RootElement;
            double[] Doubles(string name) => r.GetProperty(name).EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var total = Doubles("tot");
            Check.That(total.Zip(total.Skip(1)).All(p => p.First >= p.Second), "total desc");
            var qty = Doubles("qty");
            Check.That(qty.Zip(qty.Skip(1)).All(p => p.First <= p.Second), "qty asc");
            var names = r.GetProperty("names").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Check.That(names.Zip(names.Skip(1)).All(p => string.CompareOrdinal(p.First, p.Second) <= 0), "name asc");
            Check.Equal(SampleWealth.Unpriced, r.GetProperty("found").EnumerateArray().Single().GetInt32());
            Check.Equal(r.GetProperty("bagRows").GetInt32(), r.GetProperty("inTab").GetInt32());
            Check.Equal(1, r.GetProperty("pruned").GetArrayLength());
            Check.Equal(r.GetProperty("first").GetString(), r.GetProperty("pruned")[0].GetString());
            Check.Equal("wealth.excluded.Hero-12|wealth.excluded.Other-1", string.Join('|', r.GetProperty("k").EnumerateArray().Select(e => e.GetString())));
            var zero = r.GetProperty("zero");
            Check.Equal(0.0, zero[0].GetDouble());
            Check.That(zero[1].ValueKind == JsonValueKind.Null, "no divine price gives no Divines");
            Check.Equal("FE", zero[2].GetProperty("unit").GetString());
            Check.Equal("Div", zero[3].GetProperty("unit").GetString());
            Check.Equal("FE", zero[4].GetProperty("unit").GetString());
            Check.Equal("[[-1,false,false],[0,false,true],[1,true,false],[2,true,false],[3,false,false]]", r.GetProperty("list").GetRawText().Replace(" ", ""));
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
            Check.That(script.Contains("\"UI_Item_One\":\"Items/UI_Item_One.png\""));
            Check.That(script.Contains("WEALTH_ICON_DIR"));
            Directory.CreateDirectory(Path.Combine(directory, "Skills"));
            File.WriteAllText(Path.Combine(directory, "Skills", "index.json"), """{"icons":{"UI_Skill_One":{"file":"UI_Skill_One.png","pak":"x"},"UI_Item_One":{"file":"Dupe.png","pak":"x"}}}""");
            script = Main.IconScript(directory);
            Check.That(script.Contains("\"UI_Skill_One\":\"Skills/UI_Skill_One.png\""), "skills index");
            Check.That(script.Contains("\"UI_Item_One\":\"Items/UI_Item_One.png\""), "items win a clash");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void GarbageShapedIconIndexGivesAnEmptyMap()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prices-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Items"));
            foreach (var text in new[] { "[]", "1", "null", """{"icons":[]}""", """{"icons":5}""", """{"icons":{"A":1,"B":{"file":3},"C":[]}}""" })
            {
                File.WriteAllText(Path.Combine(directory, "Items", "index.json"), text);
                Check.That(Main.IconScript(directory).Contains("WEALTH_ICON_INDEX = {}"), text);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void IconLookupShapeMatchesTheScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prices-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Items"));
            File.WriteAllText(Path.Combine(directory, "Items", "index.json"), """{"icons":{"UI_One":{"file":"One #1.png","pak":"x"}}}""");
            File.WriteAllText(Path.Combine(directory, "icons.js"), Main.IconScript(directory));
            var app = Text("app.js");
            var start = app.IndexOf("function iconSrc", StringComparison.Ordinal);
            var end = app.IndexOf("\n  }", start, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory, "fn.js"), app[start..(end + 4)]);
            var script = "const fs=require('fs');const w={};new Function('window',fs.readFileSync(process.argv[1],'utf8'))(w);" +
                "const iconSrc=new Function('window',fs.readFileSync(process.argv[2],'utf8')+';return iconSrc;')(w);" +
                "console.log(JSON.stringify([iconSrc({icons:{1:'UI_One',2:'UI_Two'}},1),iconSrc({icons:{1:'UI_One',2:'UI_Two'}},2),iconSrc({},3)]));";
            var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in new[] { "-e", script, Path.Combine(directory, "icons.js"), Path.Combine(directory, "fn.js") }) info.ArgumentList.Add(arg);
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
                Console.WriteLine("IconLookupShapeMatchesTheScript skipped: node not found");
                return;
            }
            var urls = JsonDocument.Parse(output).RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();
            Check.That(urls[0]!.EndsWith("/Items/One%20%231.png"), urls[0]);
            Check.That(urls[1]!.EndsWith("/Items/UI_Two.png"), urls[1]);
            Check.That(urls[2] is null);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
