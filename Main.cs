using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using EmberHUD;
using EmberHUD.Ui;

namespace Prices;

public sealed class Main : Plugin<Settings>
{
    const string FeIcon = "UI_Item_chuhuoyuanzhiV3_Icon_128";
    const float IconGap = 2;
    const float GroundGap = 4;

    static readonly Dictionary<int, Price> NoPrices = [];

    readonly Dictionary<(int BaseId, int Count, bool Gear), string> _tags = [];
    IReadOnlyDictionary<int, Price> _prices = NoPrices;
    HttpClient? _http;
    long _nextAt;
    (int, int?, string) _seasonKey;
    (int, int?, string) _jobKey;
    (int, int?, string)? _fullKey;
    int? _heldSeason;
    Synced? _synced;
    int _builtMin;
    Contribution? _contribution;
    CancellationTokenSource? _stopContributing;
    readonly Dictionary<int, InventoryItem[]> _stashTabs = [];
    readonly List<(string Left, string Right)> _totals = new(3);
    readonly TextStyle _totalStyle = TextStyle.Hud();
    readonly Overlay _overlay = new();
    int _unusedWidth, _unusedHeight;

    internal string DataDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Config", "Prices");

    internal HttpClient Http
    {
        get => _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        set => _http = value;
    }

    internal BackgroundJob<Synced> Job { get; set; } = new();

    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    internal int? LogSeason { get; set; }

    internal int StashTabsSeen => _stashTabs.Count;

    public IReadOnlyDictionary<int, Price> Prices
    {
        get => _prices;
        internal set
        {
            _prices = value;
            _tags.Clear();
        }
    }

    public override void OnEnable()
    {
        _fullKey = null;
        if (PriceSources.Cached(DataDirectory, Settings.Active, Settings.Season, LogSeason) is { } cached) Apply(cached.Synced, cached.At);
    }

    public override void Tick()
    {
        if (Settings.GroundEnabled) Game.WantGroundItems();
        Contribute();
        if (_synced is not null && _builtMin != Settings.MinSamples) Prices = Build(_synced);
        if (Job.TakeFinished() is { } done)
        {
            var result = done.GetAwaiter().GetResult();
            if (_jobKey == (Settings.Season, LogSeason, SourceKey))
            {
                Apply(result);
                if (result.Error is null) _fullKey = _jobKey;
            }
            _nextAt = Clock() + Math.Max(Settings.MinRefreshMinutes, Settings.RefreshMinutes) * 60_000L;
        }
        var seasonKey = (Settings.Season, LogSeason, SourceKey);
        if (seasonKey != _seasonKey) _nextAt = 0;
        if (!(Settings.Enabled || Settings.GroundEnabled) || Job.Running || Clock() < _nextAt) return;
        _seasonKey = _jobKey = seasonKey;
        var http = Http;
        var directory = DataDirectory;
        var full = _fullKey != seasonKey;
        var active = Settings.Active;
        Job.Start(() => PriceSources.Sync(http, directory, active, seasonKey.Item1, seasonKey.Item2, full));
    }

    string SourceKey => string.Join(',', Settings.Active);

    public override void OnDisable() => StopContributing();

    public override void Dispose()
    {
        StopContributing();
        _http?.Dispose();
    }

    void Contribute()
    {
        if (!Settings.Contribute)
        {
            StopContributing();
            return;
        }
        if (_contribution is null)
        {
            if (Game.ExePath is not { } exe)
            {
                Settings.ContributeStatus = "waiting for the game";
                return;
            }
            StartContributing(Exchange.LogPath(exe));
        }
        var contribution = _contribution!;
        Settings.ContributeStatus = contribution.Status;
        if (contribution.Season is { } season) LogSeason = season;
        if (!contribution.Found.IsEmpty) Prices = Merged(_prices, contribution.Found);
    }

    void StartContributing(string logPath)
    {
        var contribution = _contribution = new Contribution(Http, logPath, DataDirectory);
        var stop = _stopContributing = new CancellationTokenSource();
        Task.Run(async () =>
        {
            var flushAt = DateTime.UtcNow.AddMinutes(1);
            try
            {
                contribution.Start();
                while (!stop.IsCancellationRequested)
                {
                    var now = DateTime.UtcNow;
                    try
                    {
                        contribution.Poll(now);
                        if (now >= flushAt)
                        {
                            contribution.Flush(() => Settings.Contribute && !stop.IsCancellationRequested, now);
                            flushAt = now.AddMinutes(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        contribution.Fail(ex.Message);
                    }
                    contribution.Describe(now);
                    await Task.Delay(1000, stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch (Exception ex)
            {
                contribution.Fail(ex.Message);
                contribution.Describe(DateTime.UtcNow);
            }
        });
    }

    void StopContributing()
    {
        _stopContributing?.Cancel();
        _stopContributing = null;
        _contribution = null;
        Settings.ContributeStatus = "";
    }

    internal static Dictionary<int, Price> Merged(IReadOnlyDictionary<int, Price> prices, ConcurrentQueue<Search> found)
    {
        var merged = new Dictionary<int, Price>(prices);
        while (found.TryDequeue(out var search))
        {
            var median = (float)Exchange.ReferencePrice(search.PricesFe);
            merged[search.BaseId] = merged.TryGetValue(search.BaseId, out var old) ? old with { Median = median } : new Price(median, null, null);
        }
        return merged;
    }

    IReadOnlyDictionary<int, Price> Build(Synced result)
    {
        _builtMin = Settings.MinSamples;
        if (_builtMin <= 0 || result.Tables is not { } tables) return result.File!.Rows;
        return PriceSources.Combine(Settings.Active.Where(tables.ContainsKey).Select(source => tables[source]), _builtMin);
    }

    void Apply(Synced result, DateTime? at = null)
    {
        if (result.File is not null)
        {
            _synced = result;
            Prices = Build(result);
            Settings.Tables = result.Tables ?? Settings.Tables;
            _heldSeason = result.Season;
        }
        else if (result.Season is { } season && _heldSeason is { } held && held != season)
        {
            Prices = NoPrices;
            _heldSeason = season;
        }
        var from = result.Season is { } shown ? $"season {shown} ({result.Source})" : "no season";
        var databases = result.Databases.Length > 0 ? $" from {result.Databases}" : "";
        Settings.Status = result.Error is { } error ? $"download failed: {error} - {from}" : $"{_prices.Count} prices{databases}, {from}, updated {at ?? DateTime.Now:HH:mm}";
    }

    public override void Render()
    {
        DrawInventory();
        DrawTotals();
        DrawGround();
    }

    void DrawTotals()
    {
        var state = Game.State;
        if (state.Stash is { } shown && state.StashItems is { } shownItems) _stashTabs[shown.Tab] = shownItems;
        if (!Settings.ShowTotals || OpenKind(state.Ui) is null) return;
        Game.WantInventory();
        _totals.Clear();
        if (state.Inventory is { } inventory) _totals.Add(("Inventory", Fe(Tags.Total(inventory, _prices))));
        if (state.Stash is { } stash && _stashTabs.TryGetValue(stash.Tab, out var tab))
        {
            var all = 0.0;
            foreach (var items in _stashTabs.Values) all += Tags.Total(items, _prices);
            _totals.Add(("Stash tab", Fe(Tags.Total(tab, _prices))));
            _totals.Add(($"Stash ({_stashTabs.Count} {(_stashTabs.Count == 1 ? "tab" : "tabs")} seen)", Fe(all)));
        }
        if (_totals.Count == 0) return;

        const float Pad = 8;
        const float Gap = 24;
        var width = 0f;
        foreach (var (left, right) in _totals) width = MathF.Max(width, Draw.MeasureText(left, _totalStyle).X + Gap + Draw.MeasureText(right, _totalStyle).X);
        var rowHeight = Draw.RowHeight(_totalStyle);
        _overlay.Locked = Settings.Lock;
        _overlay.MoveHeld = Settings.MoveKey.Key != Key.None && Input.IsDown(Settings.MoveKey.Key);
        var origin = new Vector2(Settings.PanelX, Settings.PanelY);
        var size = new Vector2(width + Pad * 2, rowHeight * _totals.Count + Pad * 2);
        var (hovered, _, active) = _overlay.Handle("prices-totals", ref origin, size, ref Settings.PanelX, ref Settings.PanelY, ref _unusedWidth, 60, 4000, ref _unusedHeight, 0, 0, Overlay.ResizeEdge.None);
        if (hovered || active) Overlay.ClickBlocker("prices-totals", origin, size);
        Draw.RectFilled(origin, origin + size, GameColors.LabelBackground, 4);
        if (_overlay.Movable) Overlay.DragHint(origin, origin + size, active, hovered, Overlay.ResizeEdge.None);
        var y = origin.Y + Pad;
        foreach (var (left, right) in _totals)
        {
            Draw.Row(new Vector2(origin.X + Pad, y), width, left, right, _totalStyle);
            y += rowHeight;
        }
    }

    static string Fe(double fe) => (Tags.Format(fe) ?? "0") + " FE";

    void DrawInventory()
    {
        if (!Settings.Enabled) return;
        var state = Game.State;
        var kind = OpenKind(state.Ui);
        if (kind is null) return;
        Game.WantInventory();
        if (kind == PanelKind.Inventory)
        {
            if (state.Bag is { } bag && state.Inventory is { } inventory)
                DrawTags(inventory, bag.ClipMin, bag.ClipMax, bag.Cells, item => item.Bag == bag.Tab);
        }
        else if (state.Stash is { } stash && state.StashItems is { } items)
            DrawTags(items, stash.ClipMin, stash.ClipMax, stash.Cells, _ => true);
    }

    void DrawTags(IReadOnlyList<InventoryItem> items, Vector2 clipMin, Vector2 clipMax, BagCell[] cells, Func<InventoryItem, bool> onTab)
    {
        var icon = TagIcon(out var hasIcon);
        using var clip = Draw.Clip(clipMin, clipMax);
        foreach (var item in items)
        {
            if (!onTab(item) || !Settings.Shows(item.RareValue) || TagFor(item.BaseId, item.Count, item.Bag == InventoryBag.Equipment) is not { } text) continue;
            foreach (var cell in cells)
            {
                if (cell.Slot != item.Slot) continue;
                DrawTag(text, icon, hasIcon, size => Tags.Anchor(Settings.Corner, cell.Min, cell.Max, size) + new Vector2(Settings.OffsetX, Settings.OffsetY));
                break;
            }
        }
    }

    void DrawGround()
    {
        if (!Settings.GroundEnabled || Game.State.GroundItems is not { } items || Game.State.Ui.AnyFullScreen) return;
        var icon = TagIcon(out var hasIcon);
        foreach (var item in items)
        {
            if (!Settings.ShowsOnGround(item) || TagFor(item.BaseId, item.Count, item.Equipment) is not { } text) continue;
            DrawTag(text, icon, hasIcon, size => new Vector2(item.LabelMax.X + GroundGap, (item.LabelMin.Y + item.LabelMax.Y - size.Y) / 2));
        }
    }

    Sprite TagIcon(out bool hasIcon)
    {
        Sprite icon = default;
        hasIcon = Settings.ShowIcon && Draw.TryGameIcon(FeIcon, out icon);
        return icon;
    }

    void DrawTag(string text, Sprite icon, bool hasIcon, Func<Vector2, Vector2> place)
    {
        var style = Settings.Text;
        var size = Draw.MeasureText(text, style);
        var height = size.Y;
        var at = place(new Vector2(size.X + (hasIcon ? height + IconGap : 0), height));
        if (hasIcon)
        {
            Draw.Image(icon, at, at + new Vector2(height));
            at.X += height + IconGap;
        }
        Draw.Text(text, at, style);
    }

    static PanelKind? OpenKind(GameUi ui)
    {
        foreach (var panel in ui.Panels)
            if (panel.Kind is PanelKind.Inventory or PanelKind.Stash) return panel.Kind;
        return null;
    }

    string? TagFor(int baseId, int count, bool gear)
    {
        var key = (baseId, count, gear);
        if (_tags.TryGetValue(key, out var text)) return text;
        text = Tags.Text(baseId, count, gear, _prices);
        if (text is not null) _tags[key] = text;
        return text;
    }
}
