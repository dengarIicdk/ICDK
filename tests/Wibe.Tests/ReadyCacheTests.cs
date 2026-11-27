using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Готовый кэш: если папка кэша уже собрана (например, Z:/ICDK_AssetVault), модели берутся
/// оттуда без экспорта, а счётчики показывают реальные объёмы.
/// </summary>
public sealed class ReadyCacheTests
{
    private static (int Models, int Icons, long Bytes) Describe(string root)
    {
        if (!Directory.Exists(root))
        {
            return (0, 0, 0);
        }

        var models = Directory.EnumerateFiles(root, "*.glb", SearchOption.AllDirectories).Count();
        var icons = Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).Count();
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // занятый файл пропускаем
            }
        }

        return (models, icons, bytes);
    }

    [Fact]
    public void Existing_cache_folder_is_described_by_real_counts()
    {
        var root = CacheLocator.Root;
        if (!Directory.Exists(root))
        {
            return;
        }

        var (models, icons, bytes) = Describe(root);
        Assert.True(models > 0 || icons > 0, $"в {root} не найдено ни моделей, ни иконок");
        Assert.True(bytes > 1024, "папка кэша пуста по весу");
    }

    [Fact]
    public void Ready_asset_vault_is_used_when_it_is_present()
    {
        const string vault = @"Z:/ICDK_AssetVault";
        if (!Directory.Exists(vault))
        {
            return;
        }

        Environment.SetEnvironmentVariable("WIBE_CACHE", vault);
        CacheLocator.Reset();
        try
        {
            var hero = GlbPreviewService.Hero("Anti-Mage");
            Assert.True(hero.Glb is not null, "готовый кэш не отдал модель героя: " + hero.Error);
            Assert.Equal(new byte[] { 0x67, 0x6C, 0x54, 0x46 }, hero.Glb!.Take(4).ToArray());

            var warm = GlbPreviewService.Hero("Anti-Mage");
            Assert.True(warm.Ms <= 50, $"тёплая отдача из готового кэша {warm.Ms} мс");

            var (models, _, bytes) = Describe(vault);
            Assert.True(models > 0, "в Z:/ICDK_AssetVault нет .glb");
            Assert.True(bytes > 1_000_000, "в Z:/ICDK_AssetVault подозрительно мало данных");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WIBE_CACHE", null);
            CacheLocator.Reset();
        }
    }
}
