using System.Text;
using Wibe.Server.Launcher;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Изолированный языковой контейнер мода: <c>game/dota_icdk/</c> с хардлинками официальной
/// русской локализации под родными именами <c>pak01_*</c>, нашим модом как <c>pak02_dir.vpk</c>
/// и картой в <c>maps/dota.vpk</c>. Родной <c>dota_russian/pak01*</c> от Valve не трогается,
/// снятие мода — удаление одной папки.
/// </summary>
public sealed class DotaIcdkMountTests
{
    private static string TempGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "icdk-mount-" + Guid.NewGuid().ToString("N")[..8]);
        var russian = Path.Combine(root, "dota_russian");
        Directory.CreateDirectory(Path.Combine(russian, "maps"));
        Directory.CreateDirectory(Path.Combine(russian, "resource"));

        // «официальные» паки Valve: dir + два тома, один из них 135 (на нём был FATAL ERROR)
        File.WriteAllText(Path.Combine(russian, "pak01_dir.vpk"), "valve-dir");
        File.WriteAllText(Path.Combine(russian, "pak01_000.vpk"), "valve-000");
        File.WriteAllText(Path.Combine(russian, "pak01_135.vpk"), "valve-135");
        File.WriteAllText(Path.Combine(russian, "resource", "localization_russian.txt"), "Привет");

        // наши старые хвосты, которые обязаны вычиститься
        File.WriteAllText(Path.Combine(russian, "pak06_dir.vpk"), "our-old-mod");
        File.WriteAllText(Path.Combine(russian, "maps", "dota.vpk"), "our-old-map");
        return root;
    }

    [Fact]
    public void Mount_keeps_valve_paks_under_native_names_and_puts_our_mod_on_top()
    {
        var game = TempGame();
        var russian = Path.Combine(game, "dota_russian");
        var mod = Path.Combine(game, "mod.vpk");
        var map = Path.Combine(game, "map.vpk");
        File.WriteAllText(mod, "our-mod-pak02");
        File.WriteAllText(map, "our-map");

        try
        {
            var linked = LanguageMount.Mount(game, russian, mod, map);
            var folder = LanguageMount.Folder(game);

            Assert.True(Directory.Exists(folder), "папка dota_icdk не создана");
            Assert.Contains("pak01_dir.vpk", linked);
            Assert.Contains("pak01_000.vpk", linked);
            Assert.Contains("pak01_135.vpk", linked);
            Assert.Contains("pak02_dir.vpk", linked);
            Assert.Contains("maps/dota.vpk", linked);

            // официальные паки Valve лежат под РОДНЫМИ именами — переименования в pak02 нет
            Assert.Equal("valve-dir", File.ReadAllText(Path.Combine(folder, "pak01_dir.vpk")));
            Assert.Equal("valve-000", File.ReadAllText(Path.Combine(folder, "pak01_000.vpk")));
            Assert.Equal("valve-135", File.ReadAllText(Path.Combine(folder, "pak01_135.vpk")));
            Assert.False(File.Exists(Path.Combine(folder, "pak02_135.vpk")),
                "том озвучки переименован в pak02 — движок упадёт на чтении тома");

            // наш мод — следующим номером пакета, поверх pak01_dir.vpk
            Assert.Equal("pak02_dir.vpk", Path.GetFileName(LanguageMount.ModPakPath(game)));
            Assert.Equal("our-mod-pak02", File.ReadAllText(LanguageMount.ModPakPath(game)));
            Assert.Equal("Привет", File.ReadAllText(Path.Combine(folder, "resource", "localization_russian.txt")));
            Assert.Equal("our-map", File.ReadAllText(LanguageMount.MapPath(game)));
            Assert.Equal("-language danish", LanguageMount.LaunchArgument);

            // родной пак Valve не изменился
            Assert.Equal("valve-dir", File.ReadAllText(Path.Combine(russian, "pak01_dir.vpk")));

            // снятие мода: папки нет, dota_russian цел
            Assert.True(LanguageMount.Unmount(game));
            Assert.False(Directory.Exists(folder));
            Assert.True(Directory.Exists(russian));
            Assert.Equal("valve-dir", File.ReadAllText(Path.Combine(russian, "pak01_dir.vpk")));
            Assert.False(LanguageMount.Unmount(game));
        }
        finally
        {
            if (Directory.Exists(game))
            {
                Directory.Delete(game, true);
            }
        }
    }

    [Fact]
    public void Base_game_folder_is_never_treated_as_a_language_source()
    {
        var game = Path.Combine(Path.GetTempPath(), "icdk-base-" + Guid.NewGuid().ToString("N")[..8]);
        var baseDota = Path.Combine(game, "dota");
        Directory.CreateDirectory(Path.Combine(baseDota, "maps"));
        File.WriteAllText(Path.Combine(baseDota, "pak01_dir.vpk"), "valve-base-dir");
        File.WriteAllText(Path.Combine(baseDota, "pak01_135.vpk"), "valve-base-135");
        File.WriteAllText(Path.Combine(baseDota, "maps", "dota.vpk"), "valve-terrain-map");
        File.WriteAllText(Path.Combine(game, "mod.vpk"), "our-mod");

        try
        {
            Assert.False(LanguageMount.IsLanguageFolder(baseDota));
            Assert.True(LanguageMount.IsLanguageFolder(Path.Combine(game, "dota_russian")));

            // Даже если по ошибке передать game/dota, базовая игра не должна пострадать.
            LanguageMount.Mount(game, baseDota, Path.Combine(game, "mod.vpk"), null);
            LanguageMount.Deploy(game, baseDota);

            Assert.Equal("valve-base-dir", File.ReadAllText(Path.Combine(baseDota, "pak01_dir.vpk")));
            Assert.Equal("valve-base-135", File.ReadAllText(Path.Combine(baseDota, "pak01_135.vpk")));
            Assert.Equal("valve-terrain-map", File.ReadAllText(Path.Combine(baseDota, "maps", "dota.vpk")));

            // и в контейнер его паки не попали
            Assert.False(File.Exists(Path.Combine(LanguageMount.Folder(game), "pak01_dir.vpk")));
        }
        finally
        {
            if (Directory.Exists(game))
            {
                Directory.Delete(game, true);
            }
        }
    }

    [Fact]
    public void Stale_pak02_volumes_from_old_versions_are_removed()
    {
        var game = TempGame();
        var russian = Path.Combine(game, "dota_russian");
        var folder = LanguageMount.Folder(game);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pak02_135.vpk"), "broken-old-volume");
        File.WriteAllText(Path.Combine(folder, "pak02_dir.vpk"), "old-mod");
        // мусор от версии, которая линковала базовые паки игры под pak01_9xx
        File.WriteAllText(Path.Combine(folder, "pak01_999.vpk"), "stray-base-pak");
        File.WriteAllText(Path.Combine(game, "mod.vpk"), "new-mod");

        try
        {
            LanguageMount.Mount(game, russian, Path.Combine(game, "mod.vpk"), null);

            Assert.False(File.Exists(Path.Combine(folder, "pak02_135.vpk")),
                "старый том pak02_135.vpk остался — движок снова упадёт");
            Assert.False(File.Exists(Path.Combine(folder, "pak01_999.vpk")),
                "лишний том базовой игры остался в контейнере");
            Assert.Equal("new-mod", File.ReadAllText(LanguageMount.ModPakPath(game)));
            Assert.True(File.Exists(Path.Combine(folder, "pak01_135.vpk")));
        }
        finally
        {
            if (Directory.Exists(game))
            {
                Directory.Delete(game, true);
            }
        }
    }

    [Fact]
    public void Legacy_leftovers_are_cleaned_without_touching_valve_packs()
    {
        var game = TempGame();
        var russian = Path.Combine(game, "dota_russian");
        try
        {
            var removed = LanguageMount.CleanLegacy(russian);

            Assert.Contains("pak06_dir.vpk", removed);
            Assert.Contains("maps/dota.vpk", removed);
            Assert.False(File.Exists(Path.Combine(russian, "pak06_dir.vpk")));
            Assert.False(File.Exists(Path.Combine(russian, "maps", "dota.vpk")));

            // пакеты Valve и локализация не тронуты
            Assert.True(File.Exists(Path.Combine(russian, "pak01_dir.vpk")));
            Assert.True(File.Exists(Path.Combine(russian, "pak01_000.vpk")));
            Assert.True(File.Exists(Path.Combine(russian, "pak01_135.vpk")));
            Assert.Equal("Привет", File.ReadAllText(Path.Combine(russian, "resource", "localization_russian.txt")));
        }
        finally
        {
            if (Directory.Exists(game))
            {
                Directory.Delete(game, true);
            }
        }
    }
}

public sealed class DeployedPakProbeTests
{
    [Fact]
    public void Our_vpk_reader_reads_the_live_mod_pak()
    {
        var pak = @"Z:\steam\steamapps\common\dota 2 beta\game\dota_danish\pak02_dir.vpk";
        if (!File.Exists(pak))
        {
            return;
        }

        var all = Wibe.Core.Vpk.VpkReader.ReadAll(pak);
        var header = string.Join(' ', File.ReadAllBytes(pak).Take(16).Select(b => b.ToString("x2")));
        Assert.True(all.Count > 0, $"VpkReader вернул 0 записей для {pak}; header={header}, size={new FileInfo(pak).Length}");
        Assert.Contains(all.Keys, k => k.EndsWith("_danish.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Localization_is_embedded_into_a_copy_of_the_mod_pak()
    {
        var game = @"Z:\steam\steamapps\common\dota 2 beta\game";
        var pak = Path.Combine(game, "dota_danish", "pak02_dir.vpk");
        if (!File.Exists(pak))
        {
            return;
        }

        var localization = Wibe.Server.Launcher.LanguageMount.CollectLocalization(game);
        Assert.True(localization.Count > 20, $"собрано файлов локализации: {localization.Count}");
        Assert.Contains(localization.Keys, k => k.EndsWith("panorama/localization/dota_danish.txt", StringComparison.OrdinalIgnoreCase) || k.EndsWith("resource/localization/dota_danish.txt", StringComparison.OrdinalIgnoreCase));

        var folder = Path.Combine(Path.GetTempPath(), "icdk-loc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var copy = Path.Combine(folder, "pak02_dir.vpk");
            File.Copy(pak, copy);
            var merged = Wibe.Server.Launcher.LanguageMount.EmbedLocalization(copy, localization);
            Assert.True(merged > 0, $"локализация не влилась: merged={merged}, loc={localization.Count}");
            var all = Wibe.Core.Vpk.VpkReader.ReadAll(copy);
            Assert.Contains(all.Keys, k => k.EndsWith("_danish.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
