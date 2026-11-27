using Wibe.Core.Dota;
using Wibe.Core.Vpk;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Ландшафт ставится не вместо карты, а на неё: навигация и сущности остаются стоковыми,
/// а внешний вид мира (узлы, расстановка, рендер) берётся из пака ландшафта. Так герой
/// ходит по ПКМ, а карта выглядит как выбранный ландшафт.
/// </summary>
public class TerrainWorldTests
{
    private static DotaInstallation? Install => DotaInstallation.Detect(out _);

    private static string? Terrain(DotaInstallation install, string name)
    {
        var path = Path.Combine(install.DotaDir, "maps", name + ".vpk");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void The_terrain_world_rides_on_the_real_map()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        var stock = Path.Combine(install.DotaDir, "maps", "dota.vpk");
        if (!File.Exists(stock))
        {
            return;
        }

        var terrain = Terrain(install, "dota_coloseum");
        if (terrain is null)
        {
            return;
        }

        // пак ландшафта сам по себе — не карта: ни навигации, ни сущностей
        Assert.False(VpkIndex.IsPlayableMap(terrain));
        Assert.True(TerrainMap.HasTerrainWorld(terrain));

        var dir = Path.Combine(Path.GetTempPath(), "wibe-terrain", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var merged = Path.Combine(dir, "dota.vpk");
        try
        {
            var files = TerrainMap.Build(stock, terrain, merged, out var overlaid, out var added);
            Assert.True(files > 0);
            Assert.True(overlaid > 500, $"подменено файлов мира: {overlaid}");
            Assert.True(added > 0);

            var before = VpkReader.ReadAll(stock);
            var after = VpkReader.ReadAll(merged);
            // карта, навигация и сущности — стоковые байт в байт, иначе герой не ходит
            foreach (var path in new[] { "maps/dota.vmap_c", "maps/dota.gnv", "maps/dota.vhcg", "maps/dota.trm" })
            {
                Assert.True(before.ContainsKey(path), "в стоковой карте нет " + path);
                Assert.True(after.ContainsKey(path), "в собранной карте нет " + path);
                Assert.Equal(before[path].Length, after[path].Length);
                Assert.Equal(before[path], after[path]);
            }

            // мир — из ландшафта
            var source = VpkReader.ReadAll(terrain);
            foreach (var path in new[] { "maps/dota/world.vwrld_c", "maps/dota/world.vrman_c" })
            {
                Assert.Equal(source[path], after[path]);
            }

            var nodes = after.Keys.Count(p => p.StartsWith("maps/dota/worldnodes/", StringComparison.OrdinalIgnoreCase));
            Assert.True(nodes > 800, "узлов мира: " + nodes);
            Assert.True(after.Count > before.Count);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void A_terrain_pack_is_not_a_map_and_is_never_linked_as_one()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        foreach (var name in new[] { "dota_coloseum", "dota_jungle", "dota_reef", "dota_autumn", "dota_winter" })
        {
            var pack = Terrain(install, name);
            if (pack is null)
            {
                continue;
            }

            // ни одна сезонная или премиум-карта в клиенте не пригодна на роль карты целиком
            Assert.False(VpkIndex.IsPlayableMap(pack), name + " выдаёт себя за карту");
            Assert.True(TerrainMap.HasTerrainWorld(pack) || name.Contains("autumn") || name.Contains("winter"),
                name + ": не найден мир для наложения");
        }
    }
}
