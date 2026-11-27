using System.Diagnostics;
using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.Tools;

namespace Wibe.Server;

public sealed class IconStatus
{
    public bool Running { get; init; }

    public int Done { get; init; }

    public int Total { get; init; }

    public int Indexed { get; init; }

    public string Phase { get; init; } = string.Empty;

    public string Directory { get; init; } = string.Empty;

    public string BuildId { get; init; } = string.Empty;

    public string? Message { get; init; }
}

/// <summary>
/// Диск с готовыми PNG: сначала папка проекта (Z:\...\png), если диска нет — <exe>/png,
/// затем %LOCALAPPDATA%. Внутри — иконки, вытащенные из pak01_dir.vpk, и карта icons_index.json.
/// </summary>
public static class ImageStore
{
    private static readonly object InitLock = new();

    private static string? _directory;

    private static IconIndex _index = new(string.Empty, 0, new Dictionary<string, string>());

    public static string Directory
    {
        get
        {
            if (_directory is not null)
            {
                return _directory;
            }

            lock (InitLock)
            {
                _directory ??= ResolveDirectory();
                try
                {
                    System.IO.Directory.CreateDirectory(_directory);
                }
                catch (IOException)
                {
                }

                MigrateLegacy(_directory);
                return _directory;
            }
        }
    }

    /// <summary>Кеш каталога «рядом с png»: открытие любого героя идёт из памяти, без сканов.</summary>
    public static string CatalogCachePath
    {
        get
        {
            var parent = Path.GetDirectoryName(Directory.TrimEnd(Path.DirectorySeparatorChar));
            return Path.Combine(parent ?? Directory, "catalog_cache.json");
        }
    }

    /// <summary>
    /// Единственная вечная папка данных: %LOCALAPPDATA%\ICDK\UserData. Ни AppContext.BaseDirectory
    /// (у single-file это временная распаковка %TEMP%\.net\..., которая чистится), ни
    /// Environment.CurrentDirectory (меняется от ярлыка к ярлыку) в дело не идут: иначе пресеты и
    /// примерка пропадают между запусками. Переопределяется WIBE_USER_DATA — для тестов.
    /// </summary>
    public static string UserDataRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("WIBE_USER_DATA");
            var root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ICDK", "UserData")
                : configured!;
            System.IO.Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string PresetsPath => Migrate(Path.Combine(UserDataRoot, "presets.json"), "presets.json");

    public static string LoadoutPath => Migrate(Path.Combine(UserDataRoot, "selected_loadout.json"), "selected_loadout.json");

    public static string SettingsPath => Migrate(Path.Combine(UserDataRoot, "settings.json"), "settings.json");

    /// <summary>Один раз переносит старый файл из прежних мест: рядом с exe и в %LOCALAPPDATA%\WibeChanger.</summary>
    private static string Migrate(string target, string name)
    {
        try
        {
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                return target;
            }

            foreach (var old in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, name),
                         Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "WibeChanger", name),
                     })
            {
                if (!File.Exists(old) || Path.GetFullPath(old).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (new FileInfo(old).Length == 0)
                {
                    continue;
                }

                AtomicWrite(target, File.ReadAllBytes(old));
                return target;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // миграция — удобство, а не условие запуска
        }

        return target;
    }

    /// <summary>Запись через временный файл: недописанный JSON не затрёт рабочий.</summary>
    public static void AtomicWrite(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            System.IO.Directory.CreateDirectory(folder);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    public static void AtomicWrite(string path, string text) =>
        AtomicWrite(path, new System.Text.UTF8Encoding(false).GetBytes(text));

    private static string ResolveDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("WIBE_PNG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured!;
        }

        // 1. Папка проекта на Z: — основной кеш на этой машине.
        const string project = @"Z:\Free-Pi-Projects\WIBE_CHANGER_NEW\png";
        if (System.IO.Directory.Exists(@"Z:\") && System.IO.Directory.Exists(Path.GetDirectoryName(project)))
        {
            return project;
        }

        // 2. Рядом с exe: у другого пользователя проекта на Z: может не быть.
        var besideExe = Path.Combine(AppContext.BaseDirectory, "png");
        try
        {
            System.IO.Directory.CreateDirectory(besideExe);
            return besideExe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        // 3. Последний резерв.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WibeChanger",
            "png");
    }

    /// <summary>Старый сетевой кеш версий ≤0.6 переносим, чтобы не терять уже скачанные портреты.</summary>
    private static void MigrateLegacy(string target)
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WibeChanger",
                "img_cache");
            if (!System.IO.Directory.Exists(legacy) ||
                string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(legacy))
            {
                var name = Path.GetFileName(file);
                var destination = Path.Combine(target, name);
                if (!File.Exists(destination))
                {
                    File.Copy(file, destination);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static IconIndex LoadIndex(string buildId)
    {
        var index = IconExtractor.LoadIndex(Directory, buildId);
        _index = index;
        return index;
    }

    public static IconIndex Index => _index;

    /// <summary>Сколько готовых иконок лежит в AssetVault (0 — кэша нет).</summary>
    public static int VaultIconCount => VaultIcons()?.Count ?? 0;

    /// <summary>Пересобрать индекс готовых иконок после смены папки кэша.</summary>
    public static void ResetVaultIcons()
    {
        _vaultIcons = null;
        _byLeaf = null;
    }

    private static Dictionary<string, string>? _byLeaf;

    private static Dictionary<string, string>? _vaultIcons;

    /// <summary>Папка готовых иконок кэша: <c>&lt;vault&gt;/temp/icons/icons_v10</c>.</summary>
    public static string? VaultIconsDirectory
    {
        get
        {
            try
            {
                var root = CacheLocator.Root;
                return System.IO.Directory.Exists(root)
                    ? Path.Combine(root, "temp", "icons", "icons_v10")
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Готовая иконка из AssetVault: имя файла — это image_inventory, где "/" заменён на "_".
    /// Так <c>econ/items/axe/x/x</c> → <c>econ_items_axe_x_x.png</c>. Файловая система Windows
    /// регистронезависима, а на всякий случай держим индекс без учёта регистра.
    /// </summary>
    private static string? FindVaultIcon(string inventory)
    {
        var name = inventory.Replace('\\', '/').Trim().TrimStart('/').Replace('/', '_') + ".png";
        var index = VaultIcons();
        return index is not null && index.TryGetValue(name, out var file) ? file : null;
    }

    private static Dictionary<string, string>? VaultIcons()
    {
        if (_vaultIcons is not null)
        {
            return _vaultIcons;
        }

        var directory = VaultIconsDirectory;
        if (directory is null || !System.IO.Directory.Exists(directory))
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
            {
                map[Path.GetFileName(file)] = file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return map.Count == 0 ? null : _vaultIcons = map;
    }

    /// <summary>Локальный файл для image_inventory — готовый кэш, затем индекс пака; без сети.</summary>
    public static string? FindItem(string? inventory)
    {
        if (string.IsNullOrWhiteSpace(inventory))
        {
            return null;
        }

        var ready = FindVaultIcon(inventory);
        if (ready is not null)
        {
            return ready;
        }

        var relative = _index.Find(inventory);
        if (relative is null)
        {
            // Некоторые пути в items_game указывают не туда (например pudge_cute/...).
            // Ищем по имени файла: иконка с таким листом почти всегда есть в паке.
            var key = inventory.Replace('\\', '/').Trim('/').ToLowerInvariant();
            var leaf = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
            if (leaf.Length > 2)
            {
                _byLeaf ??= _index.Map
                    .GroupBy(pair => pair.Key[(pair.Key.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
                relative = _byLeaf.GetValueOrDefault(leaf);
            }
        }

        if (relative is null)
        {
            return null;
        }

        var full = Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? full : null;
    }

    public static string? FindHeroIcon(string folder)
    {
        var relative = _index.Find("hero:" + (folder ?? string.Empty).Trim().ToLowerInvariant());
        if (relative is null)
        {
            return null;
        }

        var full = Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? full : null;
    }
}

/// <summary>
/// Фоновая задача «разобрать все иконки из пака игры». Запускается один раз при старте,
/// если индекс отсутствует или собран под другой buildid; кнопка в UI перезапускает вручную.
/// </summary>
public sealed class IconJob
{
    private readonly AppState _state;

    private int _running;

    private int _done;

    private int _total;

    private int _indexed;

    private string _phase = "ожидание";

    private string? _message;

    public IconJob(AppState state)
    {
        _state = state;
    }

    public IconStatus Status => new()
    {
        Running = Volatile.Read(ref _running) == 1,
        Done = Volatile.Read(ref _done),
        Total = Volatile.Read(ref _total),
        Indexed = Volatile.Read(ref _indexed),
        Phase = _phase,
        Directory = ImageStore.Directory,
        BuildId = _state.Install?.BuildId ?? string.Empty,
        Message = _message,
    };

    public void StartIfStale()
    {
        var buildId = _state.Install?.BuildId ?? string.Empty;
        var ready = ImageStore.VaultIconCount;
        if (ready > 1_000)
        {
            // Готовый AssetVault полностью закрывает иконки: пак игры даже не открываем.
            ImageStore.LoadIndex(buildId);
            _phase = "готовый кэш";
            _message = $"иконки из AssetVault: {ready}, распаковка пака не нужна";
            _total = ready;
            _done = ready;
            _indexed = ready;
            return;
        }

        if (buildId.Length == 0 || IconExtractor.IsFresh(ImageStore.Directory, buildId))
        {
            ImageStore.LoadIndex(buildId);
            return;
        }

        Start();
    }

    public bool Start()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        _phase = "готовлю извлечение";
        _message = null;
        Volatile.Write(ref _done, 0);
        Volatile.Write(ref _total, 0);
        _ = Task.Run(RunAsync);
        return true;
    }

    private async Task RunAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var install = _state.Install;
            if (install is null)
            {
                _message = "установка Dota 2 не найдена";
                return;
            }

            var progress = new Progress<int>(_ => { });
            var result = await Task.Run(() => IconExtractor.ExtractAll(
                install.MainPakDir,
                install.BuildId ?? "unknown",
                ImageStore.Directory,
                (done, phase) =>
                {
                    Volatile.Write(ref _done, done);
                    _phase = phase;
                },
                CancellationToken.None)).ConfigureAwait(false);

            if (result.Success)
            {
                var index = ImageStore.LoadIndex(install.BuildId ?? "unknown");
                Volatile.Write(ref _indexed, index.Count);
                Volatile.Write(ref _total, result.Files);
                Volatile.Write(ref _done, result.Files);
                _phase = "готово";
                _message = $"{result.Message} за {watch.Elapsed.TotalMinutes:0.0} мин — сеть больше не нужна";
            }
            else
            {
                _phase = "ошибка";
                _message = result.Message;
            }

            _ = progress;
        }
        catch (Exception ex)
        {
            _phase = "ошибка";
            _message = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
