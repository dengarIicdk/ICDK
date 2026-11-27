using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wibe.Core.Dota;
using Wibe.Core.Tools;

namespace Wibe.Core.Econ;

/// <summary>Предмет из scripts/items/items_game.txt (только чтение, файл игры не меняется).</summary>
public sealed record EconItem
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Prefab { get; init; } = string.Empty;

    public string Slot { get; init; } = string.Empty;

    /// <summary>models/items/axe/axe_carnival/axe_carnival_base.vmdl</summary>
    public string? Model { get; init; }

    /// <summary>econ/items/axe/... — путь до картинки в panorama/images.</summary>
    public string? Image { get; init; }

    public string Rarity { get; init; } = string.Empty;

    public string Hero { get; init; } = string.Empty;

    public string StyleName { get; init; } = string.Empty;

    /// <summary>Сколько вариантов оформления (styles 0..N) объявлено у предмета.</summary>
    public int Styles { get; init; } = 1;

    /// <summary>Для prefab «bundle»: имена предметов, которые входят в набор.</summary>
    public List<string> BundleNames { get; init; } = new();

    /// <summary>Модель героя из visuals → asset_modifier «entity_model» (Арканы и Личности).</summary>
    public string? EntityModel { get; init; }

    /// <summary>Прочие модели из visuals (modifier/asset с .vmdl): их надо упаковать вместе с паком.</summary>
    public List<string> ExtraModels { get; init; } = new();

    /// <summary>У предмета есть asset_modifier «persona».</summary>
    public bool Persona { get; init; }

    [JsonIgnore]
    public bool IsDefault => Prefab.Equals("default_item", StringComparison.OrdinalIgnoreCase);

    /// <summary>Путь скомпилированной модели внутри пака игры.</summary>
    [JsonIgnore]
    public string? CompiledModel => Model is null
        ? null
        : Model.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase)
            ? Model + "_c"
            : Model;
}

/// <summary>
/// Каталог косметики: 33 040 предметов items_game.txt, из них ~10 900 с моделью и героем.
/// Разбор потоковый (52 МБ файла), результат кешируется в JSON по buildid.
/// </summary>
public sealed class EconCatalog
{
    private readonly Dictionary<string, List<EconItem>> _byHero;

    private readonly Dictionary<string, EconItem> _byName;

    private readonly Dictionary<string, List<BundleEntry>> _bundlesByHero = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, List<BundleEntry>> _bundlesByItem = new();

    private readonly Dictionary<int, EconItem> _byId = new();

    private EconCatalog(string buildId, IReadOnlyList<EconItem> items, bool fromCache)
    {
        BuildId = buildId;
        Items = items;
        FromCache = fromCache;
        _byHero = new Dictionary<string, List<EconItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.Hero.Length == 0)
            {
                continue;
            }

            if (!_byHero.TryGetValue(item.Hero, out var list))
            {
                list = new List<EconItem>();
                _byHero[item.Hero] = list;
            }

            list.Add(item);
        }

        _byName = new Dictionary<string, EconItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            _byId.TryAdd(item.Id, item);
            if (item.Name.Length > 0)
            {
                _byName.TryAdd(item.Name, item);
            }
        }

        BuildBundles(items);
    }

    /// <summary>
    /// Готовые наборы (prefab bundle): в items_game они ссылаются на предметы по имени.
    /// Разворачиваем в список «id + слот» и привязываем к герою по содержимому.
    /// </summary>
    private void BuildBundles(IReadOnlyList<EconItem> items)
    {
        foreach (var bundle in items)
        {
            if (!string.Equals(bundle.Prefab, "bundle", StringComparison.OrdinalIgnoreCase) || bundle.BundleNames.Count == 0)
            {
                continue;
            }

            var children = new List<BundleItem>();
            foreach (var name in bundle.BundleNames)
            {
                if (!_byName.TryGetValue(name, out var child) || child.IsDefault)
                {
                    continue;
                }

                // An arcana base can carry its model only as entity_model (Drow, PA, WK):
                // it still belongs to the bundle, and the Rust builder places it by item id.
                var model = child.CompiledModel;
                if (model is null && !string.IsNullOrWhiteSpace(child.EntityModel))
                {
                    model = child.EntityModel.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase)
                        ? child.EntityModel + "_c"
                        : child.EntityModel;
                }

                if (model is null)
                {
                    continue;
                }

                children.Add(new BundleItem(child.Id, child.Slot, child.Name, child.Image, child.Rarity, model, child.StyleName));
            }

            if (children.Count == 0)
            {
                continue;
            }

            var heroKey = children
                .Select(c => _byName.GetValueOrDefault(c.Name))
                .Where(c => c is not null && c.Hero.Length > 0)
                .GroupBy(c => c!.Hero, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            if (heroKey is null)
            {
                continue;
            }

            var entry = new BundleEntry(bundle.Id, bundle.Name, bundle.Rarity, bundle.Image, heroKey, children);
            foreach (var child in children)
            {
                if (!_bundlesByItem.TryGetValue(child.Id, out var owners))
                {
                    owners = new List<BundleEntry>();
                    _bundlesByItem[child.Id] = owners;
                }

                owners.Add(entry);
            }

            if (!_bundlesByHero.TryGetValue(heroKey, out var list))
            {
                list = new List<BundleEntry>();
                _bundlesByHero[heroKey] = list;
            }

            list.Add(entry);
        }

        foreach (var list in _bundlesByHero.Values)
        {
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<BundleEntry> BundlesFor(string heroKey) =>
        _bundlesByHero.TryGetValue(heroKey, out var list) ? list : Array.Empty<BundleEntry>();

    /// <summary>Родной комплект предмета: у арканы-головы это весь Phantom Advent и т. п.</summary>
    public BundleEntry? BundleForItem(int itemId, string heroKey) =>
        _bundlesByItem.TryGetValue(itemId, out var owners)
            ? owners
                .Where(b => string.Equals(b.HeroKey, heroKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.Items.Count)
                .FirstOrDefault()
            : null;

    public EconItem? ItemById(int itemId) => _byId.GetValueOrDefault(itemId);

    public string BuildId { get; }

    public IReadOnlyList<EconItem> Items { get; }

    public bool FromCache { get; }

    public IReadOnlyList<string> HeroKeys => _byHero.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "cache", "econ");

    public IReadOnlyList<EconItem> ForHero(string heroKey) =>
        _byHero.TryGetValue(heroKey, out var list) ? list : Array.Empty<EconItem>();

    /// <summary>Стоковые (дефолтные) предметы героя: их модели и есть цели подмены.</summary>
    public IReadOnlyList<EconItem> DefaultsFor(string heroKey) =>
        ForHero(heroKey).Where(i => i.IsDefault && i.CompiledModel is not null).ToList();

    public static EconCatalog Load(DotaInstallation install, bool refresh = false)
    {
        var build = install.BuildId ?? "unknown";
        var cacheFile = Path.Combine(CacheDirectory, $"catalog_v14_{build}.json");

        if (!refresh && File.Exists(cacheFile))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<List<EconItem>>(File.ReadAllBytes(cacheFile));
                if (cached is { Count: > 1000 })
                {
                    return new EconCatalog(build, cached, fromCache: true);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // перечитаем из игры
            }
        }

        var text = ExtractItemsGame(install);
        var items = ItemsGameParser.Parse(text);
        WriteCache(cacheFile, items);
        return new EconCatalog(build, items, fromCache: false);
    }

    private static string ExtractItemsGame(DotaInstallation install)
    {
        Directory.CreateDirectory(CacheDirectory);
        var target = Path.Combine(CacheDirectory, "items_game.txt");
        if (File.Exists(target) && new FileInfo(target).Length > 1_000_000)
        {
            return File.ReadAllText(target, Encoding.UTF8);
        }

        var raw = Path.Combine(CacheDirectory, "raw");
        Directory.CreateDirectory(raw);
        var extracted = Source2ViewerCli.ExtractAsset(install.MainPakDir, "scripts/items/items_game.txt", raw);
        File.Copy(extracted, target, overwrite: true);
        return File.ReadAllText(target, Encoding.UTF8);
    }

    private static void WriteCache(string file, IReadOnlyList<EconItem> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(items, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllBytes(file, json);
        }
        catch (IOException)
        {
            // кеш необязателен
        }
    }
}

/// <summary>Готовый набор: один клик → все предметы раскладываются по своим слотам.</summary>
public sealed record BundleEntry(
    int Id,
    string Name,
    string Rarity,
    string? Image,
    string HeroKey,
    IReadOnlyList<BundleItem> Items);

public sealed record BundleItem(
    int Id,
    string Slot,
    string Name,
    string? Image,
    string Rarity,
    string? Model,
    string Style);

/// <summary>
/// Потоковый разбор KeyValues-файла items_game.txt: держим только путь блоков и нужные поля.
/// Полное дерево в память не тянем — 52 МБ текста превратились бы в сотни мегабайт объектов.
/// </summary>
public static class ItemsGameParser
{
    public static List<EconItem> Parse(string text)
    {
        var result = new List<EconItem>(12_000);
        var path = new List<string>(6);
        string? pendingKey = null;

        ItemBuilder? current = null;
        var styles = new List<(string Name, string Model)>();

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
                if ((path.Count == 5 && path[3] == "styles") ||
                    (path.Count == 6 && path[4] == "styles"))
                {
                    current?.StyleTokens.Add(path[^1]);
                }
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

                if (path.Count >= 3 && path[1] == "items" && current is not null)
                {
                    if (path.Count == 3)
                    {
                        Flush(current, styles, result);
                        current = null;
                        styles.Clear();
                    }
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

            if (path.Count < 2 || path[1] != "items")
            {
                continue;
            }

            if (path.Count == 2)
            {
                // новый предмет: "15323" { ... }
                continue;
            }

            if (path.Count == 3)
            {
                current ??= new ItemBuilder(path[2]);
                ApplyField(current, key, value);
                continue;
            }

            if (path.Count == 4 && path[3] == "used_by_heroes" && key.StartsWith("npc_dota_hero", StringComparison.OrdinalIgnoreCase))
            {
                current?.Heroes.Add(key);
                continue;
            }

            if (path.Count == 4 && path[3] == "bundle")
            {
                current?.BundleNames.Add(key);
                continue;
            }

            if (key == "model_player" &&
                ((path.Count == 5 && path[3] == "styles") ||
                 (path.Count == 6 && path[4] == "styles")))
            {
                styles.Add((path[^1], value));
            }

            if (path.Count == 5 && path[3] == "visuals")
            {
                current?.ApplyVisual(key, value);
            }
        }

        return result;
    }

    private static void Flush(ItemBuilder builder, List<(string Name, string Model)> styles, List<EconItem> output)
    {
        if (string.Equals(builder.Prefab, "bundle", StringComparison.OrdinalIgnoreCase))
        {
            output.Add(new EconItem
            {
                Id = builder.Id,
                Name = builder.Name,
                Prefab = builder.Prefab,
                Slot = builder.Slot,
                Image = builder.Image,
                Rarity = builder.Rarity,
                BundleNames = builder.BundleNames.ToList(),
                Styles = Math.Max(1, builder.StyleTokens.Count),
            });
            return;
        }

        var modifiers = builder.Visuals;
        var entity = modifiers
            .Where(m => string.Equals(m.Type, "entity_model", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.Type, "hero_model_change", StringComparison.OrdinalIgnoreCase))
            .Select(m => NormalizeModel(m.Modifier))
            .FirstOrDefault(m => m.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase));

        var extra = modifiers
            .SelectMany(m => new[] { NormalizeModel(m.Modifier), NormalizeModel(m.Asset) })
            .Where(m => m.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Some arcanas (Monkey King's Great Sage's Reckoning) change the model through
        // several "model" asset modifiers and have no single entity_model. Keep the item
        // visible on the Arcana shelf; the builder patches it by id anyway.
        if (entity is null &&
            builder.Model is null &&
            extra.Count > 0 &&
            builder.Rarity.Contains("arcana", StringComparison.OrdinalIgnoreCase))
        {
            entity = extra[0];
        }

        if ((builder.Model is null && entity is null) || builder.Heroes.Count == 0)
        {
            return;
        }

        output.Add(new EconItem
        {
            Id = builder.Id,
            Name = builder.Name,
            Prefab = builder.Prefab,
            Slot = builder.Slot,
            Model = builder.Model,
            Image = builder.Image,
            Rarity = builder.Rarity,
            Hero = builder.Heroes[0],
            EntityModel = entity,
            ExtraModels = extra,
            Persona = modifiers.Any(m => string.Equals(m.Type, "persona", StringComparison.OrdinalIgnoreCase)),
            Styles = Math.Max(1, builder.StyleTokens.Count),
        });

        foreach (var (styleName, model) in styles)
        {
            if (string.IsNullOrWhiteSpace(model) || !model.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            output.Add(new EconItem
            {
                Id = builder.Id,
                Name = builder.Name,
                Prefab = builder.Prefab,
                Slot = builder.Slot,
                Model = model,
                Image = builder.Image,
                Rarity = builder.Rarity,
                Hero = builder.Heroes[0],
                StyleName = styleName,
                EntityModel = entity,
                ExtraModels = extra,
                Persona = modifiers.Any(m => string.Equals(m.Type, "persona", StringComparison.OrdinalIgnoreCase)),
                Styles = Math.Max(1, builder.StyleTokens.Count),
            });
        }
    }

    private static void ApplyField(ItemBuilder builder, string key, string value)
    {
        switch (key)
        {
            case "name":
                builder.Name = value;
                break;
            case "prefab":
                builder.Prefab = value;
                break;
            case "item_slot":
                builder.Slot = value;
                break;
            case "model_player":
                builder.Model = NormalizeModel(value);
                break;
            case "image_inventory":
                builder.Image = value;
                break;
            case "item_rarity":
                builder.Rarity = value;
                break;
        }
    }

    private static string NormalizeModel(string value) =>
        value.Replace('\\', '/').Trim().ToLowerInvariant();

    /// <summary>Строка вида "key" "value" / "key" token / "key".</summary>
    private static bool TryReadPair(string line, out string key, out string? value)
    {
        key = string.Empty;
        value = null;

        if (line[0] != '"')
        {
            return false;
        }

        var close = FindQuoteEnd(line, 1);
        if (close < 0)
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
            var end = FindQuoteEnd(rest, 1);
            value = end > 0 ? rest[1..end] : rest.Trim('"');
            return true;
        }

        value = rest;
        return true;
    }

    private static int FindQuoteEnd(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class ItemBuilder
    {
        public ItemBuilder(string id)
        {
            Id = int.TryParse(id, out var parsed) ? parsed : 0;
        }

        public int Id { get; }

        public string Name { get; set; } = string.Empty;

        public string Prefab { get; set; } = string.Empty;

        public string Slot { get; set; } = string.Empty;

        public string? Model { get; set; }

        public string? Image { get; set; }

        public string Rarity { get; set; } = string.Empty;

        public List<string> Heroes { get; } = new();

        public List<string> BundleNames { get; } = new();

        public HashSet<string> StyleTokens { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<VisualModifier> Visuals { get; } = new();

        private VisualModifier? _visual;

        public void BeginVisual(string name)
        {
            _visual = new VisualModifier { Name = name };
            Visuals.Add(_visual);
        }

        public void EndVisual() => _visual = null;

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
    }

    private sealed class VisualModifier
    {
        public string Name { get; init; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string Asset { get; set; } = string.Empty;

        public string Modifier { get; set; } = string.Empty;
    }
}
