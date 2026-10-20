using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EmberHUD;

namespace Prices;

public readonly record struct RowAffix([property: JsonPropertyName("k")] TradeAffixKind Kind, [property: JsonPropertyName("i")] int Id, [property: JsonPropertyName("r")] float[]? Rolls);

public sealed class LocalRow
{
    [JsonPropertyName("g")] public long GoodId { get; set; }
    [JsonPropertyName("b")] public int BaseId { get; set; }
    [JsonPropertyName("sp")] public int SpecialId { get; set; }
    [JsonPropertyName("r")] public int Rare { get; set; }
    [JsonPropertyName("l")] public int Level { get; set; }
    [JsonPropertyName("il")] public int ItemLevel { get; set; }
    [JsonPropertyName("q")] public int Quality { get; set; }
    [JsonPropertyName("t")] public int Type { get; set; }
    [JsonPropertyName("c")] public bool Commodity { get; set; }
    [JsonPropertyName("p")] public long Price { get; set; }
    [JsonPropertyName("n")] public int Quantity { get; set; }
    [JsonPropertyName("f")] public long FirstSeen { get; set; }
    [JsonPropertyName("s")] public long LastSeen { get; set; }
    [JsonPropertyName("x")] public bool Sold { get; set; }
    [JsonPropertyName("a")] public RowAffix[]? Affixes { get; set; }
    [JsonPropertyName("fl")] public string[]? Flags { get; set; }
    [JsonPropertyName("sk")] public int SkillId { get; set; }
    [JsonPropertyName("st")] public int SkillType { get; set; }
    [JsonPropertyName("h")] public int HoleNum { get; set; }
    [JsonPropertyName("e")] public int EnhanceCount { get; set; }
    [JsonPropertyName("fo")] public bool Forged { get; set; }
    [JsonPropertyName("co")] public bool Corrode { get; set; }
    [JsonIgnore] public long WrittenSeen { get; set; }
}

public sealed class LocalBook
{
    public static readonly TimeSpan Recent = TimeSpan.FromHours(24);
    const int Fe = 100300;
    const int WriteSeenEvery = 60;

    sealed record AffixLine([property: JsonPropertyName("i")] int Id, [property: JsonPropertyName("t")] string Text);

    sealed record Update([property: JsonPropertyName("g")] long GoodId, [property: JsonPropertyName("s")] long LastSeen, [property: JsonPropertyName("x")] bool Sold, [property: JsonPropertyName("p")] long Price);

    sealed class Line
    {
        [JsonPropertyName("a")] public AffixLine? Affix { get; set; }
        [JsonPropertyName("r")] public LocalRow? Row { get; set; }
        [JsonPropertyName("u")] public Update? Update { get; set; }
    }

    static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    static readonly Regex Markup = new("<[^>]*>");
    static readonly Regex Roll = new(@"<p>(\+?)(-?[\d.]+)(\([^)]*\))?</p>");

    // the roll is stored per row, so the shared text keeps only the numbered slot and the tier range
    static string Template(string text)
    {
        var slot = 0;
        return Markup.Replace(Roll.Replace(text, match => $"{match.Groups[1].Value}{{{slot++}}}{match.Groups[3].Value}"), "").Trim();
    }

    readonly Dictionary<long, LocalRow> _rows = [];
    readonly Dictionary<int, string> _affixes = [];
    readonly HashSet<long> _newRows = [];
    readonly HashSet<long> _updatedRows = [];
    readonly HashSet<int> _newAffixes = [];
    int _fileUpdates;
    bool _compact;
    int? _baseCount;

    public int Count => _rows.Count;

    public int BaseCount => _baseCount ??= _rows.Values.Select(row => row.BaseId).Distinct().Count();

    public bool Dirty => _compact || _newRows.Count > 0 || _updatedRows.Count > 0 || _newAffixes.Count > 0;

    internal IReadOnlyDictionary<int, string> AffixTexts => _affixes;

    internal IEnumerable<LocalRow> Rows => _rows.Values;

    public bool Add(IEnumerable<TradeGood> goods, long nowUnix)
    {
        var changed = false;
        foreach (var good in goods)
        {
            if (good.PriceType != Fe) continue;
            var sold = good.Status == "Sold";
            if (!_rows.TryGetValue(good.GoodId, out var row))
            {
                _rows[good.GoodId] = Create(good, sold, nowUnix);
                _newRows.Add(good.GoodId);
                _baseCount = null;
                changed = true;
                continue;
            }
            var important = false;
            if (sold && !row.Sold)
            {
                row.Sold = true;
                important = true;
            }
            if (good.Price != row.Price)
            {
                row.Price = good.Price;
                important = true;
            }
            if (nowUnix > row.LastSeen)
            {
                row.LastSeen = nowUnix;
                changed = true;
            }
            if (important)
            {
                changed = true;
                MarkUpdated(row);
            }
            else if (row.LastSeen - row.WrittenSeen >= WriteSeenEvery) MarkUpdated(row);
        }
        return changed;
    }

    void MarkUpdated(LocalRow row)
    {
        if (!_newRows.Contains(row.GoodId)) _updatedRows.Add(row.GoodId);
    }

    LocalRow Create(TradeGood good, bool sold, long now)
    {
        var item = good.Item;
        foreach (var affix in good.Affixes)
            if (_affixes.TryAdd(affix.Id, Template(affix.Text))) _newAffixes.Add(affix.Id);
        return new LocalRow
        {
            GoodId = good.GoodId,
            BaseId = item.BaseId,
            SpecialId = good.SpecialId,
            Rare = item.RareValue,
            Level = item.Level,
            ItemLevel = good.ItemLevel,
            Quality = item.Quality,
            Type = item.Type,
            Commodity = item.StackMax > 1,
            Price = good.Price,
            Quantity = item.Count,
            FirstSeen = now,
            LastSeen = now,
            WrittenSeen = now,
            Sold = sold,
            Affixes = good.Affixes.Length > 0 ? [.. good.Affixes.Select(affix => new RowAffix(affix.Kind, affix.Id, affix.Rolls))] : null,
            Flags = good.Flags.Length > 0 ? good.Flags : null,
            SkillId = good.SkillId,
            SkillType = good.SkillType,
            HoleNum = good.HoleNum,
            EnhanceCount = good.EnhanceCount,
            Forged = good.Forged,
            Corrode = good.Corrode,
        };
    }

    public Dictionary<int, Price> Prices(long nowUnix, TimeSpan window)
    {
        var cutoff = nowUnix - (long)window.TotalSeconds;
        var groups = new Dictionary<int, List<(double Unit, long Seen)>>();
        foreach (var row in _rows.Values)
        {
            if (!row.Commodity || row.Sold || row.Quantity <= 0 || row.LastSeen < cutoff) continue;
            if (!groups.TryGetValue(row.BaseId, out var list)) groups[row.BaseId] = list = [];
            list.Add(((double)row.Price / row.Quantity, row.LastSeen));
        }
        var prices = new Dictionary<int, Price>();
        foreach (var (baseId, list) in groups)
        {
            list.Sort((a, b) => a.Unit.CompareTo(b.Unit));
            var q1 = list[list.Count / 4].Unit;
            var q3 = list[list.Count * 3 / 4].Unit;
            var fence = (q3 - q1) * 1.5;
            var kept = list.Where(entry => entry.Unit >= q1 - fence && entry.Unit <= q3 + fence).ToList();
            var units = kept.Select(entry => entry.Unit).ToArray();
            var newest = kept.Max(entry => entry.Seen);
            prices[baseId] = new Price((float)Exchange.ReferencePrice(units), (float)units[0], (float)units[(int)((units.Length - 1) * 0.9)], DateTimeOffset.FromUnixTimeSeconds(newest), units.Length, 1);
        }
        return prices;
    }

    public int Purge(long cutoffUnix)
    {
        var stale = _rows.Values.Where(row => row.LastSeen < cutoffUnix).Select(row => row.GoodId).ToList();
        foreach (var id in stale)
        {
            _rows.Remove(id);
            _newRows.Remove(id);
            _updatedRows.Remove(id);
        }
        if (stale.Count == 0) return 0;
        var used = _rows.Values.Where(row => row.Affixes is not null).SelectMany(row => row.Affixes!).Select(affix => affix.Id).ToHashSet();
        foreach (var id in _affixes.Keys.Where(id => !used.Contains(id)).ToList())
        {
            _affixes.Remove(id);
            _newAffixes.Remove(id);
        }
        _baseCount = null;
        _compact = true;
        return stale.Count;
    }

    static string PathOf(string directory, int season) => Path.Combine(directory, $"local-{season}.jsonl");

    public void Load(string directory, int season)
    {
        try
        {
            var path = PathOf(directory, season);
            if (!File.Exists(path)) return;
            foreach (var text in File.ReadLines(path))
            {
                if (text.Length == 0) continue;
                if (!Replay(text)) _compact = true;
            }
            if (_fileUpdates > _rows.Count) _compact = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // whatever was read stays, the rest comes back from the next scan
        }
    }

    bool Replay(string text)
    {
        Line? line;
        try
        {
            line = JsonSerializer.Deserialize<Line>(text, Options);
        }
        catch (JsonException)
        {
            return false;
        }
        if (line is null) return false;
        if (line.Affix is { } affix) _affixes.TryAdd(affix.Id, affix.Text);
        else if (line.Row is { } row)
        {
            row.WrittenSeen = row.LastSeen;
            if (_rows.TryGetValue(row.GoodId, out var known))
            {
                known.LastSeen = Math.Max(known.LastSeen, row.LastSeen);
                known.Sold |= row.Sold;
            }
            else
            {
                _rows[row.GoodId] = row;
                _baseCount = null;
            }
        }
        else if (line.Update is { } update)
        {
            _fileUpdates++;
            if (_rows.TryGetValue(update.GoodId, out var known))
            {
                known.LastSeen = Math.Max(known.LastSeen, update.LastSeen);
                known.Sold |= update.Sold;
                if (update.Price > 0) known.Price = update.Price;
            }
        }
        else return false;
        return true;
    }

    public void Flush(string directory, int season)
    {
        if (!Dirty) return;
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathOf(directory, season);
            if (_compact || _fileUpdates + _updatedRows.Count > _rows.Count) Rewrite(path);
            else Append(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // dirty stays set, the next flush tries again
        }
    }

    string Serialize(Line line) => JsonSerializer.Serialize(line, Options);

    void Append(string path)
    {
        using var writer = new StreamWriter(path, true);
        foreach (var id in _newAffixes) writer.WriteLine(Serialize(new Line { Affix = new AffixLine(id, _affixes[id]) }));
        foreach (var id in _newRows) writer.WriteLine(Serialize(new Line { Row = _rows[id] }));
        foreach (var id in _updatedRows)
        {
            var row = _rows[id];
            writer.WriteLine(Serialize(new Line { Update = new Update(id, row.LastSeen, row.Sold, row.Price) }));
            _fileUpdates++;
        }
        writer.Flush();
        MarkWritten();
    }

    void Rewrite(string path)
    {
        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp))
        {
            foreach (var (id, text) in _affixes) writer.WriteLine(Serialize(new Line { Affix = new AffixLine(id, text) }));
            foreach (var row in _rows.Values) writer.WriteLine(Serialize(new Line { Row = row }));
        }
        File.Move(temp, path, true);
        _fileUpdates = 0;
        _compact = false;
        foreach (var row in _rows.Values) row.WrittenSeen = row.LastSeen;
        MarkWritten();
    }

    void MarkWritten()
    {
        foreach (var id in _newRows) _rows[id].WrittenSeen = _rows[id].LastSeen;
        foreach (var id in _updatedRows) _rows[id].WrittenSeen = _rows[id].LastSeen;
        _newRows.Clear();
        _updatedRows.Clear();
        _newAffixes.Clear();
    }
}
