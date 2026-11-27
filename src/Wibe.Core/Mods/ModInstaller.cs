using Wibe.Core.Dota;
using Wibe.Core.Policy;
using Wibe.Core.Tools;

namespace Wibe.Core.Mods;

public sealed record InstallResult(bool Success, string Message, InstalledMod? Mod = null);

public sealed record UninstallResult(bool Success, string Message);

/// <summary>
/// Установка и снятие VPK-модов: извлечь ассет игры → положить по нужному пути → собрать VPK →
/// проверить индекс → скопировать в папку модов → записать в журнал.
/// Ничего не пишет в подписанные файлы игры.
/// </summary>
public sealed class ModInstaller
{
    private readonly DotaInstallation _install;
    private readonly ModJournal _journal;

    public ModInstaller(DotaInstallation install, ModJournal? journal = null)
    {
        _install = install;
        _journal = journal ?? ModJournal.Load();
    }

    public ModJournal Journal => _journal;

    public static string LibraryDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "library");

    public static string TempDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "tmp");

    public InstallResult InstallModel(
        string targetPath,
        string sourceAsset,
        string title,
        string kind = "hero.body",
        string? heroId = null,
        int? slot = null) =>
        InstallFiles(new[] { (targetPath, sourceAsset) }, title, kind, heroId, slot);

    /// <summary>Ставит несколько файлов одним паком (сет: тело + слоты). Первый файл считается основным.</summary>
    public InstallResult InstallFiles(
        IReadOnlyList<(string Target, string Source)> pairs,
        string title,
        string kind = "hero.set",
        string? heroId = null,
        int? slot = null)
    {
        if (pairs.Count == 0)
        {
            return Fail("нечего ставить: пустой список файлов");
        }

        var normalized = pairs
            .Select(p => (Target: NormalizeAssetPath(p.Target), Source: NormalizeAssetPath(p.Source)))
            .GroupBy(p => p.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var target = normalized[0].Target;
        var source = normalized[0].Source;

        if (!_install.ModFolderLayeredOnDota)
        {
            return Fail("папка модов не подключена как LayeredOnMod — установка невозможна");
        }

        if (DotaInstallation.IsGameRunning())
        {
            return Fail("Dota 2 запущена. Закрой игру (Steam → Dota 2 → Выход), иначе новый пак не подхватится и может заклинить загрузку");
        }

        foreach (var pair in normalized)
        {
            var verdict = SafetyPolicy.Evaluate(pair.Target);
            if (verdict == SafetyVerdict.Blocked)
            {
                return Fail($"заблокировано: {SafetyPolicy.Explain(pair.Target)}");
            }
        }

        int slotNumber;
        if (slot is { } requested)
        {
            if (_install.OccupiedSlots.Any(s => s.Number == requested))
            {
                return Fail($"слот pak{requested:00} уже занят");
            }

            slotNumber = requested;
        }
        else
        {
            slotNumber = _install.RecommendedFreeSlot ?? 0;
            if (slotNumber == 0)
            {
                return Fail("нет свободных слотов pak02…pak99");
            }
        }

        var work = Path.Combine(TempDirectory, Guid.NewGuid().ToString("n"));
        var raw = Path.Combine(work, "raw");
        var staging = Path.Combine(work, "staging");
        var vpkTemp = Path.Combine(work, $"pak{slotNumber:00}_dir.vpk");

        try
        {
            Directory.CreateDirectory(raw);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(TempDirectory);

            long size = 0;
            uint crc = 0;

            foreach (var pair in normalized)
            {
                var extracted = Source2ViewerCli.ExtractAsset(_install.MainPakDir, pair.Source, raw);
                var fileSize = new FileInfo(extracted).Length;
                if (size == 0)
                {
                    size = fileSize;
                    crc = Crc32.ComputeFile(extracted);
                }

                var targetFile = Path.Combine(staging, pair.Target.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(extracted, targetFile, overwrite: true);
            }

            Source2ViewerCli.CreateVpk(staging, vpkTemp);

            var built = Source2ViewerCli.ListVpk(vpkTemp);
            foreach (var pair in normalized)
            {
                var entry = built.FirstOrDefault(e => string.Equals(e.Path, pair.Target, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    return Fail($"VPK собрался, но {pair.Target} в нём не нашёлся — установка отменена");
                }
            }

            Directory.CreateDirectory(LibraryDirectory);
            var backup = Path.Combine(LibraryDirectory, $"pak{slotNumber:00}_{Sanitize(title)}.vpk");
            File.Copy(vpkTemp, backup, overwrite: true);

            var destination = Path.Combine(_install.ModFolderPath, $"pak{slotNumber:00}_dir.vpk");
            File.Copy(vpkTemp, destination, overwrite: false);

            var mod = new InstalledMod
            {
                Id = Sanitize($"{kind}-{heroId ?? target}"),
                Slot = slotNumber,
                Kind = kind,
                Title = title,
                TargetPath = target,
                SourcePath = source,
                SourceCrc = crc,
                Size = size,
                VpkPath = destination,
                BackupPath = backup,
                HeroId = heroId,
                Files = normalized.Select(p => p.Target).ToList(),
            };

            _journal.Upsert(mod);
            _journal.Save();
            _install.ScanSlots();

            var suffix = normalized.Count > 1 ? $" ({normalized.Count} файлов)" : string.Empty;
            return new InstallResult(true, $"установлено в pak{slotNumber:00}: {title}{suffix}", mod);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or FileNotFoundException)
        {
            return Fail(ex.Message);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    public UninstallResult Uninstall(int slot, bool deleteBackup = false)
    {
        if (DotaInstallation.IsGameRunning())
        {
            return new UninstallResult(false, "Dota 2 запущена — закрой игру перед снятием мода");
        }

        var mod = _journal.BySlot(slot);
        var pakPath = Path.Combine(_install.ModFolderPath, $"pak{slot:00}_dir.vpk");
        if (mod is null && !File.Exists(pakPath))
        {
            return new UninstallResult(false, $"pak{slot:00} не установлен этим приложением и файла нет");
        }

        try
        {
            if (File.Exists(pakPath))
            {
                File.Delete(pakPath);
            }
        }
        catch (IOException ex)
        {
            return new UninstallResult(false, $"не удалось удалить {Path.GetFileName(pakPath)}: {ex.Message}");
        }

        if (mod is not null)
        {
            if (deleteBackup && mod.BackupPath is not null && File.Exists(mod.BackupPath))
            {
                try
                {
                    File.Delete(mod.BackupPath);
                }
                catch (IOException)
                {
                }
            }

            _journal.RemoveBySlot(slot);
            _journal.Save();
        }

        _install.ScanSlots();
        return new UninstallResult(true, $"pak{slot:00} снят");
    }

    public static string NormalizeAssetPath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    private static InstallResult Fail(string message) => new(false, message);

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
