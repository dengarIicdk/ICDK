using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.Econ;

namespace Wibe.Core.World;

public sealed record WorldVisual(string Type, string Asset, string Modifier);

/// <summary>Один предмет мира: башни стороны, ландшафт или эмблема. Разбирается только справка.</summary>
public sealed record WorldCosmetic(
    int Id,
    string Name,
    string Prefab,
    string? Image,
    string Rarity,
    IReadOnlyList<WorldVisual> Visuals)
{
    public bool IsRadiantTower => Prefab.Equals("radianttowers", StringComparison.OrdinalIgnoreCase);

    public bool IsDireTower => Prefab.Equals("diretowers", StringComparison.OrdinalIgnoreCase);

    public bool IsTerrain => Prefab.Equals("terrain", StringComparison.OrdinalIgnoreCase);

    public bool IsEmblem => Prefab.Equals("emblem", StringComparison.OrdinalIgnoreCase);

    public bool IsRadiantCreep => Prefab.Equals("radiantcreeps", StringComparison.OrdinalIgnoreCase)
                                   || Prefab.Equals("radiantsiegecreeps", StringComparison.OrdinalIgnoreCase);

    public bool IsDireCreep => Prefab.Equals("direcreeps", StringComparison.OrdinalIgnoreCase)
                               || Prefab.Equals("diresiegecreeps", StringComparison.OrdinalIgnoreCase);

    /// <summary>Файл карты, который надо положить вместо maps/dota.vpk, или null.</summary>
    public string? TerrainMapFile
    {
        get
        {
            var map = Visuals
                .FirstOrDefault(v => v.Type.Equals("map_override", StringComparison.OrdinalIgnoreCase)
                                     && v.Modifier.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase));
            if (map is null)
            {
                return null;
            }

            var name = map.Modifier.Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }
    }

    /// <summary>Первый партикл, который игра вешает на героя.</summary>
    public string? EmblemEffect
    {
        get
        {
            var list = Visuals
                .Where(v => v.Type.Equals("particle_create", StringComparison.OrdinalIgnoreCase)
                            && v.Modifier.EndsWith(".vpcf", StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.Asset.Length == 0 ? 0 : 1)
                .ToList();
            return list.FirstOrDefault()?.Modifier;
        }
    }
}

/// <summary>
/// Справочник мировых косметик из уже извлечённого items_game.txt. Отдельный от EconCatalog:
/// там остаются только предметы героев, а башни/ландшафты/эмблемы живут без героя.
/// </summary>
public sealed class WorldCosmeticsCatalog
{
    private readonly Dictionary<int, WorldCosmetic> _byId;

    private WorldCosmeticsCatalog(IReadOnlyList<WorldCosmetic> items)
    {
        Items = items;
        _byId = items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public IReadOnlyList<WorldCosmetic> Items { get; }

    public IReadOnlyList<WorldCosmetic> RadiantTowers =>
        Items.Where(i => i.IsRadiantTower && i.Id != 677).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();

    public IReadOnlyList<WorldCosmetic> DireTowers =>
        Items.Where(i => i.IsDireTower && i.Id != 678).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();

    /// <summary>Ландшафты без стоковой карты: её роль играет карточка WIBE СТОК.</summary>
    public IReadOnlyList<WorldCosmetic> Terrains =>
        Items.Where(i => i.IsTerrain && i.Id != 590 && i.TerrainMapFile is not null)
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<WorldCosmetic> Emblems =>
        Items.Where(i => i.IsEmblem && i.EmblemEffect is not null).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();

    public IReadOnlyList<WorldCosmetic> RadiantCreeps =>
        Items.Where(i => i.IsRadiantCreep && i.Id != 660).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();

    public IReadOnlyList<WorldCosmetic> DireCreeps =>
        Items.Where(i => i.IsDireCreep && i.Id != 661).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();

    public WorldCosmetic? ById(int id) => _byId.GetValueOrDefault(id);

    public static WorldCosmeticsCatalog Load(DotaInstallation install)
    {
        _ = install;
        var path = Path.Combine(EconCatalog.CacheDirectory, "items_game.txt");
        if (!File.Exists(path))
        {
            return new WorldCosmeticsCatalog(Array.Empty<WorldCosmetic>());
        }

        var text = File.ReadAllText(path);
        return new WorldCosmeticsCatalog(Parse(text));
    }

    /// <summary>Кэш разбора: имя с версией, чтобы старый файл без новых префабов не читался.</summary>
    public static string CachePath =>
        Path.Combine(EconCatalog.CacheDirectory, "world_cosmetics_v2.json");

    public static WorldCosmeticsCatalog LoadCached(DotaInstallation install)
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var cached = JsonSerializer.Deserialize<List<WorldCosmetic>>(File.ReadAllText(CachePath));
                if (cached is { Count: > 0 })
                {
                    return new WorldCosmeticsCatalog(cached);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        var catalog = Load(install);
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(catalog.Items));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return catalog;
    }

    private static List<WorldCosmetic> Parse(string text)
    {
        var output = new List<WorldCosmetic>();
        var path = new List<string>(6);
        string? pendingKey = null;
        Builder? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[0] == '{')
            {
                path.Add(pendingKey ?? "?");
                if (path.Count == 5 && path[3] == "visuals")
                {
                    current?.BeginVisual(path[4]);
                }

                pendingKey = null;
                continue;
            }

            if (line[0] == '}')
            {
                if (path.Count == 5 && path[3] == "visuals")
                {
                    current?.EndVisual();
                }

                if (path.Count == 3 && path[1] == "items" && current is not null)
                {
                    if (current.IsWanted)
                    {
                        var item = current.ToItem();
                        if (item is not null)
                        {
                            output.Add(item);
                        }
                    }

                    current = null;
                }

                if (path.Count > 0)
                {
                    path.RemoveAt(path.Count - 1);
                }

                continue;
            }

            if (!TryReadPair(line, out var key, out var value))
            {
                continue;
            }

            if (value is null)
            {
                pendingKey = key;
                continue;
            }

            if (path.Count < 2 || path[1] != "items" || path.Count < 3)
            {
                continue;
            }

            if (path.Count == 3)
            {
                current ??= new Builder(path[2]);
                current.Set(key, value);
                continue;
            }

            if (path.Count == 5 && path[3] == "visuals")
            {
                current?.ApplyVisual(key, value);
            }
        }

        return output;
    }

    private static bool TryReadPair(string line, out string key, out string? value)
    {
        key = string.Empty;
        value = null;
        if (line[0] != '"')
        {
            return false;
        }

        var close = line.IndexOf('"', 1);
        if (close < 1)
        {
            return false;
        }

        key = line[1..close];
        var rest = line[(close + 1)..].Trim();
        if (rest.Length == 0)
        {
            return true;
        }

        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            value = end > 0 ? rest[1..end] : rest.Trim('"');
            return true;
        }

        value = rest;
        return true;
    }

    private sealed class Builder
    {
        public Builder(string id)
        {
            IdText = id;
        }

        public string IdText { get; }

        public int Id => int.TryParse(IdText, out var id) ? id : 0;

        public string Name { get; set; } = string.Empty;

        public string Prefab { get; set; } = string.Empty;

        public string? Image { get; set; }

        public string Rarity { get; set; } = string.Empty;

        public List<WorldVisual> Visuals { get; } = new();

        private VisualBuilder? _visual;

        public bool IsWanted => Prefab.Equals("radianttowers", StringComparison.OrdinalIgnoreCase)
                                || Prefab.Equals("diretowers", StringComparison.OrdinalIgnoreCase)
                                || Prefab.Equals("terrain", StringComparison.OrdinalIgnoreCase)
                                || Prefab.Equals("emblem", StringComparison.OrdinalIgnoreCase)
                                || Prefab.Equals("radiantcreeps", StringComparison.OrdinalIgnoreCase)
                                || Prefab.Equals("direcreeps", StringComparison.OrdinalIgnoreCase)
                                || Prefab.Equals("radiantsiegecreeps", StringComparison.OrdinalIgnoreCase)
                                || Prefab.Equals("diresiegecreeps", StringComparison.OrdinalIgnoreCase);

        public void Set(string key, string value)
        {
            switch (key)
            {
                case "name":
                    Name = value;
                    break;
                case "prefab":
                    Prefab = value;
                    break;
                case "image_inventory":
                    Image = value;
                    break;
                case "item_rarity":
                    Rarity = value;
                    break;
            }
        }

        public void BeginVisual(string name) => _visual = new VisualBuilder(name);

        public void EndVisual()
        {
            if (_visual is not null && (_visual.Type.Length > 0 || _visual.Modifier.Length > 0))
            {
                Visuals.Add(new WorldVisual(_visual.Type, _visual.Asset, _visual.Modifier));
            }

            _visual = null;
        }

        public void ApplyVisual(string key, string value)
        {
            if (_visual is null)
            {
                return;
            }

            switch (key)
            {
                case "type":
                    _visual.Type = value;
                    break;
                case "asset":
                    _visual.Asset = value;
                    break;
                case "modifier":
                    _visual.Modifier = value;
                    break;
            }
        }

        public WorldCosmetic? ToItem()
        {
            if (Id == 0 || Name.Length == 0)
            {
                return null;
            }

            return new WorldCosmetic(Id, Name, Prefab, Image, Rarity, Visuals.ToList());
        }
    }

    private sealed class VisualBuilder
    {
        public VisualBuilder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string Type { get; set; } = string.Empty;

        public string Asset { get; set; } = string.Empty;

        public string Modifier { get; set; } = string.Empty;
    }
}
