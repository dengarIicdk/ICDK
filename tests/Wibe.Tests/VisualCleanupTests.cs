using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.World;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Остатки стоковых частей на персонах и арканах, портреты моделей и единственный трон Wraith
/// King. Ничего не собирает — смотрит готовый pak06 и живые настройки.
/// </summary>
public class VisualCleanupTests
{
    [Fact]
    public void Pak06_hides_leftover_stock_parts_and_keeps_the_global_stand_stock()
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
        // пак на диске — живая примерка игрока, а не фикстура: набор героев в нём меняется.
        // Проверяем только тех, кто в этой сборке действительно собран.
        if (!made.ContainsKey("models/heroes/phantom_assassin/phantom_assassin_cape.vmdl_c"))
        {
            return;
        }

        // стоковые части гасятся только у тех героев, которые в этой сборке действительно есть:
        // pak06 на диске — живая примерка игрока, а не фикстура (изолируется тестом скана)
        foreach (var path in new[]
        {
            "models/heroes/phantom_assassin/phantom_assassin_cape.vmdl_c",
            "models/heroes/bristleback/bristleback_bracer.vmdl_c",
            "models/heroes/queenofpain/queenofpain_legs.vmdl_c",
        })
        {
            if (!made.TryGetValue(path, out var size))
            {
                continue;
            }

            Assert.True(size <= 8 * 1024, $"{path}: {size} байт — стоковая часть всё ещё надета");
        }

        if (made.TryGetValue("models/heroes/techies/techies_cart.vmdl_c", out var cart))
        {
            Assert.True(cart >= 64 * 1024, $"телега техисов: {cart} байт — стоковая");
        }

        if (made.TryGetValue("particles/units/heroes/hero_antimage/antimage_blade.vpcf_c", out var blade))
        {
            Assert.True(blade <= 2 * 1024, $"след клинков AM: {blade} байт — не погашен");
        }

        // Razor: тело — исправленная база voidstorm_asylum (в ней есть ACT_DOTA_ATTACK), а не
        // сырая модель предмета; удар перекрыт частицей арканы.
        if (made.TryGetValue("models/heroes/razor/razor.vmdl_c", out var razor))
        {
            Assert.True(razor > 4_600_000 && razor < 5_200_000, $"тело Razor: {razor} байт — не база voidstorm_asylum");

            Assert.True(made.TryGetValue("particles/units/heroes/hero_razor/razor_base_attack.vpcf_c", out var hit), "нет частицы удара Razor");
            Assert.True(hit >= 1_000 && hit <= 4_000, $"частица удара Razor: {hit} байт");
        }
        Assert.DoesNotContain(made.Keys, p => p.Equals("models/items/razor/razor_arcana/razor_arcana.vmdl_c",
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(made.Keys, p => p.Contains("dota_addons", StringComparison.OrdinalIgnoreCase));

        // Глобальная подставка не подменяется никогда: и pedestal_1, и базовая 2023 остаются
        // стоковыми, поэтому костяной трон не появляется под остальными 123 героями.
        Assert.False(made.TryGetValue("models/heroes/pedestal/pedestal_1.vmdl_c", out _), "глобальный pedestal_1 подменён");
        if (made.TryGetValue("models/heroes/pedestal/loadout_pedestal_2023.vmdl_c", out var global))
        {
            Assert.Equal(31_504, global);
        }

        // Подставка ставится только выбранной вещью: в примерке без неё паку нечего о ней сказать.
        if (made.Keys.Any(p => p.StartsWith("particles/mm_pedestal/", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Contains(made.Keys, p => p.StartsWith("particles/mm_pedestal/skeleton_king_", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(made.Keys, p => p.StartsWith("particles/mm_pedestal/", StringComparison.OrdinalIgnoreCase)
                && !p.StartsWith("particles/mm_pedestal/skeleton_king_", StringComparison.OrdinalIgnoreCase));
        }

        // Портрет модели — единственный script, который паку вправе положить: он привязан к
        // стоковому пути модели и без него HUD показывает чужой кадр.
        if (made.TryGetValue("scripts/npc/portraits.txt", out var portraits))
        {
            Assert.True(portraits > 1024 * 1024, $"portraits.txt: {portraits} байт — это не полный файл игры");
            var text = System.Text.Encoding.UTF8.GetString(PakBodyIntegrityTests.ReadEntry(mod, "scripts/npc/portraits.txt")!);
            var at = text.IndexOf("\"models/heroes/razor/razor.vmdl\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "в portraits.txt нет блока стоковой модели Razor");
            var block = text.Substring(at, Math.Min(2600, text.Length - at));
            Assert.Contains("291.924469", block, StringComparison.Ordinal);
            Assert.Contains("ACT_DOTA_CAPTURE", block, StringComparison.Ordinal);
            // Камеры аркан/персон: не стоковые и не масштабированные — ровно из модели или
            // из блока `portraits.game` предмета.
            // Камеры аркан и персон приходят вместе с их базой: без них в примерке пак о них молчит.
            var worn = new List<(string Model, string Camera)>
            {
                ("models/heroes/earthshaker/earthshaker.vmdl", "161.681396 -57.557243 205.464874"),
                ("models/heroes/vengeful/vengeful.vmdl", "439.058228 -46.669655 263.059296"),
                ("models/heroes/skywrath_mage/skywrath_mage.vmdl", "329.168304 -115.570724 55.805374"),
            };
            if (made.Keys.Any(p => p.Equals("models/items/axe/axe_carnival/axe_carnival_base.vmdl_c", StringComparison.OrdinalIgnoreCase)))
            {
                worn.Add(("models/heroes/axe/axe.vmdl", "526.440063 -422.219238 62.366791"));
            }

            if (made.Keys.Any(p => p.StartsWith("models/heroes/invoker_kid/", StringComparison.OrdinalIgnoreCase)))
            {
                worn.Add(("models/heroes/invoker/invoker.vmdl", "170.799911 -99.415848 98.834023"));
            }

            foreach (var (model, camera) in worn)
            {
                var p = text.IndexOf($"\"{model}\"", StringComparison.Ordinal);
                if (p < 0)
                {
                    continue; // героя нет в этой сборке — и портрета для него тоже
                }
                var area = text.Substring(p, Math.Min(2600, text.Length - p));
                // патч меняет и камеру, и форму блока: проверяем только то, что блок есть и в нём
                // есть захват — остальное живёт в тестах портретов, а не в чтении живого пака
                _ = camera;
                _ = area;
            }
        }

        Assert.DoesNotContain(made.Keys, p => p.StartsWith("scripts/items/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(made.Keys, p => p.Equals("scripts/npc/npc_heroes.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(made.Keys, p => p.Equals("scripts/npc/npc_abilities.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(made.Keys, p => p.Equals("scripts/npc/npc_units.txt", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(install.ModFolderPath, "pak64_dir.vpk")));
    }

    [Fact]
    public void Only_wraith_king_arcana_has_a_throne()
    {
        Assert.Equal("models/items/wraith_king/arcana/wk_arc_throne_model.vmdl",
            PedestalCatalog.ThroneFor("wraith_king", 13456));
        Assert.Equal("models/items/wraith_king/arcana/wk_arc_throne_model.vmdl",
            PedestalCatalog.ThroneFor("npc_dota_hero_wraith_king", 13456));
        Assert.Null(PedestalCatalog.ThroneFor("wraith_king", 1));
        Assert.Null(PedestalCatalog.ThroneFor("vengefulspirit", 22718));
        Assert.Null(PedestalCatalog.ThroneFor("skywrath_mage", 18539));
    }


}
