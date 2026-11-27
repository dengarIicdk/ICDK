using Wibe.Core.Vdf;

namespace Wibe.Core.Steam;

public sealed record SteamAppManifest(string AppId, string Name, string InstallDir, string? BuildId, long? SizeOnDisk, string LibraryRoot)
{
    public string FullInstallPath => Path.Combine(LibraryRoot, "steamapps", "common", InstallDir);
}

/// <summary>
/// Поиск Steam и установок игр: реестр → libraryfolders.vdf → appmanifest_*.acf.
/// Только чтение, ничего не пишет.
/// </summary>
public static class SteamLocator
{
    public const string DotaAppId = "570";

    public static string? FindSteamRoot()
    {
        foreach (var candidate in RegistryCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return NormalizePath(candidate);
            }
        }

        foreach (var guess in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     @"C:\Steam",
                     @"Z:\Steam",
                 })
        {
            if (Directory.Exists(guess))
            {
                return guess;
            }
        }

        return null;
    }

    public static IReadOnlyList<SteamLibrary> FindLibraries(string steamRoot)
    {
        var libraries = new List<SteamLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, bool isDefault)
        {
            var full = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(full) || !seen.Add(full))
            {
                return;
            }

            libraries.Add(new SteamLibrary(full, isDefault));
        }

        // Классический файл и новый (config/libraryfolders.vdf).
        foreach (var file in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
                 })
        {
            var root = KeyValues.TryParseFile(file);
            var node = root?.Child("libraryfolders") ?? root?.Child("LibraryFolders");
            if (node is null)
            {
                continue;
            }

            foreach (var child in node.Children)
            {
                if (child.IsBlock)
                {
                    var path = child.String("path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        Add(path!, libraries.Count == 0);
                    }

                    continue;
                }

                // Старый формат: "1" "D:\\Games"
                if (!string.IsNullOrWhiteSpace(child.Value))
                {
                    Add(child.Value!, libraries.Count == 0);
                }
            }
        }

        Add(steamRoot, libraries.Count == 0);
        return libraries;
    }

    public static IReadOnlyList<SteamAppManifest> FindApps(string appId)
    {
        var results = new List<SteamAppManifest>();
        var steamRoot = FindSteamRoot();
        if (steamRoot is null)
        {
            return results;
        }

        foreach (var library in FindLibraries(steamRoot))
        {
            var manifestPath = Path.Combine(library.Path, "steamapps", $"appmanifest_{appId}.acf");
            var root = KeyValues.TryParseFile(manifestPath);
            var state = root?.Child("AppState");
            if (state is null)
            {
                continue;
            }

            var installDir = state.String("installdir");
            if (string.IsNullOrWhiteSpace(installDir))
            {
                continue;
            }

            results.Add(new SteamAppManifest(
                state.String("appid") ?? appId,
                state.String("name") ?? $"app_{appId}",
                installDir!,
                state.String("buildid"),
                long.TryParse(state.String("SizeOnDisk"), out var size) ? size : null,
                library.Path));
        }

        return results;
    }

    private static IEnumerable<string?> RegistryCandidates()
    {
        yield return ReadRegistry(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        yield return ReadRegistry(Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        yield return ReadRegistry(Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
    }

    private static string? ReadRegistry(Microsoft.Win32.RegistryHive hive, string subKey, string value)
    {
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Default)
                .OpenSubKey(subKey);
            // HKCU SteamPath на x64-машине может лежать в 32-битном представлении — пробуем и его.
            var result = key?.GetValue(value) as string;
            if (result is not null)
            {
                return result;
            }

            using var x86 = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry32)
                .OpenSubKey(subKey);
            return x86?.GetValue(value) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static string NormalizePath(string path) =>
        Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\');
}

public sealed record SteamLibrary(string Path, bool IsDefault);
