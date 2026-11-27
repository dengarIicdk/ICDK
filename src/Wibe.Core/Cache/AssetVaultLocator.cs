using System.Text.Json;

namespace Wibe.Core.Cache;

/// <summary>
/// Единственное место, которое знает, где лежит готовый кэш ICDK AssetVault.
/// Порядок поиска: <c>WIBE_CACHE</c> → <c>cachePath</c> в settings.json → установленный
/// AssetVault на любом диске (<c>X:\ICDK_AssetVault</c>) → <c>%LOCALAPPDATA%\ICDK\AssetVault</c>.
/// Кэш только читается: ни один путь здесь не создаёт и не удаляет чужие файлы.
/// </summary>
public static class AssetVaultLocator
{
    /// <summary>Имя папки кэша в корне диска — так его создаёт конвейер ICDK.</summary>
    public const string FolderName = "ICDK_AssetVault";

    /// <summary>Путь по умолчанию, если готового кэша на дисках нет.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ICDK", "AssetVault");

    /// <summary>Папка пользовательских данных приложения (settings.json и т.п.).</summary>
    public static string UserDataRoot =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WIBE_USER_DATA"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ICDK", "UserData")
            : Environment.GetEnvironmentVariable("WIBE_USER_DATA")!;

    /// <summary>Корень кэша из настроек конкретного пользователя.</summary>
    public static string Resolve() => Resolve(Path.Combine(UserDataRoot, "settings.json"));

    /// <summary>Корень кэша; <paramref name="settingsPath"/> — settings.json с полем <c>cachePath</c>.</summary>
    public static string Resolve(string settingsPath)
    {
        var fromEnv = Environment.GetEnvironmentVariable("WIBE_CACHE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        foreach (var candidate in new[]
                 {
                     FromSettings(settingsPath),
                     FromSettings(Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "ICDK", "settings.json")),
                 })
        {
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return FindOnDrives() ?? DefaultDirectory;
    }

    /// <summary>Готовый кэш на любом подключённом диске — самый наполненный из найденных.</summary>
    public static string? FindOnDrives()
    {
        string? best = null;
        long bestScore = -1;
        foreach (var drive in Drives())
        {
            string candidate;
            try
            {
                candidate = Path.Combine(drive.RootDirectory.FullName, FolderName);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                continue;
            }

            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var score = Score(candidate);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static IEnumerable<DriveInfo> Drives()
    {
        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in all)
        {
            bool ok;
            try
            {
                ok = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }

            if (ok)
            {
                yield return drive;
            }
        }
    }

    /// <summary>Грубая «наполненность» кэша: готовые .glb и папки моделей. Дешёвая и с ограничением.</summary>
    private static long Score(string root)
    {
        long score = 0;
        try
        {
            var meshes = Path.Combine(root, "meshes");
            if (Directory.Exists(meshes))
            {
                score += Directory.EnumerateFiles(meshes, "*.glb", SearchOption.TopDirectoryOnly).Take(5_000).Count();
            }

            var ready = Path.Combine(root, "temp", "meshes");
            if (Directory.Exists(ready))
            {
                score += Directory.EnumerateDirectories(ready, "*", SearchOption.TopDirectoryOnly).Take(20_000).Count();
            }

            var icons = Path.Combine(root, "temp", "icons", "icons_v10");
            if (Directory.Exists(icons) && Directory.EnumerateFiles(icons, "*.png").Take(1).Any())
            {
                score += 10_000;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // нечитаемый диск не должен ломать поиск
        }

        return score;
    }

    private static string? FromSettings(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("cachePath", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var configured = value.GetString();
            return !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)
                ? Path.GetFullPath(configured)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // битые настройки — идём дальше по кандидатам
            return null;
        }
    }
}
