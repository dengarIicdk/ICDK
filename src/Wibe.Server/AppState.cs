using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.GameInfo;
using Wibe.Core.Econ;
using Wibe.Core.Index;
using Wibe.Core.Mods;
using Wibe.Core.Sets;
using Wibe.Core.World;

namespace Wibe.Server;

public sealed record HeroEntry(
    string Folder,
    string Name,
    string Target,
    string? ModTitle,
    bool HasMod,
    int Slot,
    string Portrait,
    string Crop);

public sealed record StateDto(
    bool Ready,
    string Status,
    string? Error,
    string? BuildId,
    string? ModFolder,
    bool ModFolderReady,
    bool GameInfoClean,
    string Pak,
    bool GameRunning,
    int ModsCount,
    int[] OccupiedSlots,
    string Version,
    string? GameDir,
    string? DotaDir,
    bool DotaManual);

/// <summary>
/// Один экземпляр на процесс: установка, индекс пака, каталог косметики, картинки, установщик.
/// Инициализация идёт в фоне, чтобы окно/страница открывались сразу.
/// </summary>
public sealed class AppState
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _installLock = new();
    private CancellationTokenSource _monitor = new();

    public static AppState Instance { get; } = new();

    public AppState()
    {
        Loadout = new LoadoutStore(ImageStore.LoadoutPath);
        Presets = new PresetStore(ImageStore.PresetsPath);
    }

    public DotaInstallation? Install { get; private set; }

    public PakIndex? Index { get; private set; }

    public EconCatalog? Catalog { get; private set; }

    public WorldCosmeticsCatalog? WorldCatalog { get; private set; }

    public HeroSetService? Sets { get; private set; }

    public ModSession? Session { get; private set; }

    public IconJob? Icons { get; private set; }

    public LoadoutStore Loadout { get; }

    public PresetStore Presets { get; }

    private static readonly Lazy<HashSet<string>> ArtResources = new(() =>
    {
        var names = typeof(AppState).Assembly.GetManifestResourceNames();
        return new HashSet<string>(names.Select(n => n.Replace('\\', '.')), StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>Есть ли локальный вырез из скриншотов игрока для карточки героя или личности.</summary>
    private static bool HasArt(string kind, string name)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "www", "art", kind, name + ".jpg");
        if (File.Exists(file))
        {
            return true;
        }

        return ArtResources.Value.Contains($"www.art.{kind}.{name}.jpg");
    }

    public string SessionNote { get; private set; } = "мод не собран";

    public string Status { get; private set; } = "ищу Dota 2…";

    public string? Error { get; private set; }

    public bool Ready { get; private set; }

    public List<HeroEntry> Heroes { get; } = new();

    public SemaphoreSlim InstallGate { get; } = new(1, 1);

    public Task WhenReady => _ready.Task;

    public object DotaInfo()
    {
        var install = Install;
        return new
        {
            gameDir = install?.GameDir,
            dotaDir = install?.DotaDir,
            modFolder = install?.ModFolderPath,
            buildId = install?.BuildId,
            audioLanguage = install?.AudioLanguage,
            manual = install?.IsManual ?? false,
            source = install is null ? "не найдена" : install.IsManual ? "выбрана вручную" : "найдена через Steam",
            settingsFile = DotaPathStore.FilePath,
            error = Error,
        };
    }

    /// <summary>
    /// Переключает установку Dota 2 без перезапуска приложения. Пустой путь возвращает
    /// автоопределение через Steam.
    /// </summary>
    public async Task<object> SetDotaPathAsync(string? path)
    {
        if (DotaInstallation.IsGameRunning())
        {
            return new { success = false, message = "Закрой Dota 2 перед сменой папки" };
        }

        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            normalized = DotaInstallation.NormalizeGameDir(path, out var pathError);
            if (normalized is null)
            {
                return new { success = false, message = pathError ?? "некорректный путь" };
            }
        }

        await InstallGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!DotaPathStore.Save(normalized, out var saveError))
            {
                return new { success = false, message = "не удалось сохранить путь: " + saveError };
            }

            Status = normalized is null ? "ищу Dota 2 через Steam…" : "переключаю папку Dota 2…";
            Ready = false;
            Error = null;
            await Task.Run(InitializeAsync).ConfigureAwait(false);
        }
        finally
        {
            InstallGate.Release();
        }

        var install = Install;
        if (install is null)
        {
            return new
            {
                success = false,
                message = Error ?? "Dota 2 не найдена по этому пути",
                gameDir = (string?)null,
                dotaDir = (string?)null,
            };
        }

        return new
        {
            success = true,
            message = $"Dota 2: {install.GameDir}",
            gameDir = install.GameDir,
            dotaDir = install.DotaDir,
            modFolder = install.ModFolderPath,
            buildId = install.BuildId,
            manual = install.IsManual,
        };
    }

    public void Start()
    {
        _ = Task.Run(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        _monitor.Cancel();
        _monitor.Dispose();
        _monitor = new CancellationTokenSource();

        Install = null;
        Index = null;
        Catalog = null;
        WorldCatalog = null;
        Sets = null;
        Session = null;
        Icons = null;
        Heroes.Clear();
        Ready = false;
        Error = null;

        try
        {
            Status = "ищу установку Dota 2…";
            var install = DotaInstallation.Detect(out var detectError);
            if (install is null)
            {
                Error = detectError ?? "Dota 2 не найдена: Steam-библиотека с dota 2 beta не обнаружена";
                _ready.TrySetResult();
                return;
            }

            Install = install;
            Status = "готовлю движок VPK…";
            var engine = Wibe.Core.Tools.EngineBundle.Ensure();
            if (engine is null)
            {
                Error = "движок VPK не распаковался: " + (Wibe.Core.Tools.EngineBundle.LastError ?? "неизвестная причина");
                _ready.TrySetResult();
                return;
            }

            Status = "читаю индекс пака игры…";
            var index = await Task.Run(() => PakIndex.Load(install));
            Index = index;

            Status = "читаю каталог косметики…";
            var catalog = await Task.Run(() => EconCatalog.Load(install));
            Catalog = catalog;
            WorldCatalog = WorldCosmeticsCatalog.LoadCached(install);
            Sets = new HeroSetService(install, index, catalog);
            Session = new ModSession(install, index, catalog, Sets);
            Loadout.FolderAlias = CanonicalFolder;
            Icons = new IconJob(this);

            // Локальные иконки из VPK: карта image_inventory → файл, без сети.
            var buildId = install.BuildId ?? "unknown";
            ImageStore.LoadIndex(buildId);

            // Легаси-патч gameinfo.gi (старые версии) чистим всегда: координатор его не терпит.
            var restored = Session.GameInfo.RestoreIfStale();
            if (restored)
            {
                SessionNote = "убрал старый патч gameinfo.gi";
            }
            else if (Session.IsActive)
            {
                SessionNote = $"мод собран: {Session.ModPakName}";
            }

            // мод живёт в изолированном контейнере game/dota_danish, а не в языковой папке Valve
            SessionNote = MountContainer() ?? SessionNote;

            _ = Task.Run(() => Session.MonitorAsync(_monitor.Token));

            Status = "готовлю каталог героев…";
            if (!Sets.TryLoadCache(ImageStore.CatalogCachePath))
            {
                await Task.Run(() => Sets.PrepareAll());
                Sets.SaveCache(ImageStore.CatalogCachePath);
            }

            Status = "собираю список героев…";
            var heroes = await HeroCatalog.LoadAsync(install, Sets.PreparedFolders);
            BuildHeroes(index, heroes);

            Ready = true;
            Status = "готово";

            // Первый запуск или новый buildid: один раз декодируем иконки из пака игры.
            Icons?.StartIfStale();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "ошибка инициализации";
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    private void BuildHeroes(PakIndex index, IReadOnlyList<HeroInfo> catalogHeroes)
    {
        var bodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hero in catalogHeroes)
        {
            names[hero.Folder] = hero.Name;
        }

        if (catalogHeroes.Count == 0)
        {
            foreach (var bodyFolder in Sets?.PreparedFolders ?? Array.Empty<string>())
            {
                names.TryAdd(bodyFolder, HeroCatalog.Prettify(bodyFolder));
            }
        }

        foreach (var entry in index.Entries)
        {
            if (!entry.Path.StartsWith("models/heroes/", StringComparison.Ordinal))
            {
                continue;
            }

            var rest = entry.Path["models/heroes/".Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                continue;
            }

            var folder = rest[..slash];
            if (string.Equals(rest, $"{folder}/{folder}.vmdl_c", StringComparison.OrdinalIgnoreCase))
            {
                bodies[folder] = entry.Path;
            }
        }

        var compiled = Session?.CompiledHeroes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Heroes.Clear();

        IEnumerable<string> order = names.Keys;
        if (catalogHeroes.Count == 0)
        {
            // офлайн и без кеша: только герои, у которых реально есть тело в паке
            order = bodies.Keys;
        }

        foreach (var folder in order.OrderBy(f => names.GetValueOrDefault(f, HeroCatalog.Prettify(f)), StringComparer.OrdinalIgnoreCase))
        {
            var target = bodies.GetValueOrDefault(folder) ?? $"models/heroes/{folder}/{folder}.vmdl_c";
            var info = catalogHeroes.FirstOrDefault(h => string.Equals(h.Folder, folder, StringComparison.OrdinalIgnoreCase))
                       ?? new HeroInfo(folder, names.GetValueOrDefault(folder, HeroCatalog.Prettify(folder)));

            // «мод» на карточке = герой реально есть в текущем собранном паке, а не запись из старого журнала.
            var inBuild = compiled.Contains(folder);
            var compiledInfo = inBuild ? Session?.CompiledInfo(folder) : null;

            Heroes.Add(new HeroEntry(
                folder,
                info.Name,
                target,
                compiledInfo is null ? (inBuild ? "в сборке" : null) : $"в сборке: {compiledInfo.Value.Picks} предмет(ов)",
                inBuild,
                compiledInfo?.Slot ?? 0,
                info.Portrait,
                info.Crop));
        }
    }

    public void RefreshHeroes()
    {
        if (Index is null)
        {
            return;
        }

        var names = Heroes.Select(h => new HeroInfo(h.Folder, h.Name)).ToList();
        BuildHeroes(Index, names);
    }

    public StateDto Snapshot()
    {
        var install = Install;
        return new StateDto(
            Ready,
            Status,
            Error,
            install?.BuildId,
            Session?.ModFolderPath,
            Session is not null && Directory.Exists(Session.ModFolderPath),
            Session is null || !Session.GameInfo.Status().Patched,
            Session?.ModPakName ?? string.Empty,
            install is not null && DotaInstallation.IsGameRunning(),
            ModJournal.Load().Mods.Count,
            install?.OccupiedSlots.Select(s => s.Number).ToArray() ?? Array.Empty<int>(),
            typeof(AppState).Assembly.GetName().Version?.ToString(3) ?? "0.3.0",
            install?.GameDir,
            install?.DotaDir,
            install?.IsManual ?? false);
    }

    public object BuildSession(string folder, IReadOnlyList<(string Slot, int ItemId)> picks)
    {
        lock (_installLock)
        {
            var outcome = Session!.Build(folder, picks);
            SessionNote = outcome.Message;
            if (outcome.Success)
            {
                RefreshHeroes();
            }

            return new
            {
                success = outcome.Success,
                message = outcome.Message,
                files = outcome.Files,
                vpkSize = outcome.VpkSize,
                remaps = outcome.Remaps,
            };
        }
    }

    public object RevertSession()
    {
        lock (_installLock)
        {
            var (success, message) = Session!.Revert();
            SessionNote = message;
            RefreshHeroes();
            return new { success, message };
        }
    }

    public object LaunchGame(bool dryRun = false, bool conDebug = false)
    {
        var (success, message) = Session!.Launch(dryRun, conDebug);
        return new { success, message };
    }

    /// <summary>Реальная проверка по console.log: примонтирован ли наш pak в языковой папке.</summary>
    public object MountInfo()
    {
        var session = Session;
        if (session is null)
        {
            return new { ok = false, message = "приложение не готово", mounted = Array.Empty<string>() };
        }

        var report = session.CheckMount();
        return new
        {
            report.Ok,
            report.Message,
            report.PakName,
            report.PakNumber,
            report.MountFolder,
            report.LogPath,
            report.CommandLine,
            report.MountedPaks,
        };
    }

    public object SessionInfo()
    {
        var session = Session;
        if (session is null)
        {
            return new { active = false, note = SessionNote };
        }

        var status = session.GameInfo.Status();
        var state = session.LoadState();

        // Мод после старта живёт не в языковой папке, а в контейнере dota_danish/pak02_dir.vpk.
        var mountedPak = string.Empty;
        long mountedBytes = 0;
        var containerFolder = session.ModFolderPath;
        var install = Install;
        if (install is not null)
        {
            var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(install.MainPakDir)!)!;
            var pak = Launcher.LanguageMount.ModPakPath(gameRoot);
            if (File.Exists(pak))
            {
                mountedPak = Path.GetFileName(pak);
                mountedBytes = new FileInfo(pak).Length;
                containerFolder = Launcher.LanguageMount.Folder(gameRoot);
            }
        }

        var active = session.IsActive || mountedPak.Length > 0;
        return new
        {
            active,
            patched = status.Patched,
            backup = status.BackupExists,
            gameinfoClean = !status.Patched,
            mountFolder = containerFolder,
            pak = mountedPak.Length > 0 ? mountedPak : session.IsActive ? session.ModPakName : null,
            languageFolder = session.ModFolderPath,
            note = SessionNote,
            vpk = mountedPak.Length > 0 ? mountedBytes : File.Exists(session.ModVpkPath) ? new FileInfo(session.ModVpkPath).Length : 0,
            hero = state?.Folder,
            picks = state?.Picks.Select(p => new { p.Slot, p.ItemId }).ToList(),
            heroes = state?.Heroes.Select(h => new
            {
                h.Folder,
                picks = h.Picks.Select(p => new { p.Slot, p.ItemId }).ToList(),
            }).ToList(),
            loadoutHeroes = Loadout.HeroesCount,
            loadoutItems = Loadout.ItemsCount,
            builtAt = state?.BuiltAt,
        };
    }

    public object? InstalledFor(string folder)
    {
        var info = Session?.CompiledInfo(folder);
        if (info is null)
        {
            return null;
        }

        return new
        {
            title = $"в сборке: {info.Value.Picks} предмет(ов)",
            slot = info.Value.Slot,
            files = info.Value.Files,
        };
    }

    /// <summary>
    /// Карточки главной: ровно то, что лежит подпапками в Heroes игрока. Список читается с
    /// диска при каждом запросе, поэтому новая папка появляется на сайте без пересборки.
    /// Ability Effects — не герой, её карточки уже прикреплены к разделу Luna.
    /// </summary>
    private static List<object> ReadHeroCollection()
    {
        var cards = new List<object>();
        var root = FindHeroesRoot();
        var meta = ReadCollectionMeta();
        if (root is null)
        {
            // Одиночный exe: папки Heroes рядом нет, но вшитый collection.json
            // по-прежнему содержит все 139 карточек, включая 12 Личностей.
            var i0 = 0;
            foreach (var seed in meta.Where(m => !string.IsNullOrWhiteSpace(m.Name)))
            {
                var slug = string.IsNullOrWhiteSpace(seed.Slug) ? seed.Name! : seed.Slug!;
                cards.Add(new
                {
                    i = i0++,
                    name = seed.Name,
                    slug,
                    persona = seed.PersonaId != 0,
                    img = string.IsNullOrWhiteSpace(seed.Img) ? $"/art/heroes/{slug}.jpg" : seed.Img,
                    hero = string.IsNullOrWhiteSpace(seed.Hero) ? slug : seed.Hero,
                    personaId = seed.PersonaId,
                    isPersona = seed.PersonaId != 0,
                });
            }

            return cards;
        }

        var byName = meta.Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var folders = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n) &&
                        !string.Equals(n, "Ability Effects", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var i = 0;
        foreach (var folder in folders)
        {
            byName.TryGetValue(folder!, out var seed);
            var slug = seed?.Slug;
            if (string.IsNullOrWhiteSpace(slug))
            {
                slug = new string(folder!.ToLowerInvariant()
                    .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
                while (slug.Contains("__", StringComparison.Ordinal))
                {
                    slug = slug.Replace("__", "_", StringComparison.Ordinal);
                }
            }

            var personaId = seed?.PersonaId ?? 0;
            var img = seed?.Img;
            if (string.IsNullOrWhiteSpace(img))
            {
                img = $"/art/heroes/{slug}.jpg";
            }

            // Портрет обязан быть локальным вырезом сетки, а не иконкой Steam.
            var art = Path.GetFileNameWithoutExtension(img);
            if (!HasArt("heroes", art))
            {
                img = $"/art/heroes/{slug}.jpg";
            }

            cards.Add(new
            {
                i = i++,
                name = folder,
                slug,
                persona = personaId != 0,
                img,
                hero = string.IsNullOrWhiteSpace(seed?.Hero) ? slug : seed.Hero,
                personaId,
                isPersona = personaId != 0,
            });
        }

        return cards;
    }

    private static string? FindHeroesRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("WIBE_HEROES_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var at = new DirectoryInfo(start);
            for (var up = 0; at is not null && up < 7; up++, at = at.Parent)
            {
                var candidate = Path.Combine(at.FullName, "Heroes");
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    if (Directory.EnumerateDirectories(candidate).Take(100).Count() >= 100)
                    {
                        return candidate;
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        return null;
    }

    private sealed record CollectionSeed(string? Name, string? Slug, string? Img, string? Hero, int PersonaId);

    private static readonly JsonSerializerOptions SeedJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static List<CollectionSeed> ReadCollectionMeta()
    {
        try
        {
            string? text = null;
            var file = Path.Combine(AppContext.BaseDirectory, "www", "collection.json");
            if (File.Exists(file))
            {
                text = File.ReadAllText(file);
            }
            else
            {
                using var stream = typeof(AppState).Assembly.GetManifestResourceStream("www.collection.json");
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    text = reader.ReadToEnd();
                }
            }

            return string.IsNullOrWhiteSpace(text)
                ? new List<CollectionSeed>()
                : JsonSerializer.Deserialize<List<CollectionSeed>>(text, SeedJson) ?? new List<CollectionSeed>();
        }
        catch (Exception)
        {
            return new List<CollectionSeed>();
        }
    }

    public object HeroList()
    {
        // Личности остаются внутри страниц своих базовых героев: отдельными
        // карточками в общей сетке они только дублируют одну и ту же модель.
        var personaFolders = HeroFolderCatalog.PersonaFolders();
        var heroes = Heroes.Where(h => !personaFolders.Contains(h.Folder)).Select(h => new
        {
            h.Folder,
            h.Name,
            h.Target,
            h.ModTitle,
            h.HasMod,
            h.Slot,
            h.Portrait,
            h.Crop,
            selected = Loadout.HasHero(h.Folder),
            picksCount = Loadout.ForHero(h.Folder).Count,
        }).ToList();

        // Настоящие личности: селектор prefab wearable. Старые default_item-заглушки
        // вроде «Anti-Mage's Persona» не показываем — они не дают модели.
        var personas = (Catalog?.Items ?? Array.Empty<EconItem>())
            .Where(i => string.Equals(i.Slot, "persona_selector", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(i.Prefab, "wearable", StringComparison.OrdinalIgnoreCase))
            .Select(i =>
            {
                var folder = i.Hero.StartsWith("npc_dota_hero_", StringComparison.OrdinalIgnoreCase)
                    ? i.Hero["npc_dota_hero_".Length..]
                    : i.Hero;
                var hero = Heroes.FirstOrDefault(h => string.Equals(h.Folder, folder, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    folder,
                    heroName = hero?.Name ?? folder,
                    itemId = i.Id,
                    name = i.Name,
                    image = i.Image,
                    portrait = hero?.Portrait,
                    crop = hero?.Crop,
                    art = HasArt("personas", i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                };
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.folder) &&
                        Heroes.Any(h => string.Equals(h.Folder, p.folder, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.heroName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.itemId)
            .ToList();

        return new
        {
            heroes,
            personas,
            collection = ReadHeroCollection(),
            loadout = new { heroesCount = Loadout.HeroesCount, itemsCount = Loadout.ItemsCount },
        };
    }

    public object SetHeroPicks(
        string folder,
        IReadOnlyList<(string Slot, int ItemId)> picks,
        IReadOnlyDictionary<string, int>? styles = null,
        bool rareTaunt = false)
    {
        // одна и та же героиня не должна заводить две записи: страница wraith_king и
        // страница skeleton_king — это Wraith King, и выбор ложится под его настоящее имя
        folder = CanonicalFolder(folder);
        Loadout.SetHero(folder, picks.Select(p => new KeyValuePair<string, int>(p.Slot, p.ItemId)), styles, rareTaunt);
        return new
        {
            success = true,
            message = Loadout.HasHero(folder)
                ? $"сохранено: {picks.Count} предмет(ов) у {folder}"
                : $"выбор для {folder} очищен",
            loadout = Loadout.Info(),
        };
    }

    /// <summary>
    /// The folder a hero is stored under, from the catalog: "wraith_king" is skeleton_king
    /// in items_game, "wisp" is wisp. Keeps one hero to one entry of the loadout.
    /// </summary>
    private string CanonicalFolder(string folder)
    {
        var key = Sets?.HeroKeyFor(folder);
        if (key is null)
        {
            return folder;
        }

        var shortName = key.StartsWith("npc_dota_hero_", StringComparison.OrdinalIgnoreCase)
            ? key["npc_dota_hero_".Length..]
            : key;
        var byKey = Heroes.FirstOrDefault(h => string.Equals(h.Folder, shortName, StringComparison.OrdinalIgnoreCase));
        return byKey?.Folder ?? folder;
    }

    public object ClearLoadout()
    {
        Loadout.Clear();
        return new { success = true, message = "текущая примерка очищена", loadout = Loadout.Info() };
    }

    /// <summary>Единая компиляция: все настроенные герои в один pak06_dir.vpk.</summary>
    /// <summary>
    /// Переносит наш собранный pak из языковой папки в изолированный контейнер
    /// <c>game/dota_danish/</c>: официальные паки русской озвучки — хардлинками под родными
    /// именами <c>pak01_*</c>, наш мод — <c>pak02_dir.vpk</c>. Возвращает строку статуса.
    /// </summary>
    private string? MountContainer(bool embedLocalization = true)
    {
        var install = Install;
        if (install is null || string.IsNullOrWhiteSpace(install.MainPakDir))
        {
            return null;
        }

        try
        {
            var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(install.MainPakDir)!)!;
            // Источник официальных паков — именно языковая папка (dota_russian), а не базовая
            // game/dota: базовые паки движок и так монтирует сам, а хардлинки всей игры
            // в контейнер давали десятки гигабайт лишних томов и падение на pak02_135.
            var deployed = Launcher.LanguageMount.Deploy(gameRoot, install.ModFolderPath, embedLocalization: embedLocalization);
            return "мод смонтирован: game/" + Launcher.LanguageMount.FolderName +
                   "/pak02_dir.vpk (запуск с -language " + Launcher.LanguageMount.Language + ")" +
                   (deployed.Any(d => d.StartsWith("loc:", StringComparison.Ordinal))
                       ? "; интерфейс: русский"
                       : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "мод собран, но контейнер " + Launcher.LanguageMount.FolderName + " не смонтирован: " + ex.Message;
        }
    }

    /// <summary>
    /// Русские строки вливаются в собранный pak ДО переноса в контейнер: свежий файл не занят
    /// игрой/антивирусом, поэтому pak02_dir.vpk получается уже с русским интерфейсом.
    /// </summary>
    private bool EmbedLocalizationIntoBuild()
    {
        var install = Install;
        var session = Session;
        if (install is null || session is null || string.IsNullOrWhiteSpace(install.MainPakDir))
        {
            return false;
        }

        try
        {
            var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(install.MainPakDir)!)!;
            var localization = Launcher.LanguageMount.CollectLocalization(gameRoot);
            return Launcher.LanguageMount.EmbedLocalization(session.ModVpkPath, localization) > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public object Compile()
    {
        lock (_installLock)
        {
            var picks = Loadout.AsPicks();
            var world = WorldSelection();
            if (picks.Count == 0 && world.IsEmpty)
            {
                return new { success = false, message = "примерка пуста: сначала выбери скины героям или косметику мира" };
            }

            var stage = new List<string>();
            var rust = Wibe.Core.Mods.RustCoreModBuilder.Build(Install!, Catalog!, picks, line =>
            {
                lock (stage)
                {
                    stage.Add(line);
                    if (stage.Count > 50)
                    {
                        stage.RemoveAt(0);
                    }
                }

                SessionNote = line;
            }, world);
            if (!rust.Success)
            {
                SessionNote = rust.Message;
                return new
                {
                    success = false,
                    message = rust.Message,
                    files = 0,
                    vpkSize = 0L,
                    remaps = Array.Empty<object>(),
                    heroes = picks.Count,
                    log = stage,
                };
            }

            var outcome = Session!.RegisterExternalBuild(picks, rust.PakName!, rust.Files, rust.VpkSize);
            var terrain = ApplyTerrain();
            SessionNote = terrain is null ? outcome.Message : outcome.Message + "; " + terrain;
            if (outcome.Success)
            {
                // Сразу переносим собранный pak в контейнер: игра монтирует его как pak02_dir.vpk.
                // Локализация вливается в свежий pak06 до копирования — так pak02 уже русский.
                var embedded = EmbedLocalizationIntoBuild();
                SessionNote = MountContainer(embedLocalization: !embedded) ?? SessionNote;
                RefreshHeroes();
            }

            return new
            {
                success = outcome.Success,
                message = SessionNote,
                files = outcome.Files,
                vpkSize = outcome.VpkSize,
                remaps = outcome.Remaps,
                heroes = picks.Count,
                world = WorldSelectionInfo(),
                left = rust.Left,
                log = stage,
            };
        }
    }

    /// <summary>Декомпиляция: убрать все наши паки, сбросить примерку и сессию.</summary>
    public object DecompileAll()
    {
        lock (_installLock)
        {
            var (success, message) = Session!.Revert();
            var terrain = RemoveTerrain();

            // снятие мода: убираем контейнер целиком и чистим старые хвосты в языковой папке
            try
            {
                var install = Install;
                if (install is not null && !string.IsNullOrWhiteSpace(install.MainPakDir))
                {
                    // Языковая папка Valve — ModFolderPath (dota_russian), не game/dota.
                    var russianDir = install.ModFolderPath;
                    var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(install.MainPakDir)!)!;
                    Launcher.LanguageMount.Unmount(gameRoot);
                    Launcher.LanguageMount.CleanLegacy(russianDir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message += "; контейнер не удалён: " + ex.Message;
            }

            Loadout.Clear();
            SessionNote = terrain is null ? message : message + "; " + terrain;
            RefreshHeroes();
            return new { success, message = SessionNote, loadout = Loadout.Info() };
        }
    }

    /// <summary>Список мировых косметик и текущий выбор: id строкой, как в JSON-состоянии.</summary>
    public object WorldState()
    {
        var catalog = WorldCatalog;
        return new
        {
            radiantTowers = catalog?.RadiantTowers.Select(WorldItem).ToList() ?? new List<object>(),
            direTowers = catalog?.DireTowers.Select(WorldItem).ToList() ?? new List<object>(),
            terrains = catalog?.Terrains.Select(WorldItem).ToList() ?? new List<object>(),
            emblems = catalog?.Emblems.Select(WorldItem).ToList() ?? new List<object>(),
            radiantCreeps = catalog?.RadiantCreeps.Select(WorldItem).ToList() ?? new List<object>(),
            direCreeps = catalog?.DireCreeps.Select(WorldItem).ToList() ?? new List<object>(),
            selected = WorldSelectionInfo(),
        };
    }

    public object SetWorldSelection(string? radiant, string? dire, string? terrain, string? emblem,
        string? radiantCreep = null, string? direCreep = null)
    {
        var catalog = WorldCatalog;
        if (catalog is null)
        {
            return new { success = false, message = "каталог мира ещё не готов" };
        }

        string? Validate(string? value, string kind, Func<WorldCosmetic, bool> allowed)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "0")
            {
                return null;
            }

            if (!int.TryParse(value, out var id))
            {
                return null;
            }

            var item = catalog.ById(id);
            return item is not null && allowed(item) ? item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        var r = Validate(radiant, "radiant", i => i.IsRadiantTower);
        var d = Validate(dire, "dire", i => i.IsDireTower);
        var t = Validate(terrain, "terrain", i => i.IsTerrain);
        var e = Validate(emblem, "emblem", i => i.IsEmblem);
        var rc = Validate(radiantCreep, "radiant_creep", i => i.IsRadiantCreep);
        var dc = Validate(direCreep, "dire_creep", i => i.IsDireCreep);
        Loadout.SetWorld(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["radiant_tower"] = r ?? string.Empty,
            ["dire_tower"] = d ?? string.Empty,
            ["terrain"] = t ?? string.Empty,
            ["emblem"] = e ?? string.Empty,
            ["radiant_creep"] = rc ?? string.Empty,
            ["dire_creep"] = dc ?? string.Empty,
        });
        return new { success = true, message = "косметика мира сохранена", selected = WorldSelectionInfo() };
    }

    private static object WorldItem(WorldCosmetic item) => new
    {
        id = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        item.Name,
        item.Prefab,
        item.Image,
        item.Rarity,
    };

    public Dictionary<string, string> WorldSelectionInfo()
    {
        var world = Loadout.WorldFor();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["radiant_tower"] = world.GetValueOrDefault("radiant_tower", string.Empty),
            ["dire_tower"] = world.GetValueOrDefault("dire_tower", string.Empty),
            ["terrain"] = world.GetValueOrDefault("terrain", string.Empty),
            ["emblem"] = world.GetValueOrDefault("emblem", string.Empty),
            ["radiant_creep"] = world.GetValueOrDefault("radiant_creep", string.Empty),
            ["dire_creep"] = world.GetValueOrDefault("dire_creep", string.Empty),
        };
    }

    private Wibe.Core.Mods.RustCoreModBuilder.WorldSelection WorldSelection()
    {
        var world = Loadout.WorldFor();
        int? Id(string key) => int.TryParse(world.GetValueOrDefault(key), out var id) && id > 0 ? id : null;
        return new Wibe.Core.Mods.RustCoreModBuilder.WorldSelection(
            Id("radiant_tower"),
            Id("dire_tower"),
            Id("emblem"),
            Id("radiant_creep"),
            Id("dire_creep"));
    }

    /// <summary>Ландшафт — отдельный файл карты в языковой папке; pak06 его не содержит.</summary>
    private string? ApplyTerrain()
    {
        var install = Install;
        var session = Session;
        var catalog = WorldCatalog;
        if (install is null || session is null)
        {
            return null;
        }

        var selected = Loadout.WorldFor().GetValueOrDefault("terrain");
        var maps = Path.Combine(session.ModFolderPath, "maps");
        var target = Path.Combine(maps, "dota.vpk");
        if (!int.TryParse(selected, out var id) || id <= 0)
        {
            RemoveTerrain();
            return null;
        }

        var item = catalog?.ById(id);
        var map = item?.TerrainMapFile;
        if (map is null)
        {
            return "ландшафт не найден в items_game";
        }

        var source = Path.Combine(install.DotaDir, "maps", Path.ChangeExtension(map, ".vpk"));
        if (!File.Exists(source))
        {
            return "в игре нет файла карты " + Path.GetFileName(source);
        }

        // Правило из dota2-mod-manager (mm-core/src/game/terrain_age.rs): ландшафт, собранный
        // раньше текущей карты игры, к ней не подходит — его выключают, а не ставят. Тот же
        // запас в сутки, что и там, чтобы не спорить с часовыми поясами архива.
        var mapTime = File.GetLastWriteTime(Path.Combine(install.DotaDir, "maps", "dota.vpk"));
        var packTime = File.GetLastWriteTime(source);
        if (packTime < mapTime.AddHours(-24))
        {
            RemoveTerrain();
            return "ландшафт: " + item!.Name + " — пак собран " + packTime.ToString("dd.MM.yyyy") +
                   ", а карта игры " + mapTime.ToString("dd.MM.yyyy") + "; к текущему патчу он не подходит, карта не подменяется";
        }

        var playable = Wibe.Core.Vpk.VpkIndex.IsPlayableMap(source);
        var world = !playable && Wibe.Core.Vpk.TerrainMap.HasTerrainWorld(source);

        Directory.CreateDirectory(maps);
        // Ландшафт — это гигабайты; жёсткая ссылка на файл игры занимает ноль байт,
        // а копия остаётся запасным путём для тома без NTFS-ссылок.
        if (File.Exists(target))
        {
            try
            {
                File.Delete(target);
            }
            catch (IOException)
            {
                return "старая карта занята игрой — закрой Dota 2";
            }
        }

        if (!playable && !world)
        {
            RemoveTerrain();
            return "ландшафт: " + item!.Name + " — в паке нет ни карты, ни мира ландшафта";
        }

        if (world)
        {
            // мир ландшафта собирается один раз на пару «ландшафт + карта игры» и лежит в кэше
            var stockMap = Path.Combine(install.DotaDir, "maps", "dota.vpk");
            if (!File.Exists(stockMap))
            {
                return "ландшафт: в игре нет карты maps/dota.vpk";
            }

            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WibeChanger", "terrain_cache");
            Directory.CreateDirectory(cacheDir);
            var cache = Path.Combine(cacheDir,
                Path.GetFileNameWithoutExtension(source) + "-" + new FileInfo(stockMap).Length + "-" +
                new FileInfo(source).Length + ".vpk");
            var overlaid = 0;
            var added = 0;
            if (!File.Exists(cache))
            {
                var files = Wibe.Core.Vpk.TerrainMap.Build(stockMap, source, cache, out overlaid, out added);
                if (files <= 0)
                {
                    return "ландшафт: " + item!.Name + " — мир не собрался";
                }
            }

            try
            {
                File.Copy(cache, target, overwrite: true);
            }
            catch (IOException)
            {
                return "старая карта занята игрой — закрой Dota 2";
            }

            var tail = overlaid + added > 0 ? $", {overlaid} файлов мира заменено, {added} добавлено" : "";
            return $"ландшафт: {item!.Name} — мир ландшафта на карте игры{tail}";
        }

        try
        {
            if (!NativeHardLink.Create(target, source))
            {
                File.Copy(source, target, overwrite: true);
            }
        }
        catch (IOException)
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            File.Copy(source, target, overwrite: true);
        }

        return "ландшафт: " + item!.Name + " — карта игры подменена целиком";
    }

    private string? RemoveTerrain()
    {
        var session = Session;
        if (session is null)
        {
            return null;
        }

        var maps = Path.Combine(session.ModFolderPath, "maps");
        var removed = false;
        foreach (var name in new[] { "dota.vpk", "dota.vpk.part", "pak07_dir.vpk" })
        {
            var path = Path.Combine(maps, name);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    removed = true;
                }
                catch (IOException)
                {
                }
            }
        }

        var slot = Path.Combine(session.ModFolderPath, "pak07_dir.vpk");
        if (File.Exists(slot))
        {
            try
            {
                File.Delete(slot);
                removed = true;
            }
            catch (IOException)
            {
            }
        }

        try
        {
            if (Directory.Exists(maps) && !Directory.EnumerateFileSystemEntries(maps).Any())
            {
                Directory.Delete(maps);
            }
        }
        catch (IOException)
        {
        }

        return removed ? "ландшафт убран" : null;
    }

    public object SavePreset(string name)
    {
        var preset = Presets.Save(name, Loadout.Heroes, Loadout.WorldFor(), Loadout.Styles());
        return new { success = true, message = $"пресет «{preset.Name}» сохранён", presets = PresetList() };
    }

    public object ApplyPreset(string id)
    {
        var preset = Presets.Find(id);
        if (preset is null)
        {
            return new { success = false, message = "пресет не найден", presets = PresetList() };
        }

        Loadout.Replace(preset.Heroes, preset.World, preset.Styles);
        RefreshHeroes();
        var styles = preset.Styles?.Sum(p => p.Value.Count) ?? 0;
        return new { success = true, message = $"загружен пресет «{preset.Name}»: героев {preset.Heroes.Count}, стилей {styles}", presets = PresetList(), loadout = Loadout.Info(), selected = WorldSelectionInfo() };
    }

    public object DeletePreset(string id)
    {
        var removed = Presets.Delete(id);
        return new { success = removed, message = removed ? "пресет удалён" : "пресет не найден", presets = PresetList() };
    }

    public object PresetList() => new
    {
        presets = Presets.All.Select(p => new
        {
            p.Id,
            p.Name,
            p.CreatedAt,
            heroes = p.Heroes.Count,
            items = p.Heroes.Sum(h => h.Value.Count),
            world = p.World?.Count ?? 0,
        }).ToList(),
        path = Presets.Path,
    };

    public object InstallPicks(string folder, IReadOnlyList<(string Slot, int ItemId)> picks)
    {
        lock (_installLock)
        {
            var result = Sets!.InstallSet(folder, picks);
            if (result.Success)
            {
                RefreshHeroes();
            }

            return new { success = result.Success, message = result.Message, slot = result.Mod?.Slot };
        }
    }

    public object Uninstall(string folder)
    {
        lock (_installLock)
        {
            var result = Sets!.Uninstall(folder);
            if (result.Success)
            {
                RefreshHeroes();
            }

            return new { success = result.Success, message = result.Message };
        }
    }

    public static string Prettify(string folder)
    {
        var words = folder.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
