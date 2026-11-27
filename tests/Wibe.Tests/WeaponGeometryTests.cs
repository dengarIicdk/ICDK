using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Золотые Башеры Anti-Mage должны оставаться человеческого размера: файл оружия
/// собирается из меша самого предмета, без вклейки стокового клинка по чужой bind pose.
/// Стоковый <c>antimage_weapon.vmdl_c</c> — 26 636 байт, оффхенд — 26 676; сборка с
/// вклейкой давала 68 717 и 129 784 байта (2.6× и 4.9×), что и раздувало секиру.
/// </summary>
public sealed class WeaponGeometryTests
{
    private const string Weapon = "models/heroes/antimage/antimage_weapon.vmdl_c";
    private const string Offhand = "models/heroes/antimage/antimage_offhand_weapon.vmdl_c";

    [Fact]
    public void Golden_bashers_stay_the_size_of_the_item_mesh()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        // Башер ищем по модели, а не по локализованному имени: items_game его не отдаёт
        var weapon = catalog.Items.FirstOrDefault(i =>
                         (i.Model ?? string.Empty).Contains("basher", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(i.Slot, "weapon", StringComparison.OrdinalIgnoreCase))
                     ?? catalog.Items.FirstOrDefault(i =>
                         (i.Model ?? string.Empty).Contains("basher", StringComparison.OrdinalIgnoreCase));
        Assert.True(weapon is not null, "в каталоге нет Башера (basher)");
        Console.WriteLine($"башер: {weapon!.Id} {weapon.Name} slot={weapon.Slot} model={weapon.Model}");

        var picks = new List<HeroPicks>
        {
            new("antimage", new List<(string Slot, int ItemId)> { ("weapon", weapon!.Id) }),
        };

        var outPath = Path.Combine(Path.GetTempPath(), "wibe-antimage-basher.vpk");
        var result = RustCoreModBuilder.Build(install, catalog, picks, outputOverride: outPath);
        Assert.True(result.Success, "сборка Anti-Mage не прошла: " + result.Message);

        var made = PakBodyIntegrityTests.ReadSizes(outPath);
        var stock = PakBodyIntegrityTests.ReadSizes(install.MainPakDir);

        // ни одна модель Anti-Mage в сборке не имеет права раздуться против стока:
        // рост = вклейка чужого меша по чужой bind pose (Башеры в 3–5 раз)
        var antimage = made.Where(kv => kv.Key.Contains("antimage", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(antimage.Count > 0, "сборка не содержит моделей Anti-Mage");
        var grown = new List<string>();
        foreach (var (path, size) in antimage)
        {
            if (!stock.TryGetValue(path, out var st) || st == 0)
            {
                continue;
            }

            if (size > st * 3)
            {
                grown.Add($"{path}: {size} б против стока {st} б ({size * 100 / st}%)");
            }
        }

        Assert.True(grown.Count == 0, "меш стокового клинка снова вклеен: " + string.Join("; ", grown));
    }
}
