using Wibe.Core.Dota;
using Wibe.Core.Policy;
using Wibe.Core.Vpk;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Страховка по управлению: ПКМ-ходьба, хоткеи способностей и камера живут в конфигах
/// игрока и в HUD-файлах. Ни мод, ни лаунчер не имеют права их подменять или трогать,
/// а карту мода можно ставить вместо стоковой только если это настоящая проходимая карта
/// (с сеткой навигации и блоком сущностей), иначе герой не ходит по ПКМ.
/// </summary>
public class InputSafetyTests
{
    private static DotaInstallation? Install => DotaInstallation.Detect(out _);

    private static readonly string[] ForbiddenRoots =
    {
        "panorama/layout/", "panorama/scripts/", "panorama/styles/",
        "cfg/", "events/", "scripts/vscripts/", "scripts/items/", "scripts/npc/npc_heroes",
    };

    [Fact]
    public void Pak06_never_carries_hud_layouts_cfg_or_scripts()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        var pak = Path.Combine(install.ModFolderPath, "pak06_dir.vpk");
        if (!File.Exists(pak))
        {
            return;
        }

        var paths = VpkIndex.Paths(pak);
        Assert.NotEmpty(paths);
        foreach (var root in ForbiddenRoots)
        {
            Assert.DoesNotContain(paths, p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        }

        // и вообще: разрешены только ресурсы, ничего исполняемого
        var allowed = new[] { "models/", "particles/", "materials/", "sounds/", "soundevents/", "panorama/images/", "scripts/npc/portraits.txt" };
        foreach (var path in paths)
        {
            Assert.True(
                allowed.Any(a => path.StartsWith(a, StringComparison.OrdinalIgnoreCase)),
                $"в паке посторонний файл: {path}");
        }
    }

    [Fact]
    public void A_terrain_pack_is_never_put_in_place_of_the_map()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        // карта ландшафта, если она стоит, обязана быть проходимой картой
        var placed = Path.Combine(install.ModFolderPath, "maps", "dota.vpk");
        if (File.Exists(placed))
        {
            Assert.True(VpkIndex.IsPlayableMap(placed), "вместо карты положен пак ландшафта без навигации и сущностей");
        }

        var maps = Path.Combine(install.DotaDir, "maps");
        if (!Directory.Exists(maps))
        {
            return;
        }

        // а те пакы, которые ландшафт называет картой, проходимыми не являются:
        // в них нет ni maps/dota.gnv, ни maps/dota.vhcg — именно из-за такого файла
        // герой перестаёт ходить по ПКМ, а камера падает к земле.
        var colosseum = Path.Combine(maps, "dota_coloseum.vpk");
        if (File.Exists(colosseum))
        {
            Assert.False(VpkIndex.IsPlayableMap(colosseum), "ландшафтный пак внезапно стал проходимой картой");
        }

        var jungle = Path.Combine(maps, "dota_jungle.vpk");
        if (File.Exists(jungle))
        {
            Assert.False(VpkIndex.IsPlayableMap(jungle));
        }

        var stock = Path.Combine(maps, "dota.vpk");
        if (File.Exists(stock))
        {
            Assert.True(VpkIndex.IsPlayableMap(stock), "стоковая карта обязана быть проходимой");
        }
    }

    [Fact]
    public void Safety_policy_blocks_the_hud_and_the_player_configs()
    {
        foreach (var path in new[]
                 {
                     "cfg/user_keys_default.vcfg",
                     "cfg/machine_convars.vcfg",
                     "panorama/layout/hud/dota_hud.xml",
                     "panorama/scripts/hud/hud.js",
                     "panorama/styles/hud.css",
                     "events/game_events.res",
                     "scripts/items/items_game.txt",
                     "scripts/vscripts/abilities/foo.lua",
                 })
        {
            Assert.Equal(SafetyVerdict.Blocked, SafetyPolicy.Evaluate(path));
        }

        // а обычная косметика проходит
        Assert.NotEqual(SafetyVerdict.Blocked, SafetyPolicy.Evaluate("models/items/wraith_king/arcana/wraith_king_arcana_head.vmdl_c"));
        Assert.NotEqual(SafetyVerdict.Blocked, SafetyPolicy.Evaluate("particles/econ/items/wraith_king/wraith_king_arcana/wraith_king_ambient.vpcf_c"));
    }

    [Fact]
    public void Nothing_in_the_app_writes_the_players_configs()
    {
        // правилу «не трогать живые конфиги» мало комментария: ни одна строка исходников
        // не должна вообще упоминать файлы биндов, камеры и userdata
        var root = AppRoot();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.rs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(files);

        var forbidden = new[]
        {
            "user_keys_default.vcfg", "user_keys_dev_default.vcfg", "machine_convars.vcfg",
            "machine_convars_default.vcfg", "dotakeys_personal.lst", "dota_camera_distance",
            "config.cfg",
        };

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var name in forbidden)
            {
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    // единственное исключение — этот же тест и его список
                    Assert.True(
                        file.EndsWith("InputSafetyTests.cs", StringComparison.OrdinalIgnoreCase),
                        $"{file} упоминает {name}: лаунчер не должен трогать конфиги игрока");
                }
            }
        }
    }

    [Fact]
    public void The_launch_arguments_do_not_reset_the_input()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        var args = Wibe.Core.Mods.ModSession.LaunchArguments(conDebug: false);
        foreach (var bad in new[] { "-autoconfig", "-tools", "-toolsmode", "+exec", "+dota_camera_distance", "-override_vpk", "-enable_addons" })
        {
            Assert.DoesNotContain(bad, args, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("-novid", args);
    }

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WibeFittingRoom.sln"))
                                && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
