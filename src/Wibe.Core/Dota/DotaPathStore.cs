using System.Text;

namespace Wibe.Core.Dota;

/// <summary>Путь к Dota 2, выбранный вручную на сайте. Лежит рядом с настройками примерочной.</summary>
public static class DotaPathStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WibeChanger",
        "dota_dir.txt");

    public static string? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var text = File.ReadAllText(FilePath, Encoding.UTF8).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool Save(string? gameDir, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir))
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, gameDir.Trim(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
