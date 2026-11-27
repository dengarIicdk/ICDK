using System.Text;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Index;
using Wibe.Core.Mods;
using Wibe.Core.Sets;
using Wibe.Core.Tools;
using Wibe.Core.Vdf;

namespace Wibe.Core.Items;

/// <summary>Результат без-табличной сборки: только готовые ассеты, без scripts/**.</summary>
public sealed record PlainBuildResult(
    bool Success,
    string Message,
    int Files,
    int Models,
    int Effects,
    int Sounds,
    int Icons,
    int BaseFiles,
    int Stubs,
    int Failed,
    IReadOnlyList<RemapOutcome> Remaps,
    IReadOnlySet<string> UsedBases,
    IReadOnlyDictionary<string, byte[]> Entries);

/// <summary>
/// Файловый конструктор косметики: items_game.txt читается только в ОЗУ как справочник,
/// а в VPK попадают исключительно скомпилированные ассеты (models/, particles/, sounds/,
/// panorama/). Ни items_game.txt, ни npc_heroes.txt никогда не упаковываются.
///
/// Для Аркан/Личностей сначала накладывается готовая база hero-base-fixes (тело, детали,
/// эффекты), затем — файлы выбранного предмета по стоковым путям. Несовместимые стоковые
/// слоты глушатся валидной пустой моделью.
/// </summary>
public sealed class PlainFileItemBuilder
{
    private static readonly string[] ForbiddenScripts =
    {
        "scripts/items/items_game.txt",
        "scripts/npc/npc_heroes.txt",
    };

    private const string PortraitsPath = "scripts/npc/portraits.txt";
    private const string ModTexturesPath = "scripts/mod_textures.txt";

    private static readonly HashSet<string> CommonAbilityIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        "blink", "black_king_bar", "force_staff", "cyclone", "sheepstick", "scythe_of_vyse",
        "invis_sword", "shadow_blade", "silver_edge", "ultimate_scepter", "refresher",
        "travel_boots", "phase_boots", "power_treads", "arcane_boots", "guardian_greaves",
    };

    private readonly DotaInstallation _install;
    private readonly PakIndex _index;
    private readonly EconCatalog _catalog;
    private readonly HeroSetService _sets;
    private readonly HeroBaseFixesProvider _bases;

    public PlainFileItemBuilder(
        DotaInstallation install,
        PakIndex index,
        EconCatalog catalog,
        HeroSetService sets,
        HeroBaseFixesProvider? bases = null)
    {
        _install = install;
        _index = index;
        _catalog = catalog;
        _sets = sets;
        _bases = bases ?? new HeroBaseFixesProvider();
    }

    public HeroBaseFixesProvider Bases => _bases;

    public PlainBuildResult Build(IReadOnlyList<HeroPicks> loadout, KeyValuesNode itemsGame, string temp)
    {
        var prepared = new List<(HeroPicks Hero, HeroSetDto Dto)>();
        foreach (var hero in loadout.Where(h => h.Picks.Count > 0))
        {
            var dto = _sets.Get(hero.Folder);
            if (dto is not null)
            {
                prepared.Add((hero, dto));
            }
        }

        if (prepared.Count == 0)
        {
            return new PlainBuildResult(false, "герои не найдены в каталоге", 0, 0, 0, 0, 0, 0, 0, 0,
                Array.Empty<RemapOutcome>(), new HashSet<string>(),
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
        }

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaps = new List<RemapOutcome>();
        var models = 0;
        var effects = 0;
        var sounds = 0;
        var icons = 0;
        var baseFiles = 0;
        var stubs = 0;
        var failed = 0;
        var portraits = new List<PortraitSwap>();
        var sprites = new List<(string Sprite, string As)>();
        byte[]? empty = null;

        byte[]? ReadAsset(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = Normalize(path);
            if (_index.ByPath.GetValueOrDefault(normalized) is null)
            {
                return null;
            }

            try
            {
                var extracted = Source2ViewerCli.ExtractAsset(_install.MainPakDir, normalized, temp);
                return File.ReadAllBytes(extracted);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or FileNotFoundException)
            {
                return null;
            }
        }

        void Put(string path, byte[] data, ref int counter)
        {
            var normalized = Normalize(path);
            if (string.IsNullOrWhiteSpace(normalized) || IsForbidden(normalized))
            {
                return;
            }

            files[normalized] = data;
            counter++;
        }

        // 2b. visuals: эффекты, звуки, иконки, модели. Вызывается и для выбранного предмета,
        // и для всех активных дочерних предметов персоны — без хардкода под героя.
        Dictionary<string, string>? unitModels = null;
        void ApplyVisuals(KeyValuesNode? node, HeroSetDto dto)
        {
            if (node?.Child("visuals") is not { } visuals)
            {
                return;
            }

            foreach (var modifier in Modifiers(visuals))
            {
                var type = (modifier.String("type") ?? string.Empty).Trim().ToLowerInvariant();
                var asset = Normalize(modifier.String("asset") ?? string.Empty);
                var source = Normalize(modifier.String("modifier") ?? string.Empty);
                if (source.Length == 0)
                {
                    continue;
                }

                switch (type)
                {
                    case "entity_model":
                        // Модель тела уже обработана через item.EntityModel / базу. Здесь только
                        // саммоны: unit-модель из npc_units.txt получает модель персоны.
                        if (asset.StartsWith("npc_dota_hero_", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        unitModels ??= UnitModels(ReadAsset);
                        if (unitModels.TryGetValue(asset, out var plainUnit) && plainUnit.Length > 0)
                        {
                            var bytes = ReadAsset(Compiled(source));
                            if (bytes is not null)
                            {
                                Put(Compiled(plainUnit), bytes, ref models);
                            }
                        }

                        break;

                    case "particle":
                        if (asset.Length > 0 && IsUnder(asset, "particles/"))
                        {
                            var bytes = ReadAsset(Compiled(source));
                            if (bytes is not null && !files.ContainsKey(Compiled(asset)))
                            {
                                Put(Compiled(asset), bytes, ref effects);
                            }
                        }

                        break;

                    case "particle_create":
                    case "particle_combined":
                        // mm-core пишет particle_create внутрь модели, а combined требует таблицу.
                        // Копирование их поверх стоковых путей даёт чужие эффекты и loadout-частицы.
                        break;

                    case "sound":
                        if (asset.Length > 0)
                        {
                            var bytes = ReadAsset(Compiled(source));
                            if (bytes is not null && !files.ContainsKey(Compiled(asset)))
                            {
                                Put(Compiled(asset), bytes, ref sounds);
                            }
                        }

                        break;

                    case "ability_icon":
                        if (asset.Length > 0 && !CommonAbilityIcons.Contains(Leaf(asset)))
                        {
                            var from = IconPath(source);
                            var to = IconPath(asset);
                            var bytes = ReadAsset(from);
                            if (bytes is not null && !files.ContainsKey(to))
                            {
                                Put(to, bytes, ref icons);
                            }
                        }

                        break;

                    case "icon_replacement_hero":
                    case "icon_replacement_hero_minimap":
                        if (asset.Length > 0)
                        {
                            var shortName = HeroShort(dto.HeroKey);
                            foreach (var folder in new[] { "heroes/icons", "heroes", "heroes/selection" })
                            {
                                var from = IconPath(source, folder);
                                var to = IconPath("npc_dota_hero_" + shortName, folder);
                                var bytes = ReadAsset(from);
                                if (bytes is not null && !files.ContainsKey(to))
                                {
                                    Put(to, bytes, ref icons);
                                }
                            }

                            // Спрайт миникарты/ХП-бара сидит не в отдельной картинке, а в
                            // scripts/mod_textures.txt: переносим ячейку персоны на ячейку героя.
                            if (type == "icon_replacement_hero_minimap")
                            {
                                sprites.Add(("minimap_heroicon_" + asset, "minimap_heroicon_" + source));
                            }
                        }

                        break;

                    case "model":
                    case "hero_model_change":
                        if (asset.Length > 0)
                        {
                            var bytes = ReadAsset(Compiled(source));
                            if (bytes is not null && !files.ContainsKey(Compiled(asset)))
                            {
                                Put(Compiled(asset), bytes, ref models);
                            }
                        }

                        break;
                }
            }
        }

        // 1. Готовые базы: они уже содержат пересобранные модели и эффекты.
        foreach (var (hero, dto) in prepared)
        {
            foreach (var pick in hero.Picks)
            {
                var fix = HeroBaseFixesProvider.Find(pick.ItemId, dto.HeroKey);
                if (fix is null || usedBases.Contains(fix.Id))
                {
                    continue;
                }

                var (baseEntries, error) = _bases.LoadAsync(fix.Id).GetAwaiter().GetResult();
                if (baseEntries.Count == 0)
                {
                    if (error is not null)
                    {
                        failed++;
                    }

                    continue;
                }

                usedBases.Add(fix.Id);
                foreach (var (path, data) in baseEntries)
                {
                    var normalized = Normalize(path);
                    if (IsForbidden(normalized))
                    {
                        continue;
                    }

                    files[normalized] = data;
                    covered.Add(normalized);
                    baseFiles++;
                }
            }
        }

        // 2. Модели и эффекты выбранных предметов: только по стоковым путям.
        foreach (var (hero, dto) in prepared)
        {
            var chosenIds = hero.Picks.Select(p => p.ItemId).ToHashSet();
            foreach (var pick in hero.Picks)
            {
                var slot = dto.Slots.FirstOrDefault(s => string.Equals(s.Slot, pick.Slot, StringComparison.OrdinalIgnoreCase));
                var item = slot?.Items.FirstOrDefault(i => i.Id == pick.ItemId);
                if (item is null)
                {
                    continue;
                }

                var fix = HeroBaseFixesProvider.Find(pick.ItemId, dto.HeroKey);
                var node = ItemNode(itemsGame, pick.ItemId);
                var heroEntity = item.Entity || (fix is not null);
                var applied = new List<string>();

                // 2a. Тело/деталь предмета.
                var model = !string.IsNullOrWhiteSpace(item.EntityModel) ? item.EntityModel : item.Model;
                if (!string.IsNullOrWhiteSpace(model))
                {
                    if (heroEntity && model.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                    {
                        portraits.Add(new PortraitSwap(model, StripCompiled(dto.BodyTarget)));
                    }

                    var source = Compiled(model);
                    var target = heroEntity ? dto.BodyTarget : slot!.Target;
                    if (fix is null || !covered.Contains(Normalize(target)))
                    {
                        var bytes = ReadAsset(source);
                        if (bytes is not null)
                        {
                            Put(target, bytes, ref models);
                            applied.Add(Normalize(target));
                        }
                        else
                        {
                            failed++;
                        }
                    }
                }

                if (fix is not null)
                {
                    // Арканное оружие/деталь лежат отдельными моделями: кладём их поверх
                    // стоковых путей, иначе меч остаётся взрослым.
                    if (node is not null)
                    {
                        foreach (var asset in CollectModelAssets(node))
                        {
                            var source = Compiled(asset);
                            if (files.ContainsKey(source))
                            {
                                continue;
                            }

                            var bytes = ReadAsset(source);
                            if (bytes is null)
                            {
                                continue;
                            }

                            files[source] = bytes;
                            if (ArcanaPartSlot(asset) is { } partSlot)
                            {
                                var partTarget = (dto.Slots.FirstOrDefault(x =>
                                                      string.Equals(x.Slot, partSlot, StringComparison.OrdinalIgnoreCase)) ??
                                                  dto.Slots.FirstOrDefault(x =>
                                                      string.Equals(x.Slot, "misc", StringComparison.OrdinalIgnoreCase)))?.Target;
                                if (!string.IsNullOrWhiteSpace(partTarget) && !files.ContainsKey(Normalize(partTarget)))
                                {
                                    Put(partTarget, bytes, ref models);
                                    applied.Add(Normalize(partTarget));
                                }
                            }
                        }
                    }
                }

                // 2b. visuals выбранного предмета → эффекты, звуки, иконки.
                ApplyVisuals(node, dto);

                remaps.Add(new RemapOutcome(pick.Slot, pick.ItemId, applied.Count > 0 || fix is not null,
                    fix is not null ? $"база {fix.Id}" : applied.Count > 0 ? "файлы предмета" : "нет ассетов"));
            }
        }

        // 2c. Точная раскладка Личности из item_builder_slots.rs::layout_of: собственные
        // слоты персоны получают стоковых хозяев четырьмя проходами, включая summon.
        foreach (var (hero, dto) in prepared)
        {
            var selectedPersona = 0;
            foreach (var pick in hero.Picks)
            {
                if (PersonaLayoutBuilder.PersonaNumber(ItemNode(itemsGame, pick.ItemId)) > 0)
                {
                    selectedPersona = pick.ItemId;
                    break;
                }
            }

            if (selectedPersona == 0)
            {
                continue;
            }

            var layout = PersonaLayoutBuilder.Build(itemsGame, dto.HeroKey, selectedPersona);
            if (layout is null)
            {
                continue;
            }

            foreach (var part in layout.Parts)
            {
                var pick = hero.Picks.FirstOrDefault(p =>
                    string.Equals(p.Slot, part.Slot, StringComparison.OrdinalIgnoreCase));
                var hasPick = !string.IsNullOrWhiteSpace(pick.Slot);
                KeyValuesNode? active = null;
                if (hasPick)
                {
                    active = ItemNode(itemsGame, pick.ItemId);
                }

                if (active is null && part.GivenId is not null)
                {
                    active = ItemNode(itemsGame, int.Parse(part.GivenId));
                }

                if (active is null)
                {
                    active = Items(itemsGame)?.Children.FirstOrDefault(n =>
                        int.TryParse(n.Key, out _) &&
                        string.Equals(PersonaLayoutBuilder.SlotOf(n), part.Slot, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(n.String("model_player")));
                }

                var model = Normalize(active?.String("model_player") ?? part.GivenModel ?? string.Empty);
                if (model.Length == 0)
                {
                    continue;
                }

                var target = Compiled(part.HostModel);
                // persona_assets из mm-core: база выигрывает везде, кроме собственной модели слота;
                // родная вещь персоны выигрывает у пустой модели базы, но не у пересобранной вручную.
                var baseFile = files.GetValueOrDefault(target);
                var baseIsEmpty = baseFile is { Length: < 8192 } &&
                                  target.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase);
                if (hasPick || baseFile is null || baseIsEmpty)
                {
                    var bytes = ReadAsset(Compiled(model));
                    if (bytes is not null)
                    {
                        Put(target, bytes, ref models);
                    }
                    else
                    {
                        failed++;
                    }
                }

                ApplyVisuals(active, dto);
            }

            // Саммоны персоны (Forge Spirits) не имеют model_player — их модель сидит в
            // entity_model и переносится на стоковую unit-модель тем же ApplyVisuals.
            var suffix = "_persona_" + layout.Number;
            foreach (var item in Items(itemsGame)?.Children ?? (IEnumerable<KeyValuesNode>)Array.Empty<KeyValuesNode>())
            {
                var slot = PersonaLayoutBuilder.SlotOf(item);
                if (slot.EndsWith(suffix, StringComparison.Ordinal))
                {
                    ApplyVisuals(item, dto);
                }
            }
        }

        // 3. Личность/Аркана: глушим оставшиеся стоковые слоты, которых нет в базе.
        foreach (var (hero, dto) in prepared)
        {
            var hasBase = hero.Picks.Any(p => usedBases.Contains(HeroBaseFixesProvider.Find(p.ItemId, dto.HeroKey)?.Id ?? string.Empty));
            if (!hasBase)
            {
                continue;
            }

            empty ??= ReadAsset("models/heroes/tiny/tiny_01/tiny_empty.vmdl_c");
            if (empty is null)
            {
                break;
            }

            var chosenIds = hero.Picks.Select(p => p.ItemId).ToHashSet();
            foreach (var node in Items(itemsGame)?.Children ?? new List<KeyValuesNode>())
            {
                if (!int.TryParse(node.Key, out var nodeId) || chosenIds.Contains(nodeId) || !ForHero(node, dto.HeroKey))
                {
                    continue;
                }

                // Только стоковые default_item: именно их пути движок рисует поверх базы.
                // Эффекты и чужие wearable-предметы здесь трогать нельзя.
                if (!string.Equals(node.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var declared = (node.String("item_slot") ?? string.Empty).Trim().ToLowerInvariant();
                if (declared.Contains("persona", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(declared, "body", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var defaultModel = Normalize(node.String("model_player") ?? string.Empty);
                if (defaultModel.Length == 0)
                {
                    continue;
                }

                var target = Compiled(defaultModel);
                if (files.ContainsKey(target) || covered.Contains(target) ||
                    string.Equals(Normalize(dto.BodyTarget), target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Put(target, empty, ref stubs);
            }
        }

        // 3b. 3D-портрет: блок камеры/света персоны или арканы переносим на стоковую модель.
        if (portraits.Count > 0)
        {
            var original = ReadAsset(PortraitsPath);
            if (original is not null)
            {
                var patched = PortraitTxtPatcher.Apply(Encoding.UTF8.GetString(original), portraits);
                if (patched is not null)
                {
                    files[PortraitsPath] = new UTF8Encoding(false).GetBytes(patched);
                }
            }
        }

        // 3c. Миникарта и иконка над ХП-баром: ячейка спрайт-листа в mod_textures.txt.
        if (sprites.Count > 0)
        {
            var original = ReadAsset(ModTexturesPath);
            if (original is not null)
            {
                var patched = ModTexturesPatcher.Apply(Encoding.UTF8.GetString(original), sprites);
                if (patched is not null)
                {
                    files[ModTexturesPath] = new UTF8Encoding(false).GetBytes(patched);
                }
            }
        }

        // 4. Страховка: скриптов в паке быть не должно.
        foreach (var forbidden in ForbiddenScripts)
        {
            files.Remove(forbidden);
        }

        var message = new StringBuilder();
        message.Append($"файловый мод собран: героев {prepared.Count}, файлов {files.Count}, ");
        message.Append($"моделей {models}, эффектов {effects}, звуков {sounds}, иконок {icons}");
        if (baseFiles > 0)
        {
            message.Append($", base-fixes {baseFiles} ({string.Join(", ", usedBases)})");
        }

        if (stubs > 0)
        {
            message.Append($", заглушек {stubs}");
        }

        if (failed > 0)
        {
            message.Append($", не извлеклось {failed}");
        }

        message.Append(". Без items_game.txt и npc_heroes.txt.");

        return new PlainBuildResult(
            true,
            message.ToString(),
            files.Count,
            models,
            effects,
            sounds,
            icons,
            baseFiles,
            stubs,
            failed,
            remaps,
            usedBases,
            files);
    }

    private Dictionary<string, string> UnitModels(Func<string, byte[]?> read)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = read("scripts/npc/npc_units.txt");
        if (text is null)
        {
            return map;
        }

        try
        {
            var root = KeyValues.Parse(Encoding.UTF8.GetString(text));
            var units = root.Child("DOTAUnits") ?? root.Child("dota_units") ?? root;
            foreach (var child in units.Children)
            {
                var model = child.String("Model") ?? string.Empty;
                if (model.Length > 0)
                {
                    map[child.Key] = model;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("unit map failed: " + ex.Message);
        }

        Console.Error.WriteLine("unit map: " + map.Count);
        return map;
    }

    public static bool IsForbidden(string path)
    {
        var normalized = Normalize(path);
        if (string.Equals(normalized, PortraitsPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, ModTexturesPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ForbiddenScripts.Any(p => string.Equals(normalized, p, StringComparison.OrdinalIgnoreCase)) ||
               normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase);
    }

    private static int PersonaNumber(KeyValuesNode? node)
    {
        var visuals = node?.Child("visuals");
        if (visuals is null)
        {
            return 0;
        }

        foreach (var modifier in Modifiers(visuals))
        {
            if (!string.Equals((modifier.String("type") ?? string.Empty).Trim(), "persona", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse((modifier.String("persona") ?? string.Empty).Trim(), out var number) && number > 0)
            {
                return number;
            }
        }

        return 0;
    }

    private static List<string> DefaultTargets(KeyValuesNode root, HeroSetDto dto)
    {
        var result = new List<string>();
        foreach (var node in Items(root)?.Children ?? new List<KeyValuesNode>())
        {
            if (!ForHero(node, dto.HeroKey) ||
                !string.Equals(node.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var declared = Normalize(node.String("item_slot") ?? string.Empty);
            if (declared.Contains("persona", StringComparison.OrdinalIgnoreCase) || declared == "body")
            {
                continue;
            }

            var target = Compiled(node.String("model_player") ?? string.Empty);
            if (target.Length > 0 && !string.Equals(target, Normalize(dto.BodyTarget), StringComparison.OrdinalIgnoreCase))
            {
                result.Add(target);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string PersonaTarget(HeroSetDto dto, string part, HashSet<string> assigned, List<string> freeTargets)
    {
        var alias = part switch
        {
            "hair" => "head",
            "helmet" => "head",
            "shoulders" => "shoulder",
            "armor" => "armor",
            "bracers" => "arms",
            "offhand" => "offhand_weapon",
            _ => part,
        };

        var exact = dto.Slots.FirstOrDefault(s =>
            string.Equals(s.Slot, alias, StringComparison.OrdinalIgnoreCase));
        if (exact is not null && !string.IsNullOrWhiteSpace(exact.Target))
        {
            return Compiled(exact.Target);
        }

        foreach (var candidate in freeTargets)
        {
            if (!assigned.Contains(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string HeroShort(string heroKey)
    {
        var value = Normalize(heroKey);
        const string prefix = "npc_dota_hero_";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }

    private static bool IsUnder(string path, string root) =>
        Normalize(path).StartsWith(Normalize(root), StringComparison.OrdinalIgnoreCase);

    private static string StripCompiled(string path)
    {
        var value = Normalize(path);
        return value.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? value[..^2] : value;
    }

    private static KeyValuesNode? Items(KeyValuesNode root) =>
        (root.Child("items_game") ?? root).Child("items");

    private static KeyValuesNode? ItemNode(KeyValuesNode root, int id) =>
        Items(root)?.Children.FirstOrDefault(n => int.TryParse(n.Key, out var value) && value == id);

    private static bool ForHero(KeyValuesNode node, string heroKey)
    {
        var heroes = node.Child("used_by_heroes");
        return heroes is null || heroes.Children.Any(c =>
            string.Equals(c.Key, heroKey, StringComparison.OrdinalIgnoreCase) && c.Value != "0");
    }

    private static IEnumerable<KeyValuesNode> Modifiers(KeyValuesNode visuals)
    {
        foreach (var child in visuals.Children)
        {
            if (child.Key.StartsWith("asset_modifier", StringComparison.OrdinalIgnoreCase) && child.IsBlock)
            {
                yield return child;
            }

            if (child.IsBlock)
            {
                foreach (var nested in Modifiers(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static HashSet<string> CollectModelAssets(KeyValuesNode node)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddModel(result, node.String("model_player"));
        if (node.Child("visuals") is { } visuals)
        {
            foreach (var modifier in Modifiers(visuals))
            {
                AddModel(result, modifier.String("modifier"));
                AddModel(result, modifier.String("asset"));
            }
        }

        return result;
    }

    private static void AddModel(HashSet<string> set, string? value)
    {
        var path = Normalize(value ?? string.Empty);
        if (path.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
        {
            set.Add(path);
        }
    }

    private static string? ArcanaPartSlot(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (var slot in new[] { "weapon", "offhand_weapon" })
        {
            if (name.EndsWith("_" + slot + "_arcana", StringComparison.Ordinal))
            {
                return slot;
            }
        }

        return null;
    }

    private static string Compiled(string path)
    {
        var value = Normalize(path);
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? value : value + "_c";
    }

    private static string IconPath(string name, string folder = "spellicons")
    {
        var clean = Normalize(name);
        if (clean.StartsWith("panorama/images/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["panorama/images/".Length..];
            if (!clean.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? clean[..^4] + "_png.vtex_c"
                    : clean + "_png.vtex_c";
            }

            return "panorama/images/" + clean;
        }

        var file = clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? clean[..^4] : clean;
        return $"panorama/images/{folder}/{file}_png.vtex_c";
    }

    private static string Leaf(string path)
    {
        var normalized = Normalize(path);
        return Path.GetFileNameWithoutExtension(normalized);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
}
