using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wibe.Core.Dota;

namespace Wibe.Core.GameInfo;

public sealed record GameInfoStatus(
    bool Patched,
    bool BackupExists,
    string GameInfoPath,
    string? BackupPath,
    DateTimeOffset? PatchedAt);

/// <summary>
/// game/dota/gameinfo.gi больше НЕ патчится: координатор Dota 2 сверяет этот файл перед поиском матча.
/// Класс отвечает только за чистку легаси-патча (если он остался от версий 0.3–0.5).
/// </summary>
public sealed class GameInfoPatcher
{
    public const string ModFolderName = "dota_custommod";

    private const string LegacyPatchLine = "Game dota_custommod";

    private readonly DotaInstallation _install;

    public GameInfoPatcher(DotaInstallation install)
    {
        _install = install;
    }

    public static string StateDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WibeChanger");

    public static string BackupDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "backup");

    public string GameInfoPath => Path.Combine(_install.GameDir, "game", "dota", "gameinfo.gi");

    /// <summary>Языковая папка мода: game/dota_custommod (рядом с game/dota).</summary>
    public string ModFolderPath => Path.Combine(_install.GameDir, "game", ModFolderName);

    public string ModVpkPath => Path.Combine(ModFolderPath, "pak01_dir.vpk");

    public string LanguageGameInfoPath => Path.Combine(ModFolderPath, "gameinfo.gi");

    public static string StatePath => Path.Combine(StateDirectory, "gameinfo-session.json");

    /// <summary>Есть ли легаси-патч в game/dota/gameinfo.gi (должно быть всегда false).</summary>
    public GameInfoStatus Status()
    {
        var text = ReadText();
        var backup = BackupPathFor(GameInfoPath);
        var state = LoadState();
        return new GameInfoStatus(
            text?.Contains(ModFolderName, StringComparison.OrdinalIgnoreCase) ?? false,
            backup is not null && File.Exists(backup),
            GameInfoPath,
            backup,
            state?.PatchedAt);
    }

    /// <summary>Возвращает оригинальный game/dota/gameinfo.gi из бэкапа или убирает строку патча.</summary>
    public (bool Success, string Message) Restore()
    {
        if (DotaInstallation.IsGameRunning())
        {
            return (false, "Dota 2 запущена: закрой игру, чтобы поправить gameinfo.gi");
        }

        var backup = BackupPathFor(GameInfoPath);
        var text = ReadText();

        if (backup is not null && File.Exists(backup))
        {
            var original = File.ReadAllText(backup);
            var hashFile = backup + ".sha256";
            if (File.Exists(hashFile))
            {
                var expected = File.ReadAllText(hashFile).Trim();
                if (!string.Equals(expected, Sha256(original), StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "бэкап gameinfo.gi повреждён (не совпал SHA-256) — восстановление отменено");
                }
            }

            WriteAtomic(GameInfoPath, original);
            try
            {
                File.Delete(backup);
                if (File.Exists(hashFile))
                {
                    File.Delete(hashFile);
                }
            }
            catch (IOException)
            {
                // бэкап удалим при следующем восстановлении
            }

            ClearState();
            return (true, "gameinfo.gi восстановлен из бэкапа");
        }

        if (text is not null && text.Contains(ModFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var cleaned = RemovePatch(text);
            WriteAtomic(GameInfoPath, cleaned);
            ClearState();
            return (true, "строка dota_custommod убрана из gameinfo.gi (бэкапа не было)");
        }

        ClearState();
        return (true, "gameinfo.gi уже чистый");
    }

    /// <summary>Чистка легаси-патча при старте приложения и перед сборкой мода.</summary>
    public bool RestoreIfStale()
    {
        if (!Status().Patched)
        {
            return false;
        }

        if (DotaInstallation.IsGameRunning())
        {
            return false;
        }

        return Restore().Success;
    }

    /// <summary>Активна ли сессия: мод-пак лежит в языковой папке.</summary>
    public bool IsSessionActive => File.Exists(ModVpkPath);

    /// <summary>Удаляет мод-пак и gameinfo.gi языковой папки (сама папка остаётся).</summary>
    public (bool Success, string Message) ClearModFiles()
    {
        var removed = 0;
        try
        {
            foreach (var file in new[] { ModVpkPath, LanguageGameInfoPath })
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                File.Delete(file);
                removed++;
            }
        }
        catch (IOException ex)
        {
            return (false, "не удалось удалить файлы мода: " + ex.Message);
        }

        return (true, removed == 0 ? "мод уже снят" : "мод снят: языковая папка очищена");
    }

    public static string? BackupPathFor(string gameInfoPath)
    {
        if (!File.Exists(gameInfoPath))
        {
            return null;
        }

        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(gameInfoPath.ToLowerInvariant())))[..12];
        return Path.Combine(BackupDirectory, $"gameinfo_{key}.gi.bak");
    }

    private string? ReadText()
    {
        try
        {
            return File.Exists(GameInfoPath) ? File.ReadAllText(GameInfoPath) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Убирает любые строки, ссылающиеся на нашу папку (легаси-патч).</summary>
    public static string RemovePatch(string text)
    {
        // Переводы строк сохраняем байт-в-байт: координатор сверяет файл целиком.
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var kept = text.Split(newline)
            .Where(l => !l.Contains(ModFolderName, StringComparison.OrdinalIgnoreCase));
        return string.Join(newline, kept);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".wibe.tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static GameInfoState? LoadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<GameInfoState>(File.ReadAllText(StatePath))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ClearState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class GameInfoState
    {
        public DateTimeOffset PatchedAt { get; set; }

        public string? BackupPath { get; set; }

        public string? BackupSha256 { get; set; }

        public string? GameInfoPath { get; set; }
    }
}
