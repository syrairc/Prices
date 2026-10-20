using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Serialization;
using EmberHUD;
using EmberHUD.Ui;
using Hexa.NET.ImGui;

namespace Prices;

public sealed class Settings : PluginSettings
{
    public bool Enabled = true;
    public Corner Corner = Corner.TopLeft;
    public int OffsetX = 6;
    public int OffsetY = 8;
    public TextStyle Text = new() { Color = Color.FromArgb(0xF9, 0xDD, 0x75), ShadowEnabled = true };
    public bool ShowIcon = true;
    public bool ShowGrey = true;
    public bool ShowCyan = true;
    public bool ShowPurple = true;
    public bool ShowPink = true;
    public bool ShowOrange = true;
    public int RefreshMinutes = 5;
    public int Season;
    public bool Contribute;
    public bool UseTitrack = true;
    public bool UseEtor = true;
    public bool UseLocal;
    public int MinSamples;
    public int IgnoreOlderThanDays;
    public int PurgeAfterDays;
    public bool ExcludeOutliers = true;
    public PriceSource[] Priority = [PriceSource.Etor, PriceSource.Titrack, PriceSource.Local];
    [JsonIgnore] public string Status = "";
    [JsonIgnore] public string ContributeStatus = "";
    [JsonIgnore] public Func<int>? PurgeLocal;
    [JsonIgnore] public string PurgeResult = "";
    [JsonIgnore] public IReadOnlyDictionary<PriceSource, IReadOnlyDictionary<int, Price>> Tables = new Dictionary<PriceSource, IReadOnlyDictionary<int, Price>>();

    [JsonIgnore] public LegendaryPrices? Legendaries;
    [JsonIgnore] internal LocalBook? Book;

    public bool ShowTotals = true;
    public bool Lock = true;
    public Hotkey MoveKey = new(Key.Alt);
    public int PanelX = 40;
    public int PanelY = 200;

    public bool GroundEnabled = true;
    public bool GroundGrey;
    public bool GroundCyan;
    public bool GroundPurple;
    public bool GroundPink;
    public bool GroundOrange = true;
    public bool GroundNonEquipment;

    public const int MinRefreshMinutes = 5;

    static bool Pick(int rareValue, bool grey, bool cyan, bool purple, bool pink, bool orange) => rareValue switch { 1 => grey, 2 => cyan, 3 => purple, 4 => pink, 100 => orange, _ => true };

    public bool Shows(int rareValue) => Pick(rareValue, ShowGrey, ShowCyan, ShowPurple, ShowPink, ShowOrange);

    public bool ShowsOnGround(GroundItem item) =>
        (item.Equipment || GroundNonEquipment) && Pick(item.RareValue, GroundGrey, GroundCyan, GroundPurple, GroundPink, GroundOrange);

    public PriceSource[] Order() => [.. Priority.Distinct(), .. Enum.GetValues<PriceSource>().Except(Priority)];

    bool Uses(PriceSource source) => source switch { PriceSource.Titrack => UseTitrack, PriceSource.Etor => UseEtor, _ => UseLocal };

    [JsonIgnore] public PriceSource[] Active => [.. Order().Where(Uses)];

    public sealed record Row(int BaseId, float Median, float? Low, float? High, DateTimeOffset? Updated, int? Submissions, int? Devices);

    public static List<Row> Rows(IReadOnlyDictionary<int, Price> table) => [.. table.OrderBy(pair => pair.Key).Select(pair => new Row(pair.Key, pair.Value.Median, pair.Value.Low, pair.Value.High, pair.Value.Updated, pair.Value.Submissions, pair.Value.Devices))];

    static readonly TableColumn<Row>[] DatabaseColumns =
    [
        new() { Header = "Base ID", Width = 90, SortKey = row => row.BaseId, Draw = (row, _) => ImGui.TextUnformatted(row.BaseId.ToString()) },
        new() { Header = "Name", SortKey = row => ItemNames.Of(row.BaseId), Draw = (row, _) => ImGui.TextUnformatted(ItemNames.Of(row.BaseId)) },
        new() { Header = "Price (FE)", Width = 110, SortKey = row => row.Median, Draw = (row, _) => ImGui.TextUnformatted(Number(row.Median)) },
        new() { Header = "Low", Width = 110, SortKey = row => row.Low ?? -1f, Draw = (row, _) => ImGui.TextUnformatted(row.Low is { } low ? Number(low) : "-") },
        new() { Header = "High", Width = 110, SortKey = row => row.High ?? -1f, Draw = (row, _) => ImGui.TextUnformatted(row.High is { } high ? Number(high) : "-") },
        new() { Header = "Samples", Width = 80, SortKey = row => row.Submissions ?? -1, Draw = (row, _) => ImGui.TextUnformatted(row.Submissions?.ToString() ?? "-") },
        new() { Header = "Devices", Width = 80, SortKey = row => row.Devices ?? -1, Draw = (row, _) => ImGui.TextUnformatted(row.Devices?.ToString() ?? "-") },
        new() { Header = "Updated", Width = 130, SortKey = row => row.Updated?.UtcTicks ?? 0L, Draw = (row, _) => ImGui.TextUnformatted(row.Updated is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "-") },
    ];

    static string Number(float value) => value.ToString(value < 10 ? "0.###" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

    static readonly TableColumn<LegendaryEntry>[] LegendaryColumns =
    [
        new() { Header = "Name", SortKey = row => ItemNames.Of(row.SpecialId), Draw = (row, _) => ImGui.TextUnformatted(ItemNames.Of(row.SpecialId)) },
        new() { Header = "State", Width = 100, SortKey = row => row.Unidentified, Draw = (row, _) => ImGui.TextUnformatted(row.Unidentified ? "Unidentified" : "Identified") },
        new() { Header = "Price (FE)", Width = 110, SortKey = row => row.Price.Median, Draw = (row, _) => ImGui.TextUnformatted(Number(row.Price.Median)) },
        new() { Header = "Low", Width = 110, SortKey = row => row.Price.Low ?? -1f, Draw = (row, _) => ImGui.TextUnformatted(row.Price.Low is { } low ? Number(low) : "-") },
        new() { Header = "High", Width = 110, SortKey = row => row.Price.High ?? -1f, Draw = (row, _) => ImGui.TextUnformatted(row.Price.High is { } high ? Number(high) : "-") },
        new() { Header = "Samples", Width = 80, SortKey = row => row.Price.Submissions ?? -1, Draw = (row, _) => ImGui.TextUnformatted(row.Price.Submissions?.ToString() ?? "-") },
        new() { Header = "Updated", Width = 130, SortKey = row => row.Price.Updated?.UtcTicks ?? 0L, Draw = (row, _) => ImGui.TextUnformatted(row.Price.Updated is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "-") },
    ];

    enum LocalView { Prices, Legendaries, Listings }

    LocalView _localView;
    readonly LocalListings _listings = new();
    string _legendaryFilter = "";
    LegendaryPrices? _viewLegendaries;
    List<LegendaryEntry> _legendaryRows = [];

    void DrawLegendaries()
    {
        if (!ReferenceEquals(Legendaries, _viewLegendaries))
        {
            _viewLegendaries = Legendaries;
            _legendaryRows = Legendaries is null ? [] : [.. Legendaries.Entries];
        }
        ImGui.TextDisabled($"{_legendaryRows.Count} legendaries");
        if (_legendaryRows.Count == 0)
        {
            ImGui.TextDisabled("No legendary listings scanned yet");
            return;
        }
        SortableTable.Draw("legendaries", _legendaryRows, LegendaryColumns, ref _legendaryFilter, row => ItemNames.Of(row.SpecialId) + " " + (row.Unidentified ? "Unidentified" : "Identified"), ImGui.GetContentRegionAvail().Y);
    }

    PriceSource _view = PriceSource.Titrack;
    string _filter = "";
    IReadOnlyDictionary<int, Price>? _viewTable;
    List<Row> _rows = [];

    internal void DrawDatabase()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Database");
        ImGui.SameLine(160);
        Combo.Enum(ref _view, "dbview");
        if (_view == PriceSource.Local)
        {
            ImGui.SameLine();
            Combo.Enum(ref _localView, "localview");
            if (_localView == LocalView.Legendaries)
            {
                DrawLegendaries();
                return;
            }
            if (_localView == LocalView.Listings)
            {
                _listings.Draw(Book);
                return;
            }
        }
        var table = Tables.TryGetValue(_view, out var found) ? found : null;
        if (!ReferenceEquals(table, _viewTable))
        {
            _viewTable = table;
            _rows = table is null ? [] : Rows(table);
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{_rows.Count} items");
        if (_rows.Count == 0)
        {
            ImGui.TextDisabled(Uses(_view) ? "Nothing loaded from this database yet" : "This database is turned off");
            return;
        }
        SortableTable.Draw("database", _rows, DatabaseColumns, ref _filter, row => row.BaseId + " " + ItemNames.Of(row.BaseId), ImGui.GetContentRegionAvail().Y);
    }

    string _seasonText = "";
    bool _seasonEditing;

    public override void Draw() => Layout.TabBar("prices", ("Prices", DrawPrices), ("Ground Items", DrawGround), ("Totals", DrawTotals), ("Database", DrawDatabase));

    internal void DrawTotals()
    {
        Setting.Checkbox("Show totals", ref ShowTotals, "The FE value of your whole inventory (all four bags) next to the bag's sort button, and while the stash is open the open tab / every stash tab opened since Ember started - the game only loads a tab once you open it. Gear counts at its median price, unpriced items count 0. Uses the Prices tab's text style and FE icon");
        Setting.Checkbox("Lock", ref Lock, "The stash total ignores the mouse while locked - hold the move key to drag it anyway");
        Setting.Hotkey("Move key", ref MoveKey, "Hold to move the stash total while locked");
    }

    internal void DrawGround()
    {
        Setting.Checkbox("Show on ground items", ref GroundEnabled, "A price tag next to the label of each priced item on the ground in maps");
        Setting.Checkbox("Grey items", ref GroundGrey, "Price tags on grey (normal) items on the ground", id: "GroundGrey");
        Setting.Checkbox("Cyan items", ref GroundCyan, "Price tags on cyan (magic) items on the ground", id: "GroundCyan");
        Setting.Checkbox("Purple items", ref GroundPurple, "Price tags on purple (rare) items on the ground", id: "GroundPurple");
        Setting.Checkbox("Pink items", ref GroundPink, "Price tags on pink items on the ground", id: "GroundPink");
        Setting.Checkbox("Orange items", ref GroundOrange, "Price tags on orange (legendary) items on the ground", id: "GroundOrange");
        Setting.Checkbox("Non-equipment items", ref GroundNonEquipment, "Also tag items that are not gear - currency, materials and the like. They follow the same colour choices");
    }

    internal void DrawPrices()
    {
        Setting.Checkbox("Show prices", ref Enabled, "A price tag on each priced item in the open bag");
        Setting.Choice("Corner", ref Corner, "Which corner of the cell the tag sits in - bottom-right is where the game draws its own item count");
        Setting.Slider("Offset X", ref OffsetX, -100, 100, "Pixels from the corner, positive moves the tag right");
        Setting.Slider("Offset Y", ref OffsetY, -100, 100, "Pixels from the corner, positive moves the tag down");
        Setting.Style("Text", Text, "Colour, size, background and shadow of the tag");
        Setting.Checkbox("Show FE icon", ref ShowIcon, "The FE icon in front of the number - needs the game icons from Setup");
        Setting.Checkbox("Grey items", ref ShowGrey, "Price tags on grey frame items (normal)");
        Setting.Checkbox("Cyan items", ref ShowCyan, "Price tags on cyan frame items (magic)");
        Setting.Checkbox("Purple items", ref ShowPurple, "Price tags on purple frame items (rare)");
        Setting.Checkbox("Pink items", ref ShowPink, "Price tags on pink frame items");
        Setting.Checkbox("Orange items", ref ShowOrange, "Price tags on orange frame items (legendary)");
        Setting.Slider("Refresh every (minutes)", ref RefreshMinutes, MinRefreshMinutes, 60, "How often new prices are downloaded");
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Season");
        EmberHUD.Ui.Text.Tip("Which season's prices are shown. 0 = automatic - the season from the game log, else the newest one with recent prices. Never changes what is shared - shared prices always carry the season from the game log. Type a number and press Enter");
        ImGui.SameLine(160);
        ImGui.SetNextItemWidth(120);
        if (!_seasonEditing) _seasonText = Season.ToString();
        var entered = ImGui.InputText("##season", ref _seasonText, 8, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CharsDecimal);
        _seasonEditing = ImGui.IsItemActive();
        if (entered) Season = int.TryParse(_seasonText, out var typed) ? Math.Max(0, typed) : 0;
        if (Status.Length > 0) ImGui.TextDisabled(Status);
        DrawSources();
        Setting.Checkbox("Share Exchange prices", ref Contribute, "Reads the game log (Settings > Other > Enable Log) and sends the prices of your Exchange searches to TITrack's price server, the same way TITrack does. Off = nothing is read or sent");
        if (Contribute && ContributeStatus.Length > 0) ImGui.TextDisabled(ContributeStatus);
    }

    int _selectedSource;

    static string Tip(PriceSource source) => source switch
    {
        PriceSource.Titrack => "TITrack's community price server - crowd-sourced Exchange searches, many items, some prices skewed by filtered searches",
        PriceSource.Etor => "ETor's community price server - only downloaded, nothing is sent. An unofficial interface, it can change or go away",
        _ => "Your own database, filled from the trade house while it is open - prices come from the cheap pile of commodity listings you scanned",
    };

    void DrawSources()
    {
        ImGui.SeparatorText("Price databases");
        ImGui.TextDisabled("Drag to reorder - higher in the list wins when two databases price the same item");
        List<PriceSource> order = [.. Order()];
        if (ListEditor.ReorderableList("sources", order, ref _selectedSource, (source, _) =>
        {
            var on = Uses(source);
            if (ImGui.Checkbox("##use", ref on))
            {
                if (source == PriceSource.Titrack) UseTitrack = on;
                else if (source == PriceSource.Etor) UseEtor = on;
                else UseLocal = on;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(source == PriceSource.Titrack ? "TITrack" : source.ToString());
            EmberHUD.Ui.Text.Tip(Tip(source));
        }, new ListOpts { ShowIndex = true })) Priority = [.. order];
        Setting.Slider("Minimum samples", ref MinSamples, 0, 200, "Ignore a price that fewer than this many Exchange searches back, the next database in the list prices the item instead. 0 = keep everything. Only TITrack reports sample counts, ETor prices always stay");
        Setting.Checkbox("Exclude price outliers", ref ExcludeOutliers, "Local prices only. Drops listings priced far above or below the bulk before picking the price - if most of an item is listed at 1 FE and a few at 200 FE, the item is worth 1 FE. Off = every listing counts");
        Setting.Slider("Ignore older than (days)", ref IgnoreOlderThanDays, 0, 90, "Ignore a price whose last update is older than this many days, the next database in the list prices the item instead. 0 = keep everything. Applies to every database");
        Setting.Slider("Purge local data after (days)", ref PurgeAfterDays, 0, 365, "Delete saved local trade house listings that were last seen more than this many days ago, on start and then every hour. 0 = never. Only your local database is purged, TITrack and ETor are downloaded copies");
        if (ImGui.Button("Purge now##local")) PurgeResult = PurgeAfterDays <= 0 ? "Set a number of days first" : PurgeLocal is { } purge ? $"Removed {purge()} saved listings" : "";
        if (PurgeResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(PurgeResult);
        }
    }
}
