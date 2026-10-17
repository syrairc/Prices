using System;
using System.Drawing;
using System.Text.Json.Serialization;
using EmberHUD;
using EmberHUD.Ui;
using Hexa.NET.ImGui;

namespace Prices;

public sealed class Settings : PluginSettings
{
    public bool Enabled = true;
    public Corner Corner = Corner.TopLeft;
    public TextStyle Text = new() { Color = Color.FromArgb(0xF9, 0xDD, 0x75), ShadowEnabled = true };
    public bool ShowIcon = true;
    public int RefreshMinutes = 5;
    public int Season;
    public bool Contribute;
    [JsonIgnore] public string Status = "";
    [JsonIgnore] public string ContributeStatus = "";

    public const int MinRefreshMinutes = 5;

    string _seasonText = "";
    bool _seasonEditing;

    public override void Draw()
    {
        Setting.Checkbox("Show prices", ref Enabled, "A price tag on each priced item in the open bag");
        Setting.Choice("Corner", ref Corner, "Which corner of the cell the tag sits in - bottom-right is where the game draws its own item count");
        Setting.Style("Text", Text, "Colour, size, background and shadow of the tag");
        Setting.Checkbox("Show FE icon", ref ShowIcon, "The FE icon in front of the number - needs the game icons from Setup");
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
        Setting.Checkbox("Share Exchange prices", ref Contribute, "Reads the game log (Settings > Other > Enable Log) and sends the prices of your Exchange searches to TITrack's price server, the same way TITrack does. Off = nothing is read or sent");
        if (Contribute && ContributeStatus.Length > 0) ImGui.TextDisabled(ContributeStatus);
    }
}
