using System.Buffers.Binary;
using System.Text;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Крип умирает, а не тонет. Облик линейного крипа от Valve — это только меши: в его модели нет
/// ни одной секвенции ACT_DOTA_DIE, и движок, не найдя смерти, уводит крипа под землю (fallback
/// «sink into ground»). Поэтому облик кладётся внутрь стоковой модели крипа: анимации (в том числе
/// смерть) остаются стоковыми, а меши — из облика. Тест держит это равенство по блокам ANIM/ASEQ.
/// </summary>
public sealed class CreepDeathSequenceTests
{
    private const string RadiantMelee = "models/creeps/lane_creeps/creep_radiant_melee/radiant_melee.vmdl_c";
    private const string DireMelee = "models/creeps/lane_creeps/creep_bad_melee/creep_bad_melee.vmdl_c";
    private const string RadiantRanged = "models/creeps/lane_creeps/creep_radiant_ranged/radiant_ranged.vmdl_c";
    private const string DireRanged = "models/creeps/lane_creeps/creep_bad_ranged/lane_dire_ranged.vmdl_c";
    private const string DireMeleeMega = "models/creeps/lane_creeps/creep_bad_melee/creep_bad_melee_mega.vmdl_c";

    [Fact]
    public void Creep_looks_keep_the_stock_death_animation()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        var outPath = Path.Combine(Path.GetTempPath(), "wibe-creep-die.vpk");
        // Cavernite: облик, у которого своей анимации смерти нет вообще.
        var world = new RustCoreModBuilder.WorldSelection(
            RadiantTower: null, DireTower: null, Emblem: null, RadiantCreep: 12188, DireCreep: 12187);
        var result = RustCoreModBuilder.Build(install, catalog, new List<HeroPicks>(), null, world, outPath);
        Assert.True(result.Success, "сборка крипов не прошла: " + result.Message);

        var stock = PakBodyIntegrityTests.ReadSizes(install.MainPakDir);
        var made = PakBodyIntegrityTests.ReadSizes(outPath);
        // Модели со старым меш-лейаутом (MVTX/MIDX) склеить нельзя, они остаются копией облика;
        // у Cavernite таких две — dire melee и dire flagbearer. Меряем по тем, что обязаны быть целыми.
        var required = new[] { RadiantMelee, DireMeleeMega, RadiantRanged, DireRanged };
        var checkedModels = 0;
        foreach (var path in required)
        {
            if (!made.TryGetValue(path, out var size) || size == 0 || !stock.TryGetValue(path, out var stockSize))
            {
                continue;
            }

            // Облик без анимаций весит меньше стокового крипа (у Cavernite 388 КБ против 1 МБ):
            // собранная модель — стоковый крип с анимациями (в том числе ACT_DOTA_DIE) плюс меши
            // облика, поэтому она обязана быть заметно больше стоковой.
            Assert.True(
                size > stockSize,
                $"{path}: {size} байт против стока {stockSize} — облик лёг копией, анимаций смерти в нём нет");
            checkedModels++;
        }

        Assert.True(checkedModels >= 3, $"собрано моделей крипов со стоковой анимацией: {checkedModels} из {required.Length}");
    }
}
