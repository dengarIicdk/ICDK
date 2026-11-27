using System.Diagnostics;
using System.Text.RegularExpressions;
using Wibe.Core.Steam;
using Wibe.Core.Vdf;

namespace Wibe.Core.Dota;

/// <summary>Занятый слот пака в папке модов (pakNN_dir.vpk).</summary>
public sealed record ModPakSlot(int Number, string DirVpkPath, long SizeBytes)
{
    public string FileName => Path.GetFileName(DirVpkPath);

    public bool IsValveLocalization => Number == 1;

    public bool IsModernLayoutNotice => Number >= 64;
}

/// <summary>
/// Всё, что нужно знать о локальной установке Dota 2:
/// пути, сборка, язык (→ папка модов), занятые и свободные слоты, наличие подписей.
/// </summary>
public sealed partial class DotaInstallation
{
    public const int FirstUsableSlot = 2;
    public const int LastUsableSlot = 99;

    private DotaInstallation(
        SteamAppManifest app,
        string audioLanguage,
        string modFolderName,
        bool manual = false,
        string? gameDirOverride = null)
    {
        IsManual = manual;
        App = app;
        GameDir = gameDirOverride ?? app.FullInstallPath;
        DotaDir = Path.Combine(GameDir, "game", "dota");
        BuildId = app.BuildId;
        AudioLanguage = audioLanguage;
        ModFolderName = modFolderName;
        ModFolderPath = Path.Combine(GameDir, "game", modFolderName);
        MainPakDir = Path.Combine(DotaDir, "pak01_dir.vpk");
        SignaturesPath = Path.Combine(GameDir, "game", "bin", "win64", "dota.signatures");
        BootConfigPath = Path.Combine(DotaDir, "cfg", "boot.vcfg");
        ModGameInfoPath = Path.Combine(ModFolderPath, "gameinfo.gi");
        HeroCatalogDir = Path.Combine(DotaDir, "scripts", "npc", "heroes");
        UnitsScriptPath = Path.Combine(DotaDir, "scripts", "npc", "npc_units.txt");
    }

    public SteamAppManifest App { get; }

    /// <summary>Путь выбран вручную на сайте, а не найден через Steam.</summary>
    public bool IsManual { get; }

    public string SteamLibrary => App.LibraryRoot;

    public string GameDir { get; }

    public string DotaDir { get; }

    public string? BuildId { get; }

    public string AudioLanguage { get; }

    public string ModFolderName { get; }

    public string ModFolderPath { get; }

    public string MainPakDir { get; }

    public string SignaturesPath { get; }

    public string BootConfigPath { get; }

    public string ModGameInfoPath { get; }

    public string HeroCatalogDir { get; }

    public string UnitsScriptPath { get; }

    public bool IsValid => Directory.Exists(GameDir) && File.Exists(MainPakDir);

    public bool ModFolderExists => Directory.Exists(ModFolderPath);

    /// <summary>Папка модов объявлена в своём gameinfo.gi как LayeredOnMod dota.</summary>
    public bool ModFolderLayeredOnDota
    {
        get
        {
            var gi = KeyValues.TryParseFile(ModGameInfoPath);
            if (gi is null)
            {
                return false;
            }

            var layered = gi.Child("GameInfo")?.String("LayeredOnMod");
            return string.Equals(layered, "dota", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>В SearchPaths папки модов она сама стоит первой (значит перекрывает dota).</summary>
    public bool ModFolderHasSelfSearchPath
    {
        get
        {
            var gi = KeyValues.TryParseFile(ModGameInfoPath);
            var paths = gi?.Child("GameInfo")?.Child("FileSystem")?.Child("SearchPaths");
            if (paths is null)
            {
                return false;
            }

            var firstGame = paths.All("Game").FirstOrDefault()?.Value;
            return string.Equals(firstGame, ModFolderName, StringComparison.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<ModPakSlot> OccupiedSlots => SlotCache ??= ScanSlots();

    private IReadOnlyList<ModPakSlot>? SlotCache { get; set; }

    public int? RecommendedFreeSlot => FreeSlots.FirstOrDefault();

    public IEnumerable<int> FreeSlots
    {
        get
        {
            var used = OccupiedSlots.Select(s => s.Number).ToHashSet();
            for (var i = FirstUsableSlot; i <= LastUsableSlot; i++)
            {
                if (!used.Contains(i))
                {
                    yield return i;
                }
            }
        }
    }

    public IReadOnlyList<ModPakSlot> ScanSlots()
    {
        var result = new List<ModPakSlot>();
        if (!ModFolderExists)
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(ModFolderPath, "pak*_dir.vpk", SearchOption.TopDirectoryOnly))
        {
            var match = SlotRegex().Match(Path.GetFileName(file));
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number))
            {
                continue;
            }

            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // файл занят — размер не критичен
            }

            result.Add(new ModPakSlot(number, file, size));
        }

        result.Sort((a, b) => a.Number.CompareTo(b.Number));
        return result;
    }

    public static bool IsGameRunning()
    {
        try
        {
            return Process.GetProcessesByName("dota2").Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static DotaInstallation? Detect(out string? error)
    {
        error = null;
        var manualPath = DotaPathStore.Read();
        if (!string.IsNullOrWhiteSpace(manualPath))
        {
            var manual = FromGameDir(manualPath, out var manualError);
            if (manual is not null)
            {
                return manual;
            }

            error = manualError;
        }

        var apps = SteamLocator.FindApps(SteamLocator.DotaAppId);
        if (apps.Count == 0)
        {
            error = "Steam не найден или Dota 2 не установлена (нет appmanifest_570.acf ни в одной библиотеке).";
            return null;
        }

        SteamAppManifest? best = null;
        foreach (var app in apps)
        {
            if (Directory.Exists(app.FullInstallPath))
            {
                best = app;
                break;
            }
        }

        best ??= apps[0];

        var audioLanguage = ReadAudioLanguage(best.FullInstallPath);
        var modFolder = ResolveModFolderName(best.FullInstallPath, audioLanguage);
        return new DotaInstallation(best, audioLanguage, modFolder);
    }

    /// <summary>
    /// Приводит выбранный пользователем путь к корню установки Dota 2. Принимает сам корень,
    /// `game`, `game\dota` или файл внутри установки: ищет вверх до `game\dota\pak01_dir.vpk`.
    /// </summary>
    public static string? NormalizeGameDir(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "путь не указан";
            return null;
        }

        string start;
        try
        {
            start = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "некорректный путь";
            return null;
        }

        if (File.Exists(start))
        {
            start = Path.GetDirectoryName(start)!;
        }

        var cursor = new DirectoryInfo(start);
        for (var depth = 0; depth < 4 && cursor is not null; depth++)
        {
            var probe = Path.Combine(cursor.FullName, "game", "dota", "pak01_dir.vpk");
            if (File.Exists(probe))
            {
                return cursor.FullName.TrimEnd('\\');
            }

            cursor = cursor.Parent;
        }

        error = "в этой папке нет Dota 2: не найден game\\dota\\pak01_dir.vpk";
        return null;
    }

    /// <summary>Установка по конкретному пути. Buildid подтягивается из ближайшего appmanifest_570.acf.</summary>
    public static DotaInstallation? FromGameDir(string? path, out string? error)
    {
        var gameDir = NormalizeGameDir(path, out error);
        if (gameDir is null)
        {
            return null;
        }

        var buildId = FindBuildId(gameDir);
        var app = new SteamAppManifest(
            SteamLocator.DotaAppId,
            "Dota 2",
            Path.GetFileName(gameDir),
            buildId,
            null,
            Path.GetDirectoryName(gameDir) ?? gameDir);

        var audioLanguage = ReadAudioLanguage(gameDir);
        var modFolder = ResolveModFolderName(gameDir, audioLanguage);
        return new DotaInstallation(app, audioLanguage, modFolder, manual: true, gameDirOverride: gameDir);
    }

    private static string? FindBuildId(string gameDir)
    {
        var cursor = new DirectoryInfo(gameDir);
        for (var depth = 0; depth < 5 && cursor is not null; depth++)
        {
            foreach (var manifest in new[]
                     {
                         Path.Combine(cursor.FullName, "steamapps", "appmanifest_570.acf"),
                         Path.Combine(cursor.FullName, "appmanifest_570.acf"),
                     })
            {
                var state = KeyValues.TryParseFile(manifest)?.Child("AppState");
                var buildId = state?.String("buildid");
                if (!string.IsNullOrWhiteSpace(buildId))
                {
                    return buildId;
                }
            }

            cursor = cursor.Parent;
        }

        return null;
    }

    public static string ReadAudioLanguage(string gameDir)
    {
        var boot = Path.Combine(gameDir, "game", "dota", "cfg", "boot.vcfg");
        var root = KeyValues.TryParseFile(boot);
        var value = root?.Child("boot")?.String("AudioLanguage");
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value!.Trim().ToLowerInvariant();
    }

    /// <summary>Имя папки модов: dota_&lt;язык&gt;, если она существует, иначе dota.</summary>
    public static string ResolveModFolderName(string gameDir, string audioLanguage)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(audioLanguage))
        {
            candidates.Add($"dota_{audioLanguage}");
        }

        candidates.Add("dota");

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(gameDir, "game", candidate)))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    [GeneratedRegex(@"^pak(\d{2,3})_dir\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex SlotRegex();
}
