using System.IO.Compression;

namespace Wibe.Core.Tools;

/// <summary>
/// Движок извлечения (Source2Viewer-CLI + его библиотеки) может лежать рядом с exe
/// или быть вшит в сборку как ресурс Wibe.Engine.zip. Тогда у пользователя один файл:
/// при первом обращении архив распаковывается в %LOCALAPPDATA%\WibeChanger\engine.
/// </summary>
public static class EngineBundle
{
    private const string ResourceName = "Wibe.Engine.zip";
    private const string ExeName = "Source2Viewer-CLI.exe";

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "engine");

    public static string InstalledExe => Path.Combine(InstallDirectory, ExeName);

    /// <summary>Последняя ошибка распаковки — для диагностики в /api/state.</summary>
    public static string? LastError { get; private set; }

    public static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "engine.log");

    /// <summary>Гарантирует, что движок есть на диске, и возвращает путь к exe (или null).</summary>
    public static string? Ensure()
    {
        if (File.Exists(InstalledExe))
        {
            return InstalledExe;
        }

        var assembly = typeof(EngineBundle).Assembly;
        var names = assembly.GetManifestResourceNames();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            LastError = "ресурс не найден, есть: " + string.Join(", ", names.Take(8));
            Log(LastError);
            return null;
        }

        var staging = InstallDirectory + ".tmp";
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(staging, overwriteFiles: true);

            if (Directory.Exists(InstallDirectory))
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }

            Directory.Move(staging, InstallDirectory);
            return File.Exists(InstalledExe) ? InstalledExe : null;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            Log(LastError);
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (IOException)
            {
            }

            return null;
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
