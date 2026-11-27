using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.GameInfo;
using Wibe.Core.Index;
using Wibe.Core.Items;
using Wibe.Core.Sets;
using Wibe.Core.Steam;
using Wibe.Core.Tools;
using Wibe.Core.Vdf;
using Wibe.Core.Vpk;

namespace Wibe.Core.Mods;

public sealed record HeroPicks(
    string Folder,
    IReadOnlyList<(string Slot, int ItemId)> Picks,
    IReadOnlyDictionary<string, int>? Styles = null,
    bool RareTaunt = false);

public sealed record BuildOutcome(
    bool Success,
    string Message,
    int Files,
    long VpkSize,
    IReadOnlyList<RemapOutcome> Remaps);

/// <summary>
/// Сессия мода: кладёт VPK (items_game.txt + подменённые модели) свободным слотом в ту папку,
/// которую движок реально монтирует, — <c>game/dota_&lt;AudioLanguage&gt;</c> (обычно dota_russian).
/// game/dota/gameinfo.gi не трогается вообще, поэтому проверка координатора проходит.
/// <c>-language custommod</c> с 24.07.2026 не создаёт search path: движок монтирует только известные языки.
/// </summary>
public sealed class ModSession
{
    private readonly DotaInstallation _install;
    private readonly PakIndex _index;
    private readonly EconCatalog _catalog;
    private readonly HeroSetService _sets;
    private readonly GameInfoPatcher _gameInfo;

    public ModSession(DotaInstallation install, PakIndex index, EconCatalog catalog, HeroSetService sets)
    {
        _install = install;
        _index = index;
        _catalog = catalog;
        _sets = sets;
        _gameInfo = new GameInfoPatcher(install);
    }

    public static string SessionPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WibeChanger", "session.json");

    public GameInfoPatcher GameInfo => _gameInfo;

    /// <summary>Папка, которую Source 2 монтирует: game/dota_&lt;язык озвучки&gt;.</summary>
    public string ModFolderPath => _install.ModFolderPath;

    /// <summary>Имя пака мода в языковой папке (pak02…pak99, кроме pak01 и pak64).</summary>
    public string ModPakName => LoadState()?.PakFile ?? NextFreePakName();

    public string ModVpkPath => Path.Combine(ModFolderPath, ModPakName);

    /// <summary>Слот, число файлов и предметов для героя из текущей сессии; null — героя нет в паке.</summary>
    public (int Slot, int Files, int Picks)? CompiledInfo(string folder)
    {
        var state = LoadState();
        if (state is null || !File.Exists(ModVpkPath))
        {
            return null;
        }

        var hero = state.Heroes.FirstOrDefault(h => string.Equals(h.Folder, folder, StringComparison.OrdinalIgnoreCase));
        var picks = hero?.Picks ?? (string.Equals(state.Folder, folder, StringComparison.OrdinalIgnoreCase) ? state.Picks : null);
        if (picks is null || picks.Count == 0)
        {
            return null;
        }

        return (PakNumber(state.PakFile), state.Files, picks.Count);
    }

    /// <summary>Герои, которые реально лежат в текущем собранном паке (а не в старом журнале).</summary>
    public IReadOnlySet<string> CompiledHeroes
    {
        get
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var state = LoadState();
            if (state is null || !File.Exists(ModVpkPath))
            {
                return set;
            }

            foreach (var hero in state.Heroes)
            {
                if (hero.Picks.Count > 0)
                {
                    set.Add(hero.Folder);
                }
            }

            if (state.Folder is { Length: > 0 } folder && state.Picks.Count > 0)
            {
                set.Add(folder);
            }

            return set;
        }
    }

    public bool IsActive
    {
        get
        {
            var state = LoadState();
            return state?.PakFile is { Length: > 0 } file && File.Exists(Path.Combine(ModFolderPath, file));
        }
    }

    public SessionState? LoadState()
    {
        try
        {
            return File.Exists(SessionPath)
                ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(SessionPath))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Новый файловый режим: items_game.txt читается только в память, в пак идут только
    /// скомпилированные ассеты. Всегда пишет pak06_dir.vpk в языковую папку, без тега MOD.
    /// </summary>
    public BuildOutcome BuildPlainAll(IReadOnlyList<HeroPicks> loadout)
    {
        if (DotaInstallation.IsGameRunning())
        {
            return new BuildOutcome(false, "Dota 2 запущена: закрой игру и повтори", 0, 0, Array.Empty<RemapOutcome>());
        }

        var configured = loadout.Where(h => h.Picks.Count > 0).ToList();
        if (configured.Count == 0)
        {
            return new BuildOutcome(false, "ничего не выбрано: настрой хотя бы одного героя", 0, 0, Array.Empty<RemapOutcome>());
        }

        _gameInfo.RestoreIfStale();
        CleanupLegacyLanguageFolder();
        var folderError = EnsureMountFolder();
        if (folderError is not null)
        {
            return new BuildOutcome(false, folderError, 0, 0, Array.Empty<RemapOutcome>());
        }

        var itemsGamePath = Path.Combine(EconCatalog.CacheDirectory, "items_game.txt");
        if (!File.Exists(itemsGamePath))
        {
            return new BuildOutcome(false, "нет локальной копии items_game.txt — открой каталог ещё раз", 0, 0, Array.Empty<RemapOutcome>());
        }

        var temp = Path.Combine(Path.GetTempPath(), "wibe-plain-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var root = KeyValues.ParseFile(itemsGamePath);
            var builder = new PlainFileItemBuilder(_install, _index, _catalog, _sets);
            var result = builder.Build(configured, root, temp);
            if (!result.Success)
            {
                return new BuildOutcome(false, result.Message, 0, 0, Array.Empty<RemapOutcome>());
            }

            // Сборщик отдаёт только ассеты; запись пака — единственное место, где они ещё в памяти.
            return WritePlain(result, configured);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            return new BuildOutcome(false, "файловая сборка не удалась: " + ex.Message, 0, 0, Array.Empty<RemapOutcome>());
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Записывает сессию после сборки внешним Rust-ядром: пак уже лежит в языковой папке,
    /// поэтому остаётся только запомнить слот, файлы и пики для UI/декомпиляции.
    /// </summary>
    public BuildOutcome RegisterExternalBuild(IReadOnlyList<HeroPicks> configured, string pakName, int files, long size)
    {
        var configuredList = configured.Where(h => h.Picks.Count > 0).ToList();
        if (configuredList.Count == 0)
        {
            return new BuildOutcome(false, "примерка пуста: сначала выбери скины героям", 0, 0, Array.Empty<RemapOutcome>());
        }

        _gameInfo.RestoreIfStale();
        CleanupLegacyLanguageFolder();
        var folderError = EnsureMountFolder();
        if (folderError is not null)
        {
            return new BuildOutcome(false, folderError, 0, 0, Array.Empty<RemapOutcome>());
        }

        try
        {
            var journal = ModJournal.Load();
            if (journal.Mods.Count > 0)
            {
                journal.Mods.Clear();
                journal.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var dtoByFolder = configuredList
            .Select(h => (Hero: h, Dto: _sets.Get(h.Folder)))
            .Where(x => x.Dto is not null)
            .ToList();

        SaveState(new SessionState
        {
            Folder = dtoByFolder.Count > 0 ? dtoByFolder[0].Hero.Folder : configuredList[0].Folder,
            HeroKey = dtoByFolder.Count > 0 ? dtoByFolder[0].Dto!.HeroKey : string.Empty,
            Picks = configuredList[0].Picks.Select(p => new PickState { Slot = p.Slot, ItemId = p.ItemId }).ToList(),
            Heroes = dtoByFolder.Select(x => new HeroState
            {
                Folder = x.Hero.Folder,
                HeroKey = x.Dto!.HeroKey,
                Picks = x.Hero.Picks.Select(p => new PickState { Slot = p.Slot, ItemId = p.ItemId }).ToList(),
            }).ToList(),
            Files = files,
            VpkSize = size,
            PakFile = pakName,
            AllPaks = new List<string> { pakName },
            BuiltAt = DateTimeOffset.UtcNow,
        });

        return new BuildOutcome(
            true,
            $"готово: {configuredList.Count} гер., {files} файлов, {size / 1024 / 1024.0:0.0} МБ — Rust-ядро",
            files,
            size,
            Array.Empty<RemapOutcome>());
    }

    private BuildOutcome WritePlain(PlainBuildResult result, IReadOnlyList<HeroPicks> configured)
    {
        const string pakName = "pak06_dir.vpk";
        var pakPath = Path.Combine(ModFolderPath, pakName);
        var previous = LoadState()?.PakFile;

        VpkWriter.Write(pakPath, result.Entries);
        var size = new FileInfo(pakPath).Length;

        if (previous is { Length: > 0 } && !string.Equals(previous, pakName, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(Path.Combine(ModFolderPath, previous));
        }

        _gameInfo.RestoreIfStale();

        try
        {
            var journal = ModJournal.Load();
            if (journal.Mods.Count > 0)
            {
                journal.Mods.Clear();
                journal.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var dtoByFolder = configured
            .Select(h => (Hero: h, Dto: _sets.Get(h.Folder)))
            .Where(x => x.Dto is not null)
            .ToList();

        SaveState(new SessionState
        {
            Folder = dtoByFolder.Count > 0 ? dtoByFolder[0].Hero.Folder : configured[0].Folder,
            HeroKey = dtoByFolder.Count > 0 ? dtoByFolder[0].Dto!.HeroKey : string.Empty,
            Picks = configured[0].Picks.Select(p => new PickState { Slot = p.Slot, ItemId = p.ItemId }).ToList(),
            Heroes = dtoByFolder.Select(x => new HeroState
            {
                Folder = x.Hero.Folder,
                HeroKey = x.Dto!.HeroKey,
                Picks = x.Hero.Picks.Select(p => new PickState { Slot = p.Slot, ItemId = p.ItemId }).ToList(),
            }).ToList(),
            Files = result.Entries.Count,
            VpkSize = size,
            PakFile = pakName,
            AllPaks = new List<string> { pakName },
            BuiltAt = DateTimeOffset.UtcNow,
        });

        return new BuildOutcome(
            true,
            result.Message + $" Пак: {_install.ModFolderName}/{pakName} ({size / (1024d * 1024d):0.0} МБ)",
            result.Entries.Count,
            size,
            result.Remaps);
    }

    public BuildOutcome Build(string folder, IReadOnlyList<(string Slot, int ItemId)> picks) =>
        BuildAll(new[] { new HeroPicks(folder, picks) });

    /// <summary>
    /// Одна компиляция на всех настроенных героев: общий items_game.txt и все модели в одном паке.
    /// </summary>
    public BuildOutcome BuildAll(IReadOnlyList<HeroPicks> loadout)
    {
        if (DotaInstallation.IsGameRunning())
        {
            return new BuildOutcome(false, "Dota 2 запущена: закрой игру и повтори", 0, 0, Array.Empty<RemapOutcome>());
        }

        var configured = loadout.Where(h => h.Picks.Count > 0).ToList();
        if (configured.Count == 0)
        {
            return new BuildOutcome(false, "ничего не выбрано: настрой хотя бы одного героя", 0, 0, Array.Empty<RemapOutcome>());
        }

        // Легаси-патч gameinfo.gi (от старых версий) обязан быть убран: иначе координатор
        // блокирует поиск матча.
        _gameInfo.RestoreIfStale();
        CleanupLegacyLanguageFolder();

        var folderError = EnsureMountFolder();
        if (folderError is not null)
        {
            return new BuildOutcome(false, folderError, 0, 0, Array.Empty<RemapOutcome>());
        }

        var prepared = new List<(HeroPicks Hero, HeroSetDto Dto)>();
        foreach (var hero in configured)
        {
            var found = _sets.Get(hero.Folder);
            if (found is not null)
            {
                prepared.Add((hero, found));
            }
        }

        if (prepared.Count == 0)
        {
            return new BuildOutcome(false, "герои не найдены в каталоге", 0, 0, Array.Empty<RemapOutcome>());
        }

        var temp = Path.Combine(Path.GetTempPath(), "wibe-build-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(temp);

        try
        {
            // 1. Патчим items_game.txt: visuals/model_player/иконка базовых предметов.
            var itemsGamePath = Path.Combine(EconCatalog.CacheDirectory, "items_game.txt");
            if (!File.Exists(itemsGamePath))
            {
                return new BuildOutcome(false, "нет локальной копии items_game.txt — открой каталог ещё раз", 0, 0, Array.Empty<RemapOutcome>());
            }

            var root = KeyValues.ParseFile(itemsGamePath);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var remaps = new List<ItemRemap>();

            foreach (var (hero, dto) in prepared)
            {
                foreach (var pick in hero.Picks)
                {
                    var slot = dto.Slots.FirstOrDefault(s => string.Equals(s.Slot, pick.Slot, StringComparison.OrdinalIgnoreCase));
                    var item = slot?.Items.FirstOrDefault(i => i.Id == pick.ItemId);

                    // Обычное тело меняется только моделью; Аркане/Личности нужен перенос visuals
                    // (entity_model, persona, particles), иначе новая модель без эффектов и анимаций.
                    var heroEntity = item?.Entity == true && IsHeroEntity(item);
                    if (string.Equals(pick.Slot, "body", StringComparison.OrdinalIgnoreCase) && !heroEntity)
                    {
                        continue;
                    }

                    remaps.Add(new ItemRemap(dto.HeroKey, pick.Slot, pick.ItemId, item?.Name ?? pick.ItemId.ToString()));

                    // Аркана с нативным слотом (спина/оружие) дополнительно ставится на тело:
                    // визуал и модель героя не должны зависеть от надетого предмета.
                    if (heroEntity && !string.Equals(pick.Slot, "body", StringComparison.OrdinalIgnoreCase))
                    {
                        remaps.Add(new ItemRemap(dto.HeroKey, "body", pick.ItemId, item!.Name));
                    }
                }
            }

            var outcomes = ItemsGamePatcher.Apply(root, remaps);

            // Модель героя для scripts/npc/heroes/*.txt: Личности и Арканы должны спавниться
            // сразу с нужным скелетом, иначе поверх детской модели появляется взрослый герой.
            var heroModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var personaMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (hero, dto) in prepared)
            {
                var entityPick = hero.Picks
                    .Select(p => new
                    {
                        Pick = p,
                        Slot = dto.Slots.FirstOrDefault(x => string.Equals(x.Slot, p.Slot, StringComparison.OrdinalIgnoreCase)),
                    })
                    .Select(x => new
                    {
                        x.Pick,
                        x.Slot,
                        Item = x.Slot?.Items.FirstOrDefault(i => i.Id == x.Pick.ItemId),
                    })
                    .FirstOrDefault(x => x.Item?.Entity == true);

                if (entityPick?.Item is null || string.IsNullOrWhiteSpace(entityPick.Item.Model))
                {
                    continue;
                }

                if (!IsHeroEntity(entityPick.Item))
                {
                    continue;
                }

                heroModels[hero.Folder] = string.IsNullOrWhiteSpace(entityPick.Item.EntityModel)
                    ? entityPick.Item.Model
                    : entityPick.Item.EntityModel;

                // Только настоящие Личности: у Аркан тоже бывают *_persona_1 слоты,
                // но их нельзя чистить/подменять, иначе арканная экипировка исчезнет.
                if (entityPick.Item.Persona)
                {
                    personaMaps[dto.HeroKey] = PersonaParts(dto);
                }
            }

            string? Extract(string path)
            {
                if (_index.ByPath.GetValueOrDefault(path) is null)
                {
                    return null;
                }

                try
                {
                    return Source2ViewerCli.ExtractAsset(_install.MainPakDir, path, temp);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                {
                    return null;
                }
            }

            byte[]? Read(string path)
            {
                var extracted = Extract(path);
                return extracted is null ? null : File.ReadAllBytes(extracted);
            }

            // 2. Модели: каждый выбранный предмет кладём и по своему пути, и поверх стоковой
            //    модели слота — так скин виден даже если координатор надел стоковый инвентарь.
            var applied = 0;
            var failed = 0;
            var bodies = 0;
            var entities = 0;
            var stubs = 0;
            var customStubs = 0;
            var personaCount = 0;
            byte[]? emptyStub = null;

            foreach (var (hero, dto) in prepared)
            {
                var picks = hero.Picks
                    .Select(p => new
                    {
                        Pick = p,
                        Slot = dto.Slots.FirstOrDefault(x => string.Equals(x.Slot, p.Slot, StringComparison.OrdinalIgnoreCase)),
                    })
                    .Select(x => new { x.Pick, x.Slot, Item = x.Slot?.Items.FirstOrDefault(i => i.Id == x.Pick.ItemId) })
                    .Where(x => x.Slot is not null && x.Item is not null)
                    .ToList();

                foreach (var entry in picks)
                {
                    var item = entry.Item!;
                    var slot = entry.Slot!;
                    if (string.IsNullOrWhiteSpace(item.Model))
                    {
                        continue;
                    }

                    var heroEntity = item.Entity && IsHeroEntity(item);
                    // У Арканы model_player — это деталь слота (спина/оружие), а entity_model —
                    // вся модель героя. На стоковое тело кладём именно entity_model.
                    var source = heroEntity && !string.IsNullOrWhiteSpace(item.EntityModel)
                        ? (item.EntityModel.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? item.EntityModel : item.EntityModel + "_c")
                        : item.Model;

                    var bytes = Read(source);
                    if (bytes is null)
                    {
                        continue;
                    }

                    // Аркана/Личность с чужим нативным слотом (head, back) — это всё равно
                    // модель всего героя: кладём её на стоковое тело, а не в слот головы.
                    var target = heroEntity &&
                                 !string.Equals(slot.Slot, "body", StringComparison.OrdinalIgnoreCase)
                        ? dto.BodyTarget
                        : slot.Target;

                    files[target] = bytes;
                    applied++;

                    // Родная деталь арканы (model_player) — поверх стокового слота,
                    // чтобы вместо взрослой спины/оружия осталась арканная.
                    if (heroEntity &&
                        !string.Equals(slot.Target, target, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(item.Model))
                    {
                        var partBytes = Read(item.Model);
                        if (partBytes is not null)
                        {
                            files[slot.Target] = partBytes;
                            applied++;
                        }
                    }

                    // Оригинальный путь модели — для предметов с несколькими ссылками.
                    if (!string.Equals(target, source, StringComparison.OrdinalIgnoreCase) &&
                        !files.ContainsKey(source))
                    {
                        files[source] = bytes;
                    }

                    if (string.Equals(slot.Slot, "body", StringComparison.OrdinalIgnoreCase))
                    {
                        bodies++;
                    }

                    if (item.Entity)
                    {
                        entities++;

                        foreach (var asset in ItemsGamePatcher.CollectModelAssets(root, entry.Pick.ItemId))
                        {
                            var compiled = asset.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? asset : asset + "_c";
                            if (files.ContainsKey(compiled))
                            {
                                continue;
                            }

                            var extra = Read(compiled);
                            if (extra is null)
                            {
                                failed++;
                                continue;
                            }

                            files[compiled] = extra;
                            applied++;

                            // Оружие Арканы лежит отдельной моделью (…_weapon_arcana).
                            // Кладём его поверх стокового оружия героя, иначе меч остаётся
                            // стоковым или пропадает.
                            if (ArcanaPartSlot(asset) is { } partSlot)
                            {
                                var partTarget = (dto.Slots
                                                     .FirstOrDefault(x => string.Equals(x.Slot, partSlot, StringComparison.OrdinalIgnoreCase)) ??
                                                 dto.Slots.FirstOrDefault(x => string.Equals(x.Slot, "misc", StringComparison.OrdinalIgnoreCase)))
                                    ?.Target;
                                if (!string.IsNullOrWhiteSpace(partTarget) && !files.ContainsKey(partTarget))
                                {
                                    files[partTarget] = extra;
                                    applied++;
                                }
                            }
                        }
                    }
                }

                if (!personaMaps.TryGetValue(dto.HeroKey, out var personaParts))
                {
                    continue;
                }

                personaCount++;

                // 2a. Родные детали Личности ставим поверх взрослых стоковых путей:
                //     голова, руки, спина, плечи, оружие и т.д. теперь физически детские.
                foreach (var (baseSlot, partTarget) in personaParts)
                {
                    var mainSlot = dto.Slots.FirstOrDefault(x => string.Equals(x.Slot, baseSlot, StringComparison.OrdinalIgnoreCase));
                    if (mainSlot is null || files.ContainsKey(mainSlot.Target))
                    {
                        continue;
                    }

                    var partBytes = Read(partTarget);
                    if (partBytes is null)
                    {
                        continue;
                    }

                    files[mainSlot.Target] = partBytes;
                    applied++;
                }

                // 2b. Слоты без персонажного аналога (пояс, ноги, взрослое оружие) глушим
                //     пустой моделью, чтобы взрослая одежда не торчала сквозь Личность.
                foreach (var slot in dto.Slots)
                {
                    if (slot.Slot.Contains("persona", StringComparison.OrdinalIgnoreCase) ||
                        !IsEquipSlot(slot.Slot) ||
                        string.Equals(slot.Slot, "body", StringComparison.OrdinalIgnoreCase) ||
                        files.ContainsKey(slot.Target))
                    {
                        continue;
                    }

                    emptyStub ??= Read("models/heroes/tiny/tiny_01/tiny_empty.vmdl_c");
                    if (emptyStub is null)
                    {
                        break;
                    }

                    files[slot.Target] = emptyStub;
                    stubs++;
                }

                // 2c. Всё, что надето на аккаунте сверх нашей сборки (кастомные головы,
                //     плащи, оружие), тоже глушим: иначе поверх Личности/Арканы остаётся
                //     взрослая деталь — ровно то, что видно на скриншотах.
                if (heroModels.ContainsKey(hero.Folder))
                {
                    var chosenIds = picks.Select(x => x.Pick.ItemId).ToHashSet();
                    foreach (var node in root.Child("items_game")?.Child("items")?.Children ?? new List<KeyValuesNode>())
                    {
                        if (!int.TryParse(node.Key, out var nodeId) || chosenIds.Contains(nodeId))
                        {
                            continue;
                        }

                        if (string.Equals(node.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase) ||
                            !NodeForHero(node, dto.HeroKey))
                        {
                            continue;
                        }

                        var declared = (node.String("item_slot") ?? string.Empty).Trim().ToLowerInvariant();
                        if (!IsEquipSlot(declared))
                        {
                            continue;
                        }

                        var paths = ItemsGamePatcher.CollectModelAssets(root, nodeId);
                        if (!string.IsNullOrWhiteSpace(node.String("model_player")))
                        {
                            paths = paths.Append(node.String("model_player")!).ToList();
                        }

                        foreach (var asset in paths)
                        {
                            var compiled = asset.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? asset : asset + "_c";
                            if (files.ContainsKey(compiled))
                            {
                                continue;
                            }

                            emptyStub ??= Read("models/heroes/tiny/tiny_01/tiny_empty.vmdl_c");
                            if (emptyStub is null)
                            {
                                break;
                            }

                            files[compiled] = emptyStub;
                            customStubs++;
                        }
                    }
                }
            }

            // 3. npc_heroes: у Личностей/Аркан Model героя = модель личности/арканы.
            var heroFiles = 0;
            foreach (var (folder, model) in heroModels)
            {
                var npcPath = $"scripts/npc/heroes/npc_dota_hero_{folder}.txt";
                var extracted = Extract(npcPath);
                if (extracted is null)
                {
                    failed++;
                    continue;
                }

                try
                {
                    var heroRoot = KeyValues.ParseFile(extracted);
                    if (HeroModelPatcher.Apply(heroRoot, "npc_dota_hero_" + folder, model))
                    {
                        files[npcPath] = new UTF8Encoding(false).GetBytes(KeyValuesWriter.Serialize(heroRoot));
                        heroFiles++;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    failed++;
                }
            }

            foreach (var (heroKey, parts) in personaMaps)
            {
                ItemsGamePatcher.ApplyPersonaParts(root, heroKey, parts);
            }

            files["scripts/items/items_game.txt"] = new UTF8Encoding(false).GetBytes(KeyValuesWriter.Serialize(root));

            // 3. Пишем VPK свободным слотом в языковую папку, старый пак мода удаляем.
            var previous = LoadState()?.PakFile;
            var pakName = PickFreePakName(previous);
            var pakPath = Path.Combine(ModFolderPath, pakName);
            VpkWriter.Write(pakPath, files);
            var size = new FileInfo(pakPath).Length;

            if (previous is { Length: > 0 } && !string.Equals(previous, pakName, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(Path.Combine(ModFolderPath, previous));
            }

            // 4. game/dota/gameinfo.gi не трогаем: мод подключается языковой папкой.
            _gameInfo.RestoreIfStale();

            // Журнал старых сборок больше не отражает реальность: единственный источник правды — состояние сессии.
            try
            {
                var journal = ModJournal.Load();
                if (journal.Mods.Count > 0)
                {
                    journal.Mods.Clear();
                    journal.Save();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            var previousState = LoadState();
            var allPaks = (previousState?.AllPaks ?? new List<string>()).ToList();
            if (!allPaks.Contains(pakName, StringComparer.OrdinalIgnoreCase))
            {
                allPaks.Add(pakName);
            }

            SaveState(new SessionState
            {
                Folder = prepared[0].Hero.Folder,
                HeroKey = prepared[0].Dto.HeroKey,
                Picks = prepared[0].Hero.Picks.Select(p => new PickState { Slot = p.Slot, ItemId = p.ItemId }).ToList(),
                Heroes = prepared.Select(p => new HeroState
                {
                    Folder = p.Hero.Folder,
                    HeroKey = p.Dto.HeroKey,
                    Picks = p.Hero.Picks.Select(x => new PickState { Slot = x.Slot, ItemId = x.ItemId }).ToList(),
                }).ToList(),
                Files = files.Count,
                VpkSize = size,
                PakFile = pakName,
                AllPaks = allPaks,
                BuiltAt = DateTimeOffset.UtcNow,
            });

            var remapNote = outcomes.Any(o => o.Applied)
                ? $", visuals изменены у {outcomes.Count(o => o.Applied)} слотов"
                : string.Empty;

            return new BuildOutcome(
                true,
                $"мод собран: героев {prepared.Count}, файлов {files.Count}, {size / (1024d * 1024d):0.0} МБ, моделей {applied}{(bodies > 0 ? $" (тел {bodies})" : string.Empty)}{(entities > 0 ? $", аркан/личностей {entities}" : string.Empty)}{(personaCount > 0 ? $", персонажей {personaCount}, заглушек {stubs}" : string.Empty)}{(customStubs > 0 ? $", скрыто чужих предметов {customStubs}" : string.Empty)}{(heroFiles > 0 ? $", npc-файлов {heroFiles}" : string.Empty)}{(failed > 0 ? $", не извлеклось {failed}" : string.Empty)}{remapNote}. Пак: {_install.ModFolderName}/{pakName}",
                files.Count,
                size,
                outcomes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            return new BuildOutcome(false, ex.Message, 0, 0, Array.Empty<RemapOutcome>());
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool NodeForHero(KeyValuesNode node, string heroKey)
    {
        var heroes = node.Child("used_by_heroes");
        return heroes is null ||
               heroes.Children.Any(c => string.Equals(c.Key, heroKey, StringComparison.OrdinalIgnoreCase) && c.Value != "0");
    }

    private static bool IsHeroEntity(SetItemDto item)
    {
        if (!item.Entity)
        {
            return false;
        }

        if (item.Persona)
        {
            return true;
        }

        var slot = item.NativeSlot.ToLowerInvariant();
        var model = ((item.EntityModel ?? string.Empty).Length > 0 ? item.EntityModel : item.Model ?? string.Empty)
            .ToLowerInvariant();
        return slot is "hero_base" or "persona_selector" or "persona" or "body_head" or "body" ||
               model.Contains("_arcana") || model.Contains("/arcana/");
    }

    private static Dictionary<string, string> PersonaParts(HeroSetDto dto)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in dto.Slots)
        {
            var name = slot.Slot.ToLowerInvariant();
            var marker = name.IndexOf("_persona", StringComparison.Ordinal);
            if (marker <= 0)
            {
                continue;
            }

            var baseSlot = name[..marker];
            if (baseSlot.Length > 0)
            {
                map.TryAdd(baseSlot, slot.Target);
            }
        }

        return map;
    }

    private static string? ArcanaPartSlot(string modelPath)
    {
        var name = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
        foreach (var slot in new[] { "weapon", "offhand_weapon" })
        {
            if (name.EndsWith("_" + slot + "_arcana", StringComparison.Ordinal))
            {
                return slot;
            }
        }

        return null;
    }

    private static bool IsEquipSlot(string slot) => slot.ToLowerInvariant() is
        "head" or "hair" or "armor" or "shoulder" or "shoulders" or "arms" or "legs" or "belt" or "back" or
        "neck" or "gloves" or "weapon" or "offhand_weapon" or "offhand_weapon_1" or "tail" or "mount" or "misc";

    public (bool Success, string Message) Revert()
    {
        var removed = 0;
        try
        {
            var state = LoadState();
            var paks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (state?.PakFile is { Length: > 0 } current)
            {
                paks.Add(current);
            }

            foreach (var pak in state?.AllPaks ?? new List<string>())
            {
                if (pak.Length > 0)
                {
                    paks.Add(pak);
                }
            }

            // Наши паки живут в свободных слотах 02..99. В стоковой игре есть только pak01 и pak64,
            // поэтому всё остальное в языковой папке — наш мод (в т.ч. остатки старых версий).
            if (Directory.Exists(ModFolderPath))
            {
                foreach (var file in Directory.EnumerateFiles(ModFolderPath, "pak*_dir.vpk"))
                {
                    var number = PakNumber(Path.GetFileName(file));
                    if (number <= 1 || number == 64)
                    {
                        continue;
                    }

                    paks.Add(Path.GetFileName(file));
                }
            }

            foreach (var pak in paks)
            {
                var path = Path.Combine(ModFolderPath, pak);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }
        }
        catch (IOException ex)
        {
            return (false, "не удалось удалить пак: " + ex.Message);
        }

        removed += CleanupLegacyLanguageFolder();
        var restored = _gameInfo.Restore();
        ClearState();

        try
        {
            var journal = ModJournal.Load();
            if (journal.Mods.Count > 0)
            {
                journal.Mods.Clear();
                journal.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (!restored.Success)
        {
            return (false, restored.Message);
        }

        return (true, removed > 0
            ? $"моды сняты: удалено паков {removed}, игра вернулась к стоковому состоянию"
            : "мод уже снят");
    }

    /// <summary>
    /// Запуск без <c>-language custommod</c>, <c>-override_vpk</c> и <c>-enable_addons</c>:
    /// мод лежит в папке языка озвучки, которую движок монтирует сам.
    /// <paramref name="conDebug"/> добавляет <c>-condebug</c> (лог game/dota/console.log).
    /// </summary>
    /// <summary>
    /// Флаги запуска Dota 2. Здесь нет и не должно быть ничего, что сбрасывает управление:
    /// ни автоконфига, ни режима инструментов, ни exec-скриптов, ни правки дистанции камеры.
    /// </summary>
    public static string LaunchArguments(bool conDebug = false) =>
        "-novid -language danish" + (conDebug ? " -condebug" : string.Empty);

    public (bool Success, string Message) Launch(bool dryRun = false, bool conDebug = false)
    {
        var launchArgs = LaunchArguments(conDebug);
        var steam = FindSteam();
        try
        {
            if (steam is not null)
            {
                var arguments = $"-applaunch 570 {launchArgs}";
                if (dryRun)
                {
                    return (true, $"steam.exe {arguments}");
                }

                Process.Start(new ProcessStartInfo(steam, arguments) { UseShellExecute = true });
                return (true, $"Dota 2 запускается через Steam: мод в {_install.ModFolderName}/{ModPakName}");
            }

            var exe = Path.Combine(_install.GameDir, "game", "bin", "win64", "dota2.exe");
            if (File.Exists(exe))
            {
                if (dryRun)
                {
                    return (true, $"dota2.exe {launchArgs}");
                }

                Process.Start(new ProcessStartInfo(exe, launchArgs) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                return (true, $"Dota 2 запускается напрямую: мод в {_install.ModFolderName}/{ModPakName}");
            }

            return (false, "не нашёл ни Steam, ни dota2.exe");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    public sealed record MountReport(
        bool Ok,
        string PakName,
        int PakNumber,
        string MountFolder,
        string LogPath,
        string? CommandLine,
        IReadOnlyList<string> MountedPaks,
        string Message);

    /// <summary>Проверка по console.log: смонтировал ли Source 2 наш пак в языковой папке.</summary>
    public MountReport CheckMount()
    {
        var pakName = ModPakName;
        var pakNumber = PakNumber(pakName);
        var logPath = Path.Combine(_install.GameDir, "game", "dota", "console.log");
        if (!File.Exists(ModVpkPath))
        {
            return new MountReport(false, pakName, pakNumber, ModFolderPath, logPath, null,
                Array.Empty<string>(), "нашего пака в языковой папке нет — мод не собран или снят");
        }

        if (!File.Exists(logPath))
        {
            return new MountReport(false, pakName, pakNumber, ModFolderPath, logPath, null,
                Array.Empty<string>(), "console.log не найден — запусти игру с -condebug и проверь снова");
        }

        string[] lines;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        }
        catch (IOException ex)
        {
            return new MountReport(false, pakName, pakNumber, ModFolderPath, logPath, null,
                Array.Empty<string>(), "console.log занят: " + ex.Message);
        }

        var command = lines.FirstOrDefault(l =>
            l.Contains("[CommandLine]", StringComparison.OrdinalIgnoreCase) &&
            l.Contains("-steam", StringComparison.OrdinalIgnoreCase));

        var folderMarker = Path.DirectorySeparatorChar + "game" + Path.DirectorySeparatorChar + _install.ModFolderName + Path.DirectorySeparatorChar;
        var expected = $"pak{pakNumber:00}.vpk";
        var mounted = lines
            .Where(l => l.Contains("(vpk)", StringComparison.OrdinalIgnoreCase) &&
                        l.Contains(folderMarker, StringComparison.OrdinalIgnoreCase))
            .Select(l => l[(l.LastIndexOf(Path.DirectorySeparatorChar) + 1)..].Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var found = mounted.Contains(expected, StringComparer.OrdinalIgnoreCase);
        return new MountReport(found, pakName, pakNumber, ModFolderPath, logPath, command, mounted,
            found
                ? $"Source 2 смонтировал {_install.ModFolderName}/{expected} — скин должен быть в игре"
                : $"в console.log нет {_install.ModFolderName}/{expected}: мод не примонтирован");
    }

    /// <summary>
    /// Фон: если от старой версии остался патч gameinfo.gi и игра закрыта — чистим.
    /// </summary>
    public async Task MonitorAsync(CancellationToken token)
    {
        var wasRunning = DotaInstallation.IsGameRunning();
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            var running = DotaInstallation.IsGameRunning();
            if (wasRunning && !running)
            {
                _gameInfo.RestoreIfStale();
            }

            wasRunning = running;
        }
    }

    private static int PakNumber(string pakName) =>
        int.TryParse(new string(pakName.Where(char.IsDigit).Take(2).ToArray()), out var number) ? number : 0;

    private string? EnsureMountFolder()
    {
        try
        {
            Directory.CreateDirectory(ModFolderPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "не удалось открыть языковую папку " + ModFolderPath + ": " + ex.Message;
        }
    }

    private string NextFreePakName() => PickFreePakName(null);

    private string PickFreePakName(string? previous)
    {
        var used = new HashSet<int>();
        foreach (var file in Directory.EnumerateFiles(ModFolderPath, "pak*_dir.vpk", SearchOption.TopDirectoryOnly))
        {
            var number = PakNumber(Path.GetFileName(file));
            if (number > 0)
            {
                used.Add(number);
            }
        }

        if (previous is { Length: > 0 })
        {
            used.Remove(PakNumber(previous));
        }

        for (var number = 2; number <= 99; number++)
        {
            if (number != 64 && !used.Contains(number))
            {
                return $"pak{number:00}_dir.vpk";
            }
        }

        throw new InvalidOperationException("в языковой папке нет свободного слота pak02…pak99");
    }

    private int CleanupLegacyLanguageFolder()
    {
        var removed = 0;
        var legacy = Path.Combine(_install.GameDir, "game", GameInfoPatcher.ModFolderName);
        foreach (var file in new[]
                 {
                     Path.Combine(legacy, "pak01_dir.vpk"),
                     Path.Combine(legacy, "gameinfo.gi"),
                 })
        {
            if (File.Exists(file) && TryDelete(file))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? FindSteam()
    {
        // 1. Реестр (HKCU/HKLM SteamPath) — работает и когда Steam стоит не в Program Files.
        var root = SteamLocator.FindSteamRoot();
        if (!string.IsNullOrWhiteSpace(root))
        {
            var fromRegistry = Path.Combine(root, "steam.exe");
            if (File.Exists(fromRegistry))
            {
                return fromRegistry;
            }
        }

        // 2. Стандартные места.
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     @"C:\Program Files (x86)",
                     @"C:\Program Files",
                 })
        {
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                continue;
            }

            var candidate = Path.Combine(programFiles, "Steam", "steam.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void SaveState(SessionState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ClearState()
    {
        try
        {
            if (File.Exists(SessionPath))
            {
                File.Delete(SessionPath);
            }
        }
        catch (IOException)
        {
        }
    }

    public sealed class SessionState
    {
        public string Folder { get; set; } = string.Empty;

        public string HeroKey { get; set; } = string.Empty;

        public List<PickState> Picks { get; set; } = new();

        public List<HeroState> Heroes { get; set; } = new();

        public List<string> AllPaks { get; set; } = new();

        public int Files { get; set; }

        public long VpkSize { get; set; }

        public string? PakFile { get; set; }

        public DateTimeOffset BuiltAt { get; set; }
    }

    public sealed class HeroState
    {
        public string Folder { get; set; } = string.Empty;

        public string HeroKey { get; set; } = string.Empty;

        public List<PickState> Picks { get; set; } = new();
    }

    public sealed class PickState
    {
        public string Slot { get; set; } = string.Empty;

        public int ItemId { get; set; }
    }
}
