using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Ленивый 3D-экспорт: тело героя и модель предмета выгружаются в бинарный glTF из пака
/// игры только по запросу, а повторный запрос идёт из дискового кэша.
/// </summary>
public sealed class GlbPreviewServiceTests
{
    [Fact]
    public void Hero_body_and_golden_basher_are_exported_as_binary_gltf_and_cached()
    {
        var hero = GlbPreviewService.Hero("Anti-Mage");
        Assert.True(hero.Glb is not null, "тело Anti-Mage не экспортировано: " + hero.Error);
        Assert.True(hero.Glb!.Length > 1024, $"слишком маленький glb тела: {hero.Glb.Length}");
        Assert.Equal(new byte[] { 0x67, 0x6C, 0x54, 0x46 }, hero.Glb!.Take(4).ToArray());

        var warmHero = GlbPreviewService.Hero("Anti-Mage");
        Assert.NotNull(warmHero.Glb);
        Assert.True(warmHero.Ms <= 50, $"тёплый герой отдаётся за {warmHero.Ms} мс — кэш не работает");

        var install = Wibe.Core.Dota.DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid)
        {
            return;
        }

        var catalog = Wibe.Core.Econ.EconCatalog.Load(install);
        var basher = catalog.Items.FirstOrDefault(i => (i.Model ?? string.Empty).Contains("basher", StringComparison.OrdinalIgnoreCase));
        if (basher is null)
        {
            return;
        }

        var item = GlbPreviewService.Item(basher.Id);
        Assert.True(item.Glb is not null, "Башер не экспортирован: " + item.Error);
        Assert.Equal(new byte[] { 0x67, 0x6C, 0x54, 0x46 }, item.Glb!.Take(4).ToArray());

        var warmItem = GlbPreviewService.Item(basher.Id);
        Assert.NotNull(warmItem.Glb);
        Assert.True(warmItem.Ms <= 50, $"тёплый предмет отдаётся за {warmItem.Ms} мс — кэш не работает");

        Assert.True(Directory.Exists(GlbPreviewService.CacheDirectory));
        Assert.True(Directory.GetFiles(GlbPreviewService.CacheDirectory, "*_meshinfo.json").Length > 0, "сайдкар meshinfo не создан");
    }
}
