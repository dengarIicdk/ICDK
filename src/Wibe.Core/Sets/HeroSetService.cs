using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Index;
using System.Text.Json;
using Wibe.Core.Mods;

namespace Wibe.Core.Sets;

public sealed record SetItemDto(
    int Id,
    string Name,
    string Rarity,
    string Model,
    string Style,
    string? Image,
    bool Entity = false,
    bool Persona = false,
    string NativeSlot = "",
    string EntityModel = "",
    int Styles = 1);

public sealed record SetSlotDto(string Slot, string Label, string Target, IReadOnlyList<SetItemDto> Items);

public sealed record BundlePickDto(string Slot, int ItemId, string Name, string? Image = null, string NativeSlot = "");

public sealed record ShelfItemDto(
    int Id,
    string Name,
    string Rarity,
    string? Image,
    string Slot,
    string? Target,
    string? Style,
    IReadOnlyList<BundlePickDto>? Bundle,
    int Styles = 1,
    bool Persona = false,
    int PersonaIndex = 0,
    IReadOnlyList<BundlePickDto>? Parts = null);

public sealed record ShelfDto(string Id, string Title, string Kind, IReadOnlyList<ShelfItemDto> Items);

public sealed record HeroSetDto(
    string HeroKey,
    string Folder,
    string Name,
    string BodyTarget,
    string ItemFolder,
    IReadOnlyList<SetSlotDto> Slots,
    IReadOnlyList<ShelfDto> Shelves,
    InstalledMod? Installed);

/// <summary>
/// Сборка «своего сета»: слоты героя (тело, оружие, голова…) → модель-сток служит целью подмены,
/// модель выбранной косметики — содержимым пака.
/// Каталог слотов считается один раз на старте, поэтому /api/hero отвечает мгновенно.
/// </summary>
public sealed class HeroSetService
{
    // Папки скриншотов не всегда совпадают с именем героя в items_game.
    private static readonly Dictionary<string, string> HeroAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["natures_prophet"] = "furion",
        ["outworld_destroyer"] = "obsidian_destroyer",
        // игра так и не переименовала npc-ключ вслед за панорамой
        ["windranger"] = "windrunner",
    };

    private static readonly Dictionary<string, string> SlotLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["body"] = "Тело",
        ["weapon"] = "Оружие",
        ["offhand_weapon"] = "Вторая рука",
        ["head"] = "Голова",
        ["hair"] = "Волосы",
        ["armor"] = "Доспех",
        ["shoulder"] = "Плечи",
        ["shoulders"] = "Плечи",
        ["arms"] = "Руки",
        ["legs"] = "Ноги",
        ["belt"] = "Пояс",
        ["back"] = "Спина",
        ["neck"] = "Шея",
        ["misc"] = "Прочее",
        ["taunt"] = "Насмешка",
        ["courier"] = "Курьер",
        ["ward"] = "Вард",
        ["persona"] = "Личность",
        ["mount"] = "Ездовое",
        ["tail"] = "Хвост",
        ["gloves"] = "Перчатки",
    };

    private readonly DotaInstallation _install;
    private readonly PakIndex _index;
    private readonly EconCatalog _catalog;
    private readonly ModInstaller _installer;
    private readonly Dictionary<string, HeroSetDto> _prepared = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Папка героя в каталоге не всегда совпадает с его именем в items_game: модели Wraith King
    /// лежат в models/items/wraith_king, а сам он npc_dota_hero_skeleton_king (то же с wisp).
    /// Пару «папка → mp-ключ» знает каталог, по путям моделей, поэтому имя берётся оттуда.
    /// </summary>
    private Dictionary<string, string>? _itemHeroes;

    public string BuildId => _install.BuildId ?? "unknown";

    /// <summary>Полный кеш каталога: при следующем запуске герои открываются без сканов и композиции.</summary>
    public bool TryLoadCache(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var payload = JsonSerializer.Deserialize<CatalogCache>(File.ReadAllBytes(path));
            if (payload is null ||
                payload.Version < 13 ||
                !string.Equals(payload.BuildId, BuildId, StringComparison.Ordinal) ||
                payload.Heroes.Count < 50)
            {
                return false;
            }

            foreach (var hero in payload.Heroes)
            {
                _prepared[hero.Folder] = hero;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }

    public void SaveCache(string path)
    {
        try
        {
            var payload = new CatalogCache
            {
                Version = 13,
                BuildId = BuildId,
                GeneratedAt = DateTimeOffset.UtcNow,
                Heroes = _prepared.Values
                    .Select(h => h with { Installed = null })
                    .OrderBy(h => h.Folder, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(payload, AppJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly JsonSerializerOptions AppJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class CatalogCache
    {
        public int Version { get; set; }

        public string BuildId { get; set; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; set; }

        public List<HeroSetDto> Heroes { get; set; } = new();
    }

    public HeroSetService(DotaInstallation install, PakIndex index, EconCatalog catalog)
    {
        _install = install;
        _index = index;
        _catalog = catalog;
        _installer = new ModInstaller(install);
    }

    public IReadOnlyCollection<string> PreparedFolders => _prepared.Keys;

    /// <summary>Один проход по индексу пака + каталогу: все герои и слоты готовы заранее.</summary>
    public void PrepareAll()
    {
        var bodies = CollectBodies();

        var folders = new HashSet<string>(_catalog.HeroKeys.Select(Short), StringComparer.OrdinalIgnoreCase);
        foreach (var folder in bodies.Keys)
        {
            folders.Add(folder);
        }

        foreach (var folder in folders)
        {
            try
            {
                _prepared[folder] = Compose(folder, bodies.GetValueOrDefault(folder) ?? new List<EconItem>());
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
            }
        }
    }

    public HeroSetDto? Get(string folder)
    {
        var shortName = Short(folder);
        if (!_prepared.TryGetValue(shortName, out var ready))
        {
            var composed = Compose(shortName, CollectBodies(shortName).GetValueOrDefault(shortName) ?? new List<EconItem>());
            _prepared[shortName] = composed;
            ready = composed;
        }

        var installed = _installer.Journal.Mods.FirstOrDefault(m =>
            string.Equals(m.HeroId, shortName, StringComparison.OrdinalIgnoreCase) &&
            m.Kind is "hero.body" or "hero.slot" or "hero.set");

        return ready with { Installed = installed };
    }

    /// <summary>Тела героев из индекса пака: models/items/&lt;hero&gt;/&lt;set&gt;/*_base.vmdl_c.</summary>
    private Dictionary<string, List<EconItem>> CollectBodies(string? onlyHero = null)
    {
        var result = new Dictionary<string, List<EconItem>>(StringComparer.OrdinalIgnoreCase);
        var prefix = onlyHero is null ? "models/items/" : $"models/items/{onlyHero}/";

        foreach (var entry in _index.Entries)
        {
            if (!entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !entry.Path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Path.GetFileNameWithoutExtension(entry.Path).Contains("_base", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = entry.Path["models/items/".Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                continue;
            }

            var hero = rest[..slash];
            var itemFolder = $"models/items/{hero}/";
            var setFolder = SetFolderOf(entry.Path, itemFolder);

            var item = new EconItem
            {
                Id = BodyId(entry.Path),
                Name = SetNameOf(setFolder),
                Model = entry.Path[..^2],
                Hero = "npc_dota_hero_" + hero,
            };

            if (!result.TryGetValue(hero, out var list))
            {
                list = new List<EconItem>();
                result[hero] = list;
            }

            list.Add(item);
        }

        foreach (var list in result.Values)
        {
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        return result;
    }

    /// <summary>The mp-key of a hero by its item folder, the way the catalog names it.</summary>
    public string HeroKeyFor(string dotaName)
    {
        // Папки в панораме и кропы названы новым именем, а npc-ключ остался старым.
        if (NameAliases.TryGetValue(dotaName, out var alias))
        {
            dotaName = alias;
        }

        var guessed = "npc_dota_hero_" + dotaName;
        if (_catalog.ForHero(guessed).Count > 0)
        {
            return guessed;
        }

        var map = _itemHeroes ??= ItemHeroes();
        return map.TryGetValue(dotaName, out var real) ? real : guessed;
    }

    /// <summary>Папка/имя героя → npc-ключ там, где игра его так и не переименовала.</summary>
    private static readonly Dictionary<string, string> NameAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["windranger"] = "windrunner" };

    /// <summary>Item folder → hero key, from the models the catalog carries under models/items/&lt;folder&gt;.</summary>
    private Dictionary<string, string> ItemHeroes()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _catalog.Items)
        {
            var model = item.Model;
            if (model is null || !model.StartsWith("models/items/", StringComparison.OrdinalIgnoreCase) || item.Hero.Length == 0)
            {
                continue;
            }

            var rest = model["models/items/".Length..];
            var slash = rest.IndexOf('/');
            if (slash > 0)
            {
                map.TryAdd(rest[..slash], item.Hero);
            }
        }

        return map;
    }

    private HeroSetDto Compose(string folder, List<EconItem> bodies)
    {
        var shortName = Short(folder);
        var dotaName = HeroAliases.TryGetValue(shortName, out var alias) ? alias : shortName;
        var heroKey = HeroKeyFor(dotaName);
        var bodyTarget = $"models/heroes/{dotaName}/{dotaName}.vmdl_c";
        var itemFolder = $"models/items/{dotaName}/";

        var items = _catalog.ForHero(heroKey);
        var slots = new List<SetSlotDto>();

        // Арканы и Личности задают модель героя через entity_model, а не через model_player,
        // поэтому в каталоге у них нет Model — их тоже кладём в слот «Тело».
        var specials = items
            .Where(i => i.EntityModel is not null &&
                        !string.IsNullOrWhiteSpace(i.EntityModel) &&
                        string.Equals(EffectiveSlot(i), "body", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .ToList();

        if (bodies.Count > 0 || specials.Count > 0)
        {
            var bodyItems = bodies.Select(ToDto)
                .Concat(specials.Select(ToDto))
                .GroupBy(i => i.Id)
                .Select(g => g.First())
                .OrderByDescending(i => Rank(i.Rarity))
                .ToList();
            slots.Add(new SetSlotDto("body", Label("body"), bodyTarget, bodyItems));
        }

        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in _catalog.DefaultsFor(heroKey))
        {
            if (def.CompiledModel is null || !usedTargets.Add(def.CompiledModel))
            {
                continue;
            }

            var slotName = EffectiveSlot(def);
            var variants = items
                .Where(i => !i.IsDefault &&
                            i.CompiledModel is not null &&
                            string.Equals(EffectiveSlot(i), slotName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(i => i.Id)
                .Select(g => g.First())
                .OrderByDescending(i => _index.ByPath.TryGetValue(i.CompiledModel!, out var e) ? e.Size : 0)
                .Select(ToDto)
                .ToList();

            if (variants.Count == 0)
            {
                continue;
            }

            slots.Add(new SetSlotDto(slotName, Label(slotName), def.CompiledModel, variants));
        }

        var shelves = BuildShelves(heroKey, slots);
        var installed = _installer.Journal.Mods.FirstOrDefault(m =>
            string.Equals(m.HeroId, shortName, StringComparison.OrdinalIgnoreCase) &&
            m.Kind is "hero.body" or "hero.slot" or "hero.set");

        return new HeroSetDto(heroKey, shortName, Prettify(shortName), bodyTarget, itemFolder, slots, shelves, installed);
    }

    /// <summary>
    /// Полки для экрана героя: готовые сеты → Arcana/личности → Immortal → остальные по слотам.
    /// Один и тот же предмет не повторяется на нижних полках, чтобы список не пух.
    /// </summary>
    /// <summary>items_game sometimes points a bundle at the empty-slot placeholder instead of real art.</summary>
    private static bool IsUsableArt(string? image) =>
        !string.IsNullOrWhiteSpace(image)
        && !image.Contains("testitem_slot_empty", StringComparison.OrdinalIgnoreCase)
        && !image.EndsWith("/empty", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<ShelfDto> BuildShelves(string heroKey, IReadOnlyList<SetSlotDto> slots)
    {
        var shelves = new List<ShelfDto>();
        var used = new HashSet<int>();
        var byId = new Dictionary<int, (SetSlotDto Slot, SetItemDto Item)>();
        foreach (var slot in slots)
        {
            foreach (var item in slot.Items)
            {
                byId.TryAdd(item.Id, (slot, item));
            }
        }

        // 1. Готовые наборы из items_game (prefab bundle).
        var bundles = new List<ShelfItemDto>();
        foreach (var bundle in _catalog.BundlesFor(heroKey))
        {
            var picks = new List<BundlePickDto>();
            foreach (var child in bundle.Items)
            {
                if (!byId.TryGetValue(child.Id, out var hit))
                {
                    continue;
                }

                picks.Add(new BundlePickDto(hit.Slot.Slot, child.Id, child.Name, child.Image));
            }

            // «Готовый сет» — это комплект из нескольких частей. Одиночные клинки,
            // браслеты и паки оружия живут на своих слотовых полках, а не здесь.
            var distinctSlots = picks.Select(p => p.Slot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (picks.Count < 3 && !(distinctSlots.Count == 1 && distinctSlots[0] == "body"))
            {
                continue;
            }

            // Обложка сета — собственный широкий арт бандла (econ/sets/...),
            // и только если его нет, крупный предмет: голова/оружие/тело.
            var main = picks.FirstOrDefault(p => p.Slot is "head" or "body_head" or "weapon" or "body");
            var cover = IsUsableArt(bundle.Image)
                ? bundle.Image
                : (!string.IsNullOrWhiteSpace(main?.Image)
                    ? main!.Image
                    : picks.Select(p => p.Image).FirstOrDefault(img => IsUsableArt(img)));

            var bundleStyles = picks
                .Select(p => byId.TryGetValue(p.ItemId, out var hit)
                    ? Math.Max(hit.Item.Styles, HeroBaseFixesProvider.Find(p.ItemId, heroKey)?.Styles ?? 1)
                    : 1)
                .DefaultIfEmpty(1)
                .Max();
            bundles.Add(new ShelfItemDto(
                bundle.Id,
                bundle.Name,
                bundle.Rarity,
                cover,
                "set",
                null,
                null,
                picks,
                bundleStyles));
        }

        if (bundles.Count > 0)
        {
            shelves.Add(new ShelfDto("sets", "Готовые сеты", "sets", bundles.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        // 2. Arcana и личности.
        var arcana = slots
            .SelectMany(s => s.Items.Where(i => IsArcana(i) || IsPersona(i, s.Slot)).Select(i => (Slot: s, Item: i)))
            .GroupBy(x => x.Item.Id)
            .Select(g => g.First())
            .ToList();

        if (arcana.Count > 0)
        {
            foreach (var entry in arcana)
            {
                used.Add(entry.Item.Id);
            }

            shelves.Add(new ShelfDto("arcana", "Arcana и личности", "arcana", arcana
                .OrderByDescending(x => Rank(x.Item.Rarity))
                .Select(x => ToShelf(heroKey, x.Slot, x.Item, slots))
                .ToList()));
        }

        // 3. Immortal.
        var immortal = slots
            .Where(s => !string.Equals(s.Slot, "body", StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Items.Where(i => IsImmortal(i) && !used.Contains(i.Id)).Select(i => (Slot: s, Item: i)))
            .GroupBy(x => x.Item.Id)
            .Select(g => g.First())
            .ToList();

        if (immortal.Count > 0)
        {
            foreach (var entry in immortal)
            {
                used.Add(entry.Item.Id);
            }

            shelves.Add(new ShelfDto("immortal", "Immortal", "immortal", immortal
                .OrderByDescending(x => Rank(x.Item.Rarity))
                .Select(x => ToShelf(heroKey, x.Slot, x.Item, slots))
                .ToList()));
        }

        // 4. Остальное по слотам, от редких к обычным.
        foreach (var slot in slots)
        {
            var rest = slot.Items
                .Where(i => !used.Contains(i.Id))
                .OrderByDescending(i => Rank(i.Rarity))
                .ThenByDescending(i => i.Model.Length)
                .Select(i => ToShelf(heroKey, slot, i, slots))
                .ToList();

            if (rest.Count == 0)
            {
                continue;
            }

            var id = slot.Slot == "body" ? "slot:body" : "slot:" + slot.Slot;
            shelves.Add(new ShelfDto(id, slot.Label, "slot", rest));
        }

        return shelves;
    }

    private ShelfItemDto ToShelf(string heroKey, SetSlotDto slot, SetItemDto item, IReadOnlyList<SetSlotDto> slots)
    {
        IReadOnlyList<BundlePickDto>? parts = null;
        if (IsArcana(item))
        {
            var bundle = _catalog.BundleForItem(item.Id, heroKey);
            if (bundle is not null)
            {
                var list = new List<BundlePickDto>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in bundle.Items)
                {
                    var econ = _catalog.ItemById(child.Id);
                    if (econ is null || econ.CompiledModel is null && econ.EntityModel is null)
                    {
                        continue;
                    }

                    var childSlot = EffectiveSlot(econ);
                    if (!slots.Any(s => string.Equals(s.Slot, childSlot, StringComparison.OrdinalIgnoreCase)) || !seen.Add(childSlot))
                    {
                        continue;
                    }

                    list.Add(new BundlePickDto(childSlot, child.Id, child.Name, child.Image, econ.Slot));
                }

                if (list.Count > 1)
                {
                    parts = list;
                }
            }
        }

        var index = 0;
        var at = item.NativeSlot.IndexOf("_persona_", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            _ = int.TryParse(item.NativeSlot[(at + 9)..], out index);
        }

        return new ShelfItemDto(
            item.Id,
            item.Name,
            item.Rarity,
            item.Image,
            slot.Slot,
            slot.Target,
            item.Style,
            null,
            item.Styles,
            item.Persona || index > 0,
            index,
            parts);
    }

    /// <summary>Слоты, которые не носят на теле героя: варды, способности, звуки.</summary>
    private static readonly HashSet<string> NonWearableSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "ability", "ability2", "ability3", "ability4", "ability_effects", "ambient_effects",
        "ward", "healing_ward", "courier", "announcer", "music", "hud", "loading_screen",
        "taunt", "voice", "terrain", "emblem", "weather", "misc", "effect", "effects",
    };

    /// <summary>Аркана — только настоящее качество arcana и только носимая вещь.</summary>
    private static bool IsArcana(SetItemDto item)
    {
        var rarity = item.Rarity.Replace("rarity_", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!rarity.Equals("arcana", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !item.Entity && !NonWearableSlots.Contains(item.NativeSlot);
    }

    private static bool IsImmortal(SetItemDto item) =>
        item.Rarity.Contains("immortal", StringComparison.OrdinalIgnoreCase);

    /// <summary>Личность — только предмет с собственной моделью/слотом личности.</summary>
    private static bool IsPersona(SetItemDto item, string slot) =>
        item.Persona
        || string.Equals(slot, "persona", StringComparison.OrdinalIgnoreCase)
        || (item.NativeSlot.StartsWith("persona", StringComparison.OrdinalIgnoreCase)
            && !NonWearableSlots.Contains(item.NativeSlot));

    private static int Rank(string rarity)
    {
        var value = rarity.ToLowerInvariant();
        if (value.Contains("immortal")) return 7;
        if (value.Contains("arcana")) return 6;
        if (value.Contains("ancient")) return 6;
        if (value.Contains("legendary")) return 5;
        if (value.Contains("mythical")) return 4;
        if (value.Contains("rare")) return 3;
        if (value.Contains("uncommon")) return 2;
        if (value.Contains("common")) return 1;
        return 0;
    }

    public InstallResult InstallBody(string folder, int itemId)
    {
        var folderName = Short(folder);
        var dotaName = HeroAliases.TryGetValue(folderName, out var alias) ? alias : folderName;
        var item = _catalog.ForHero(HeroKeyFor(dotaName))
            .FirstOrDefault(i => i.Id == itemId && i.CompiledModel is not null);

        if (item is null)
        {
            var body = Get(folder)?.Slots.FirstOrDefault(s => s.Slot == "body")?.Items.FirstOrDefault(i => i.Id == itemId);
            if (body is null)
            {
                return new InstallResult(false, "такой предмет не найден в каталоге");
            }

            var target = $"models/heroes/{dotaName}/{dotaName}.vmdl_c";
            return ReplaceHeroMod(folder, new[] { (target, body.Model) }, $"Тело · {body.Name}");
        }

        var stock = $"models/heroes/{dotaName}/{dotaName}.vmdl_c";
        return ReplaceHeroMod(folder, new[] { (stock, item.CompiledModel!) }, $"Тело · {item.Name}");
    }

    /// <summary>picks: пары «слот → id предмета».</summary>
    public InstallResult InstallSet(string folder, IReadOnlyList<(string Slot, int ItemId)> picks)
    {
        if (picks.Count == 0)
        {
            return new InstallResult(false, "ничего не выбрано");
        }

        var dto = Get(folder);
        if (dto is null)
        {
            return new InstallResult(false, "герой не найден");
        }

        var files = new List<(string Target, string Source)>();
        foreach (var pick in picks)
        {
            var slot = dto.Slots.FirstOrDefault(s => string.Equals(s.Slot, pick.Slot, StringComparison.OrdinalIgnoreCase));
            var item = slot?.Items.FirstOrDefault(i => i.Id == pick.ItemId);
            if (slot is null || item is null)
            {
                continue;
            }

            files.Add((slot.Target, item.Model));
        }

        if (files.Count == 0)
        {
            return new InstallResult(false, "выбранные предметы не подходят этому герою");
        }

        var title = picks.Count == 1
            ? $"{dto.Name} · {picks[0].Slot}"
            : $"{dto.Name} · свой сет ({files.Count})";

        return ReplaceHeroMod(folder, files, title);
    }

    public UninstallResult Uninstall(string folder)
    {
        var shortName = Short(folder);
        var mods = _installer.Journal.Mods
            .Where(m => string.Equals(m.HeroId, shortName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mods.Count == 0)
        {
            return new UninstallResult(false, "на этого героя ничего не установлено");
        }

        var last = new UninstallResult(true, "снято");
        foreach (var mod in mods)
        {
            last = _installer.Uninstall(mod.Slot);
        }

        return last;
    }

    private InstallResult ReplaceHeroMod(string folder, IReadOnlyList<(string Target, string Source)> files, string title)
    {
        var shortName = Short(folder);
        foreach (var existing in _installer.Journal.Mods
                     .Where(m => string.Equals(m.HeroId, shortName, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            var removed = _installer.Uninstall(existing.Slot);
            if (!removed.Success)
            {
                return new InstallResult(false, removed.Message);
            }
        }

        return _installer.InstallFiles(files, title, kind: files.Count > 1 ? "hero.set" : "hero.slot", heroId: shortName);
    }

    private static string SetFolderOf(string modelPath, string itemFolder)
    {
        if (!modelPath.StartsWith(itemFolder, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var rest = modelPath[itemFolder.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : string.Empty;
    }

    private static string SetNameOf(string setFolder) =>
        setFolder.Length == 0 ? "Тело героя" : Prettify(setFolder.Replace("__", "_"));

    private static int BodyId(string path)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in path)
            {
                hash = hash * 31 + c;
            }

            return -Math.Abs(hash % 1_000_000) - 1;
        }
    }

    private static string EffectiveSlot(EconItem item)
    {
        var declared = item.Slot.Trim().ToLowerInvariant();
        if (declared is "hero_base" or "persona_selector" or "persona" ||
            declared.StartsWith("body", StringComparison.Ordinal))
        {
            return "body";
        }

        if (!string.IsNullOrWhiteSpace(item.EntityModel))
        {
            return "body";
        }

        if (!string.IsNullOrWhiteSpace(declared))
        {
            return declared;
        }

        // A wearable with no item_slot is the game's weapon: items_game leaves the slot out for
        // most old scythes, swords and axes, and the builder places them by item id the same way.
        var inferred = item.CompiledModel is null ? "misc" : InferSlot(item.CompiledModel);
        if (inferred == "misc" &&
            item.Prefab is "wearable" or "default_item")
        {
            return "weapon";
        }

        return inferred;
    }

    private static string InferSlot(string target)
    {
        var file = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        foreach (var slot in new[]
                 {
                     "offhand_weapon", "weapon", "head", "armor", "belt", "back", "tail", "mount", "legs", "arms",
                     "shoulder", "neck", "gloves", "hair",
                 })
        {
            if (file.Contains(slot, StringComparison.Ordinal))
            {
                return slot;
            }
        }

        return "misc";
    }

    private static SetItemDto ToDto(EconItem item)
    {
        var model = item.CompiledModel;
        if (model is null && !string.IsNullOrWhiteSpace(item.EntityModel))
        {
            var entity = item.EntityModel!;
            model = entity.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? entity : entity + "_c";
        }

        return new SetItemDto(
            item.Id,
            item.Name,
            item.Rarity,
            model ?? string.Empty,
            item.StyleName,
            item.Image,
            item.EntityModel is not null,
            item.Persona,
            item.Slot,
            item.EntityModel ?? string.Empty,
            Math.Max(item.Styles, HeroBaseFixesProvider.Find(item.Id, item.Hero)?.Styles ?? 1));
    }

    private static string Label(string slot) => SlotLabels.TryGetValue(slot, out var label) ? label : slot;

    private static string Short(string folder) => folder.StartsWith("npc_dota_hero", StringComparison.OrdinalIgnoreCase)
        ? folder["npc_dota_hero_".Length..]
        : folder;

    private static string Prettify(string folder)
    {
        var words = folder.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
