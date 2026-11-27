using System.Diagnostics;
using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Items;
using Wibe.Core.Vpk;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// ЗАМОК КАТАЛОГА. Скан всех шмоток Dota 2 идёт по оглавлению pak01_dir.vpk в памяти (секунды,
/// без внешних процессов), и каждый предмет проверяется трижды: модель в паке, слот надевается,
/// картинка карточки в паке. Результат фиксируется в locked_catalog_manifest.json: правка каталога,
/// потерявшая хоть один валидный предмет или добавившая предмет без модели/иконки, роняет тест.
/// Обновление замка только осознанно: WIBE_CATALOG_UPDATE=1.
/// </summary>
public sealed class AllHeroItemsCatalogLockTests
{
    private const string ManifestName = "locked_catalog_manifest.json";

    private static string ManifestPath
    {
        get
        {
            var dir = Directory.GetCurrentDirectory();
            while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return Path.Combine(dir ?? Directory.GetCurrentDirectory(), "tests", "Wibe.Tests", ManifestName);
        }
    }

    [Fact]
    public void Every_scanned_item_has_a_model_a_slot_and_a_picture()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        var watch = Stopwatch.StartNew();
        var report = AllHeroItemsScanner.Scan(install, catalog);
        watch.Stop();

        // скан — чтение оглавления, а не распаковка: секунды, не минуты
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"скан шёл {watch.Elapsed.TotalSeconds:F1} с");
        Assert.True(report.Considered > 5000, $"просмотрено всего {report.Considered} предметов");
        Assert.True(report.Valid > 0, "ни один предмет не прошёл проверку");

        var index = VpkIndex.Paths(install.MainPakDir);
        var broken = new List<string>();
        foreach (var entry in report.Entries)
        {
            if (!index.Contains(entry.Model))
            {
                broken.Add($"{entry.ItemId} {entry.Name}: нет модели {entry.Model}");
            }

            if (!index.Contains(entry.Icon))
            {
                broken.Add($"{entry.ItemId} {entry.Name}: нет картинки {entry.Icon}");
            }

            if (entry.Slot.Length == 0)
            {
                broken.Add($"{entry.ItemId} {entry.Name}: пустой слот");
            }
        }

        Assert.True(broken.Count == 0,
            "в каталог попали предметы без модели/иконки:" + Environment.NewLine + string.Join(Environment.NewLine, broken.Take(15)));

        Assert.DoesNotContain(report.Entries, e => e.Model.Contains("default", StringComparison.OrdinalIgnoreCase) &&
                                                   e.Model.EndsWith("/default_item.vmdl_c", StringComparison.OrdinalIgnoreCase));

        var path = ManifestPath;
        if (Environment.GetEnvironmentVariable("WIBE_CATALOG_UPDATE") == "1" || !File.Exists(path))
        {
            AllHeroItemsScanner.WriteManifest(report, path);
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var locked = doc.RootElement.GetProperty("valid").GetInt32();
        var lockedSections = doc.RootElement.GetProperty("sections").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("count").GetInt32(), StringComparer.OrdinalIgnoreCase);

        Assert.True(
            report.Valid >= locked,
            $"каталог усох: {report.Valid} валидных предметов против {locked} в замке");
        foreach (var (section, count) in lockedSections)
        {
            var now = report.BySection.TryGetValue(section, out var n) ? n : 0;
            Assert.True(now >= count, $"раздел «{section}» усох: {now} против {count}");
        }

        var lockedIds = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("itemId").GetInt32())
            .ToHashSet();
        var missing = lockedIds.Except(report.Entries.Select(e => e.ItemId)).Take(15).ToList();
        Assert.True(missing.Count == 0, "из замка пропали предметы: " + string.Join(", ", missing));
    }
}
