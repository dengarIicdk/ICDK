using System.Text.Json;
using System.Text.Json.Serialization;
using Wibe.Core.Mods;

namespace Wibe.Server;

/// <summary>
/// «Примерочная» на несколько героев: выбранные предметы живут в selected_loadout.json,
/// переживают переходы по героям и перезапуск программы. Компилируется всё разом.
/// </summary>
public sealed class LoadoutStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    private readonly object _lock = new();

    private LoadoutFile _data = new();

    public LoadoutStore(string path)
    {
        _path = path;
        Load();
        Normalize();
    }

    public string Path => _path;

    /// <summary>Ключ примерки — короткое имя героя: «ogre_magi», без npc_dota_hero_.</summary>
    public static string NormalizeFolder(string folder)
    {
        var name = (folder ?? string.Empty).Trim().ToLowerInvariant();
        return name.StartsWith("npc_dota_hero_", StringComparison.Ordinal)
            ? name["npc_dota_hero_".Length..]
            : name;
    }

    public IReadOnlyDictionary<string, Dictionary<string, int>> Heroes
    {
        get
        {
            lock (_lock)
            {
                return _data.Heroes.ToDictionary(
                    pair => pair.Key,
                    pair => new Dictionary<string, int>(pair.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public int HeroesCount
    {
        get
        {
            lock (_lock)
            {
                return _data.Heroes.Count(h => h.Value.Count > 0);
            }
        }
    }

    public int ItemsCount
    {
        get
        {
            lock (_lock)
            {
                return _data.Heroes.Sum(h => h.Value.Count);
            }
        }
    }

    public Dictionary<string, int> ForHero(string folder)
    {
        lock (_lock)
        {
            folder = NormalizeFolder(Resolve(folder));
            return _data.Heroes.TryGetValue(folder, out var picks)
                ? new Dictionary<string, int>(picks, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Как игра называет героя: страница wraith_king и записи skeleton_king — один герой,
    /// поэтому чтение и запись идут через одно имя, а не мимо друг друга.
    /// </summary>
    public Func<string, string>? FolderAlias { get; set; }

    private string Resolve(string folder) =>
        FolderAlias is null ? folder : FolderAlias(folder) ?? folder;

    public bool HasHero(string folder)
    {
        lock (_lock)
        {
            folder = NormalizeFolder(Resolve(folder));
            return _data.Heroes.TryGetValue(folder, out var picks) && picks.Count > 0;
        }
    }

    /// <summary>Стили всех героев: пресет обязан помнить, какой облик был выбран.</summary>
    public Dictionary<string, Dictionary<string, int>> Styles()
    {
        lock (_lock)
        {
            return _data.Styles.ToDictionary(
                p => p.Key,
                p => new Dictionary<string, int>(p.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public Dictionary<string, int> StylesForHero(string folder)
    {
        lock (_lock)
        {
            folder = NormalizeFolder(Resolve(folder));
            return _data.Styles.TryGetValue(folder, out var styles)
                ? new Dictionary<string, int>(styles, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Мировые выборы: radiant_tower, dire_tower, terrain, emblem → id предмета.</summary>
    public Dictionary<string, string> WorldFor()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_data.World, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetWorld(string kind, string? id)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (kind.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _data.World.Remove(kind);
            }
            else
            {
                _data.World[kind] = id.Trim();
            }

            _data.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public void SetWorld(IReadOnlyDictionary<string, string> world)
    {
        lock (_lock)
        {
            _data.World = world
                .Where(p => p.Value.Length > 0)
                .ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value, StringComparer.OrdinalIgnoreCase);
            _data.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    /// <summary>Редкая насмешка (со своей анимацией) выбрана у этого героя.</summary>
    public bool RareTauntFor(string folder)
    {
        lock (_lock)
        {
            folder = NormalizeFolder(Resolve(folder));
            return _data.RareTaunt.TryGetValue(folder, out var rare) && rare;
        }
    }

    public void SetHero(
        string folder,
        IEnumerable<KeyValuePair<string, int>> picks,
        IReadOnlyDictionary<string, int>? styles = null,
        bool rareTaunt = false)
    {
        folder = NormalizeFolder(Resolve(folder));
        lock (_lock)
        {
            var map = picks
                .Where(p => p.Value != 0 && p.Key.Length > 0)
                .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

            if (map.Count == 0)
            {
                _data.Heroes.Remove(folder);
                _data.Styles.Remove(folder);
                _data.RareTaunt.Remove(folder);
            }
            else
            {
                _data.Heroes[folder] = map;
                if (rareTaunt && map.ContainsKey("taunt"))
                {
                    _data.RareTaunt[folder] = true;
                }
                else
                {
                    _data.RareTaunt.Remove(folder);
                }
                var styleMap = (styles ?? new Dictionary<string, int>())
                    .Where(p => p.Value > 0 && map.ContainsKey(p.Key))
                    .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
                if (styleMap.Count == 0)
                {
                    _data.Styles.Remove(folder);
                }
                else
                {
                    _data.Styles[folder] = styleMap;
                }
            }

            _data.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public void RemoveHero(string folder)
    {
        folder = NormalizeFolder(folder);
        lock (_lock)
        {
            _data.Heroes.Remove(folder);
            _data.Styles.Remove(folder);
            _data.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _data = new LoadoutFile { UpdatedAt = DateTimeOffset.UtcNow };
            Save();
        }
    }

    public void Replace(
        IReadOnlyDictionary<string, Dictionary<string, int>> heroes,
        IReadOnlyDictionary<string, string>? world = null,
        IReadOnlyDictionary<string, Dictionary<string, int>>? styles = null)
    {
        lock (_lock)
        {
            _data = new LoadoutFile
            {
                Heroes = heroes.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase),
                Styles = styles is null
                    ? new Dictionary<string, Dictionary<string, int>>(_data.Styles, StringComparer.OrdinalIgnoreCase)
                    : styles.ToDictionary(
                        p => p.Key,
                        p => new Dictionary<string, int>(p.Value, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase),
                RareTaunt = new Dictionary<string, bool>(_data.RareTaunt, StringComparer.OrdinalIgnoreCase),
                World = world is null
                    ? new Dictionary<string, string>(_data.World, StringComparer.OrdinalIgnoreCase)
                    : world.Where(p => p.Value.Length > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            Save();
        }
    }

    public IReadOnlyList<HeroPicks> AsPicks()
    {
        lock (_lock)
        {
            return _data.Heroes
                .Where(pair => pair.Value.Count > 0)
                .Select(pair => new HeroPicks(
                    pair.Key,
                    pair.Value.Select(p => (p.Key, p.Value)).ToList(),
                    _data.Styles.TryGetValue(pair.Key, out var styles)
                        ? new Dictionary<string, int>(styles, StringComparer.OrdinalIgnoreCase)
                        : null,
                    _data.RareTaunt.TryGetValue(pair.Key, out var rare) && rare))
                .ToList();
        }
    }

    public object Info() => new
    {
        heroes = Heroes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase),
        styles = _data.Styles.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, int>(pair.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase),
        rareTaunt = new Dictionary<string, bool>(_data.RareTaunt, StringComparer.OrdinalIgnoreCase),
        heroesCount = HeroesCount,
        itemsCount = ItemsCount,
        world = WorldFor(),
        path = _path,
    };

    /// <summary>Старые файлы хранили полное npc-имя — приводим их к короткому ключу.</summary>
    private void Normalize()
    {
        var heroes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _data.Heroes)
        {
            var key = NormalizeFolder(pair.Key);
            heroes[key] = pair.Value;
        }

        _data.RareTaunt = new Dictionary<string, bool>(_data.RareTaunt, StringComparer.OrdinalIgnoreCase);
        var styles = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _data.Styles)
        {
            var key = NormalizeFolder(pair.Key);
            styles[key] = pair.Value;
        }

        _data.Heroes = heroes;
        _data.Styles = styles;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _data = JsonSerializer.Deserialize<LoadoutFile>(File.ReadAllText(_path), Json) ?? new LoadoutFile();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _data = new LoadoutFile();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class LoadoutFile
    {
        [JsonPropertyName("heroes")]
        public Dictionary<string, Dictionary<string, int>> Heroes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("styles")]
        public Dictionary<string, Dictionary<string, int>> Styles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("rareTaunt")]
        public Dictionary<string, bool> RareTaunt { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("world")]
        public Dictionary<string, string> World { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}

public sealed record PresetEntry(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    Dictionary<string, Dictionary<string, int>> Heroes,
    Dictionary<string, string>? World = null,
    Dictionary<string, Dictionary<string, int>>? Styles = null);

/// <summary>Пресеты сборок: presets.json рядом с программой (или в %LOCALAPPDATA%, если папка только для чтения).</summary>
public sealed class PresetStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    private readonly object _lock = new();

    private List<PresetEntry> _presets = new();

    public PresetStore(string path)
    {
        _path = path;
        Load();
    }

    public string Path => _path;

    public IReadOnlyList<PresetEntry> All
    {
        get
        {
            lock (_lock)
            {
                return _presets.ToList();
            }
        }
    }

    public PresetEntry Save(
        string name,
        IReadOnlyDictionary<string, Dictionary<string, int>> heroes,
        IReadOnlyDictionary<string, string>? world = null,
        IReadOnlyDictionary<string, Dictionary<string, int>>? styles = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            trimmed = "сборка " + DateTime.Now.ToString("dd.MM HH:mm");
        }

        var entry = new PresetEntry(
            Guid.NewGuid().ToString("n")[..8],
            trimmed,
            DateTimeOffset.UtcNow,
            heroes
                .Where(p => p.Value.Count > 0)
                .ToDictionary(
                    p => p.Key,
                    p => p.Value.Where(x => x.Value != 0).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase),
            world is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : world.Where(p => p.Value.Length > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            styles is null
                ? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
                : styles
                    .Where(p => p.Value.Count > 0)
                    .ToDictionary(
                        p => p.Key,
                        p => new Dictionary<string, int>(p.Value, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase));

        lock (_lock)
        {
            _presets.RemoveAll(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            _presets.Insert(0, entry);
            Save();
        }

        return entry;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _presets.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    public PresetEntry? Find(string id)
    {
        lock (_lock)
        {
            return _presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(_path), Json);
            _presets = payload?.Presets ?? new List<PresetEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _presets = new List<PresetEntry>();
        }
    }

    private void Save()
    {
        try
        {
            ImageStore.AtomicWrite(_path, JsonSerializer.Serialize(new PresetFile { Presets = _presets }, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class PresetFile
    {
        public List<PresetEntry> Presets { get; set; } = new();
    }
}
