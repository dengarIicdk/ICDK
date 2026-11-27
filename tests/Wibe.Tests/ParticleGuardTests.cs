using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// След на реке: витринные частицы героя и предметов не должны жить в бою без своей модели.
/// Собранный пак обязан положить стоковую loadout-частицу пустышкой.
/// </summary>
public class ParticleGuardTests
{
    private const string QopLoadout = "particles/units/heroes/hero_queenofpain/queenofpain_loadout.vpcf_c";
    private const string Empty = "particles/dev/empty_particle.vpcf_c";

    [Fact]
    public void Qop_arcana_style_2_leaves_no_live_loadout_particle()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        Assert.NotNull(catalog.ItemById(12930));
        var outPath = Path.Combine(Path.GetTempPath(), "wibe-qop-phantom.vpk");
        var picks = new List<HeroPicks>
        {
            new(
                "queenofpain",
                new List<(string Slot, int ItemId)> { ("body", 12930) },
                new Dictionary<string, int> { ["body"] = 1 }),
        };

        var result = RustCoreModBuilder.Build(install, catalog, picks, outputOverride: outPath);
        Assert.True(result.Success, result.Message);

        var sizes = PakBodyIntegrityTests.ReadSizes(outPath);
        var stock = PakBodyIntegrityTests.ReadSizes(install.MainPakDir);
        Assert.True(stock.TryGetValue(Empty, out var empty), "в игре нет пустой частицы");
        Assert.True(sizes.TryGetValue(QopLoadout, out var built), $"нет {QopLoadout}");
        Assert.Equal(empty, built);
    }

    [Fact]
    public void Pudge_taunt_moves_the_chosen_dance_into_his_body()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        var outPath = Path.Combine(Path.GetTempPath(), "wibe-pudge-taunt.vpk");
        var picks = new List<HeroPicks>
        {
            new(
                "pudge",
                new List<(string Slot, int ItemId)> { ("taunt", 9722) },
                new Dictionary<string, int>(),
                RareTaunt: true),
        };

        var result = RustCoreModBuilder.Build(install, catalog, picks, outputOverride: outPath);
        Assert.True(result.Success, result.Message);
        var sizes = PakBodyIntegrityTests.ReadSizes(outPath);
        Assert.True(sizes.TryGetValue("models/heroes/pudge/pudge.vmdl_c", out var body), "тело Pudge не собрано");
        Assert.True(body > 4 * 1024 * 1024, $"тело Pudge: {body} байт");
        Assert.DoesNotContain(sizes.Keys, p => p.EndsWith("items_game.txt", StringComparison.OrdinalIgnoreCase));
    }
}
