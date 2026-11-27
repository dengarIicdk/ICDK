using System.Diagnostics;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Items;
using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Иконка шмотки по требованию: холодный вызов достаёт PNG из pak01_dir.vpk и кладёт в
/// %LOCALAPPDATA%\ICDK\IconCache, тёплый — просто читает файл. Никакой распаковки тысяч иконок
/// при старте: платим только за те карточки, которые игрок реально открыл.
/// </summary>
public sealed class VpkIconEndpointTests
{
    [Fact]
    public void Icons_are_real_png_from_the_game_pak_and_the_second_read_is_free()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        var report = AllHeroItemsScanner.Scan(install, catalog);
        var sample = report.Entries
            .Where(e => e.Icon.Contains("_png.vtex_c", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.ItemId)
            .Select(g => g.First())
            .Take(10)
            .ToList();
        Assert.True(sample.Count >= 5, $"в каталоге мало картинок для проверки: {sample.Count}");

        var cold = new List<long>();
        foreach (var entry in sample)
        {
            var inv = entry.Icon
                .Replace("panorama/images/", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("_png.vtex_c", string.Empty, StringComparison.OrdinalIgnoreCase);

            // холодный: иконки в кэше заведомо нет — путь детерминирован по имени
            var first = VpkIconService.Timed(inv);
            Assert.True(first.Png is not null, $"{entry.Name}: PNG не добыт из пака ({entry.Icon})");
            Assert.True(first.Png!.Length > 64, $"{entry.Name}: подозрительно маленький PNG");
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, first.Png!.Take(4).ToArray());
            cold.Add(first.Ms);

            var second = VpkIconService.Timed(inv);
            Assert.NotNull(second.Png);
            Assert.True(second.Ms <= first.Ms || second.Ms < 50, $"тёплая отдача {second.Ms} мс медленнее холодной {first.Ms} мс");
            Assert.True(second.Ms < 100, $"тёплая отдача {second.Ms} мс — кэш не работает");
        }

        var coldAvg = cold.Average();
        var cacheDir = VpkIconService.CacheDirectory;
        Assert.True(Directory.Exists(cacheDir), "нет папки кэша иконок");
        Assert.True(coldAvg < 10_000, $"средний холодный вызов {coldAvg:F0} мс — слишком долго");
        var cached = Directory.GetFiles(cacheDir, "*.png").Length;
        Console.WriteLine($"vpk-icon: холодный в среднем {coldAvg:F0} мс, файлов в кэше {cached}");
    }

    [Fact]
    public void Catalog_covers_almost_every_item_of_the_game()
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

        Assert.True(report.Valid >= 10_900, $"проверенных шмоток всего {report.Valid}");
        Assert.True(report.BySection.Count >= 15, $"разделов всего {report.BySection.Count}");
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"скан шёл {watch.Elapsed.TotalSeconds:F1} с");
        Assert.True(report.Entries.Select(e => e.HeroKey).Distinct().Count() >= 100,
            $"героев с карточками всего {report.Entries.Select(e => e.HeroKey).Distinct().Count()}");
    }
}
