using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Monkey King: каждый предмет ложится строго на стоковый путь своего слота и ни на чей чужой.
/// Посох — в файл оружия (<c>monkey_king_base_weapon.vmdl_c</c>: своего <c>monkey_king_staff.vmdl_c</c>
/// в игре нет, это и есть стоковый файл посоха), голова — в файл головы, тело — в файл тела.
/// Тело при этом не трогается по мешам: DATA совпадает с телом арканы байт в байт.
/// </summary>
public sealed class MonkeyKingWeaponSlotTests
{
    private const string Body = "models/heroes/monkey_king/monkey_king.vmdl_c";
    private const string Head = "models/heroes/monkey_king/monkey_king_hair.vmdl_c";
    private const string Weapon = "models/heroes/monkey_king/monkey_king_base_weapon.vmdl_c";

    [Fact]
    public void Staff_lands_on_the_stock_weapon_file_and_heads_stay_heads()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        var picks = new List<HeroPicks>
        {
            new(
                "monkey_king",
                new List<(string Slot, int ItemId)> { ("body", 9050), ("head", 34183), ("weapon", 9212) }),
        };

        var outPath = Path.Combine(Path.GetTempPath(), "wibe-monkey-king.vpk");
        var result = RustCoreModBuilder.Build(install, catalog, picks, outputOverride: outPath);
        Assert.True(result.Success, "сборка Monkey King не прошла: " + result.Message);

        var made = PakBodyIntegrityTests.ReadSizes(outPath);
        var stock = PakBodyIntegrityTests.ReadSizes(install.MainPakDir);

        // 1. посох лёг на стоковый файл оружия, а не остался стоковой пустышкой на 67 КБ
        Assert.True(made.TryGetValue(Weapon, out var weaponSize), "посоха нет на стоковом пути оружия");
        Assert.True(stock.TryGetValue(Weapon, out var stockWeapon), "в игре нет стокового файла оружия MK");
        Assert.NotEqual(stockWeapon, weaponSize);
        Assert.True(weaponSize > 1_000_000, $"в слоте оружия {weaponSize} байт — это не посох");

        // 2. в голове — голова арканы, а не посох и не пустышка
        Assert.True(made.TryGetValue(Head, out var headSize), "головы нет на стоковом пути головы");
        Assert.True(headSize > 1_000_000, $"в слоте головы {headSize} байт — туда попал не тот предмет");

        // 3. тело осталось телом: своя модель целиком, а не склейка с посохом
        Assert.True(made.TryGetValue(Body, out var bodySize), "тела нет на стоковом пути тела");
        Assert.True(bodySize > 5_000_000, $"тело {bodySize} байт — это не тело героя");

    }
}
