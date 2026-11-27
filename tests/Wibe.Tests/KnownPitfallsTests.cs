using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Index;
using Wibe.Core.Sets;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Страховка от уже проходивших багов: эмблема на кольце выбора, ночные формы Night Stalker,
/// череп Pugna под рогами и папка windranger, которую игра зовёт windrunner.
/// </summary>
public class KnownPitfallsTests
{
    private static DotaInstallation? Install => DotaInstallation.Detect(out _);

    /// <summary>Собранный pak и ванильный pak рядом; null, когда собирать было нечего.</summary>
    private static (Dictionary<string, long> Made, Dictionary<string, long> Stock)? Paks()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return null;
        }

        var mod = Path.Combine(install.ModFolderPath, "pak06_dir.vpk");
        if (!File.Exists(mod) || !File.Exists(install.MainPakDir))
        {
            return null;
        }

        return (PakBodyIntegrityTests.ReadSizes(mod), PakBodyIntegrityTests.ReadSizes(install.MainPakDir));
    }

    [Fact]
    public void Emblem_goes_on_the_ring_the_game_draws()
    {
        var paks = Paks();
        if (paks is null)
        {
            return;
        }

        var (made, stock) = paks.Value;
        // эмблему ставит только выбранная вещь: без неё пака её файлов не несёт
        if (!made.ContainsKey("particles/ui_mouseactions/selected_ring.vpcf_c"))
        {
            return;
        }

        Assert.True(
            made["particles/ui_mouseactions/selected_ring.vpcf_c"] > 1024,
            "кольцо выбора записано пустышкой (пустой партикл весит меньше килобайта)");
        if (stock.TryGetValue("particles/ui_mouseactions/selected_ring.vpcf_c", out var clean))
        {
            Assert.NotEqual(clean, made["particles/ui_mouseactions/selected_ring.vpcf_c"]);
        }
    }

    [Fact]
    public void Night_stalker_wears_his_night_parts()
    {
        var paks = Paks();
        if (paks is null)
        {
            return;
        }

        var (made, _) = paks.Value;
        var day = made.Keys
            .Where(p => p.StartsWith("models/heroes/nightstalker/nightstalker_", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("_night", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("/nightstalker_", StringComparison.OrdinalIgnoreCase) || p.Count(c => c == '/') == 3)
            .ToList();
        if (day.Count == 0)
        {
            return;
        }

        foreach (var part in day)
        {
            var night = part[..^".vmdl_c".Length] + "_night.vmdl_c";
            Assert.True(made.ContainsKey(night), $"{part}: нет ночной формы {night}");
        }
    }

    [Fact]
    public void Pugna_head_keeps_the_skull_under_the_horns()
    {
        var paks = Paks();
        if (paks is null)
        {
            return;
        }

        var (made, stock) = paks.Value;
        const string head = "models/heroes/pugna/pugna_head.vmdl_c";
        if (!made.TryGetValue(head, out var mine) || !stock.TryGetValue(head, out var clean))
        {
            return;
        }

        if (clean < 16 * 1024)
        {
            return;
        }

        // рога — дополнение к черепу: собранная модель не может быть меньше стоковой
        Assert.True(mine >= clean, $"{head}: {mine} байт против стока {clean} — череп затёрт рогами");
    }

    [Fact]
    public void Windranger_folder_resolves_to_the_windrunner_hero()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        var index = PakIndex.Load(install);
        var service = new HeroSetService(install, index, catalog);
        var sets = service.Get("windranger");
        Assert.NotNull(sets);
        Assert.True(
            sets!.Slots.Sum(s => s.Items.Count) > 0,
            "папка windranger осталась без слотов: игра зовёт героя windrunner");
    }
}
