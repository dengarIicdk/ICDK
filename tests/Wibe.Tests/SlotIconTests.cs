using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Квадратик занятого слота показывает выбранную вещь: в pak06 картинка лежит под стоковым
/// путём слота, байт в байт картинкой выбранного стиля, и другие слоты не трогаются.
/// </summary>
public class SlotIconTests
{
    // Queen of Pain, Horns of the Wicked Succubus: three styles, each with its own alternate icon.
    private const int Horns = 4517;
    private const string SlotIcon = "panorama/images/econ/heroes/queenofpain/hair_png.vtex_c";
    private const string Style2Icon = "panorama/images/econ/items/queenofpain/tpl_hair_lvl2/tpl_hair_lvl2_png.vtex_c";

    [Fact]
    public void Pak06_slot_square_is_the_picked_style_and_no_other_slot_moves()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        Assert.NotNull(catalog.ItemById(Horns));
        var outPath = Path.Combine(Path.GetTempPath(), "wibe-slot-icon.vpk");
        var picks = new List<HeroPicks>
        {
            new(
                "queenofpain",
                new List<(string Slot, int ItemId)> { ("head", Horns) },
                new Dictionary<string, int> { ["head"] = 1 }),
        };

        var result = RustCoreModBuilder.Build(install, catalog, picks, outputOverride: outPath);
        Assert.True(result.Success, result.Message);

        var built = PakBodyIntegrityTests.ReadEntry(outPath, SlotIcon);
        Assert.NotNull(built);
        var stock = PakBodyIntegrityTests.ReadSizes(install.MainPakDir);
        Assert.True(stock.TryGetValue(Style2Icon, out var picked), "нет стоковой картинки выбранного стиля");
        Assert.Equal(picked, built!.LongLength);

        var icons = PakBodyIntegrityTests.ReadSizes(outPath)
            .Keys.Where(p => p.StartsWith("panorama/images/econ/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(new[] { SlotIcon }, icons);
    }
}
