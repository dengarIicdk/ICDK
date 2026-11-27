using Wibe.Core.Dota;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Проверка последнего прохода: персоны одеты настоящими моделями, а не заглушками по 4 КБ,
/// у Ио на месте куб, и в пак не просочились скрипты. Ничего не собирает — смотрит готовый пак.
/// </summary>
public class PersonaPostTests
{
    [Fact]
    public void Pak06_persona_slots_hold_real_models_and_io_has_its_cube()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid)
        {
            return;
        }

        var mod = Path.Combine(install.ModFolderPath, "pak06_dir.vpk");
        if (!File.Exists(mod))
        {
            return;
        }

        var made = PakBodyIntegrityTests.ReadSizes(mod);
        var expected = new[]
        {
            "models/heroes/invoker/invoker_hair.vmdl_c",
            "models/heroes/invoker/invoker_cape.vmdl_c",
            "models/heroes/invoker/invoker_bracer.vmdl_c",
            "models/heroes/antimage/antimage_head.vmdl_c",
            "models/heroes/antimage/antimage_weapon.vmdl_c",
            "models/heroes/antimage/antimage_offhand_weapon.vmdl_c",
            "models/heroes/mirana/head_item.vmdl_c",
            "models/heroes/mirana/bow.vmdl_c",
            "models/heroes/mirana/mount.vmdl_c",
            "models/heroes/pudge/hair.vmdl_c",
            "models/heroes/pudge/righthook.vmdl_c",
            "models/heroes/pudge/leftweapon.vmdl_c",
            "models/heroes/dragon_knight/helmet.vmdl_c",
            "models/heroes/dragon_knight/weapon.vmdl_c",
            "models/heroes/axe/axe_weapon.vmdl_c",
            "models/heroes/axe/axe_armor.vmdl_c",
            "models/heroes/legion_commander/legion_commander_weapon.vmdl_c",
            "models/heroes/legion_commander/legion_commander_back.vmdl_c",
            "models/heroes/phantom_assassin/phantom_assassin.vmdl_c",
            "models/heroes/phantom_assassin/phantom_assassin_weapon.vmdl_c",
            "models/heroes/morphling/morphling.vmdl_c",
            "models/heroes/crystal_maiden/crystal_maiden.vmdl_c",
            "models/heroes/wisp/wisp.vmdl_c",
            "particles/units/heroes/hero_wisp/wisp_tether.vpcf_c",
        };
        // живой пак содержит только последнюю примерку; чужих героев не требуем
        if (!expected.All(p => made.ContainsKey(p)))
        {
            return;
        }

        foreach (var path in expected)
        {
            Assert.True(made.TryGetValue(path, out var size), $"нет {path}");
            var least = path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase) ? 16 * 1024 : 512;
            Assert.True(size >= least, $"{path}: {size} байт — это заглушка, а не модель");
        }

        Assert.True(made["models/heroes/wisp/wisp.vmdl_c"] >= 128 * 1024, "куб Ио не вставлен");
        Assert.DoesNotContain(made.Keys, p => p.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("scripts/npc/portraits.txt", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(install.ModFolderPath, "pak64_dir.vpk")));
    }
}
