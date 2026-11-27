using System.Text;
using Wibe.Core.GameInfo;
using Wibe.Core.Items;
using Wibe.Core.Tools;
using Wibe.Core.Vdf;
using Wibe.Core.Vpk;
using Xunit;

namespace Wibe.Tests;

public class OverlayPipelineTests
{
    private static readonly string Nl = "\n";

    [Fact]
    public void VpkWriter_ProducesReadableVpk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wibe-vpk-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var vpk = Path.Combine(dir, "pak01_dir.vpk");

        var text = Encoding.UTF8.GetBytes("items_game\n{\n\titems\n\t{\n\t}\n}\n");
        var payload = new byte[5000];
        Random.Shared.NextBytes(payload);

        VpkWriter.Write(vpk, new Dictionary<string, byte[]>
        {
            ["scripts/items/items_game.txt"] = text,
            ["models/items/test/test_model.vmdl_c"] = payload,
        });

        Assert.True(new FileInfo(vpk).Length > 5000);

        if (Source2ViewerCli.Locate() is null)
        {
            Directory.Delete(dir, recursive: true);
            return;
        }

        var entries = Source2ViewerCli.ListVpk(vpk);
        Assert.Contains(entries, e => e.Path.Equals("scripts/items/items_game.txt", StringComparison.OrdinalIgnoreCase) && e.Size == text.Length);
        Assert.Contains(entries, e => e.Path.Equals("models/items/test/test_model.vmdl_c", StringComparison.OrdinalIgnoreCase) && e.Size == payload.Length);

        var outDir = Path.Combine(dir, "out");
        var extracted = Source2ViewerCli.ExtractAsset(vpk, "scripts/items/items_game.txt", outDir);
        Assert.Equal(Encoding.UTF8.GetString(text), File.ReadAllText(extracted));

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void KeyValues_RoundtripKeepsStructure()
    {
        var lines = new[]
        {
            "\"items_game\"",
            "{",
            "\t\"items\"",
            "\t{",
            "\t\t\"1\"",
            "\t\t{",
            "\t\t\t\"name\"\t\t\"Anti-Mage's Glaive\"",
            "\t\t\t\"prefab\"\t\t\"default_item\"",
            "\t\t\t\"used_by_heroes\"",
            "\t\t\t{",
            "\t\t\t\t\"npc_dota_hero_antimage\"\t\t\"1\"",
            "\t\t\t}",
            "\t\t}",
            "\t}",
            "}",
            string.Empty,
        };

        var root = KeyValues.Parse(string.Join(Nl, lines));
        var text = KeyValuesWriter.Serialize(root);
        var again = KeyValues.Parse(text);

        var item = again.Child("items_game")?.Child("items")?.Child("1");
        Assert.NotNull(item);
        Assert.Equal("Anti-Mage's Glaive", item!.String("name"));
        Assert.Equal("default_item", item.String("prefab"));
        Assert.Equal("1", item.Child("used_by_heroes")?.String("npc_dota_hero_antimage"));
    }

    [Fact]
    public void ItemsGamePatcher_ReplacesVisualsOfBaseItem()
    {
        var lines = new[]
        {
            "\"items_game\"",
            "{",
            "\t\"items\"",
            "\t{",
            "\t\t\"10\"",
            "\t\t{",
            "\t\t\t\"name\"\t\t\"Axe's Axe\"",
            "\t\t\t\"prefab\"\t\t\"default_item\"",
            "\t\t\t\"model_player\"\t\t\"models/heroes/axe/axe_weapon.vmdl\"",
            "\t\t\t\"used_by_heroes\"",
            "\t\t\t{",
            "\t\t\t\t\"npc_dota_hero_axe\"\t\t\"1\"",
            "\t\t\t}",
            "\t\t}",
            "\t\t\"20\"",
            "\t\t{",
            "\t\t\t\"name\"\t\t\"Days of the Demon - Weapon\"",
            "\t\t\t\"item_slot\"\t\t\"weapon\"",
            "\t\t\t\"model_player\"\t\t\"models/items/axe/oni_weapon/oni_weapon.vmdl\"",
            "\t\t\t\"image_inventory\"\t\t\"econ/items/axe/oni_weapon\"",
            "\t\t\t\"used_by_heroes\"",
            "\t\t\t{",
            "\t\t\t\t\"npc_dota_hero_axe\"\t\t\"1\"",
            "\t\t\t}",
            "\t\t\t\"visuals\"",
            "\t\t\t{",
            "\t\t\t\t\"asset_modifier\"",
            "\t\t\t\t{",
            "\t\t\t\t\t\"type\"\t\t\"model\"",
            "\t\t\t\t\t\"asset\"\t\t\"models/items/axe/oni_weapon/oni_weapon.vmdl\"",
            "\t\t\t\t}",
            "\t\t\t}",
            "\t\t}",
            "\t}",
            "}",
            string.Empty,
        };

        var root = KeyValues.Parse(string.Join(Nl, lines));
        var outcomes = ItemsGamePatcher.Apply(root, new[] { new ItemRemap("npc_dota_hero_axe", "weapon", 20, "oni") });

        Assert.Single(outcomes);
        Assert.True(outcomes[0].Applied);

        var baseItem = root.Child("items_game")!.Child("items")!.Child("10")!;
        Assert.Equal("models/items/axe/oni_weapon/oni_weapon.vmdl", baseItem.String("model_player"));
        Assert.Equal("econ/items/axe/oni_weapon", baseItem.String("image_inventory"));
        Assert.NotNull(baseItem.Child("visuals"));
    }

    [Fact]
    public void GameInfoPatcher_RemovesLegacyPatch()
    {
        var original = string.Join(Nl, new[]
        {
            "\"GameInfo\"",
            "{",
            "\tFileSystem",
            "\t{",
            "\t\tSearchPaths",
            "\t\t{",
            "\t\t\tGame\t\tdota",
            "\t\t}",
            "\t}",
            "}",
            string.Empty,
        });

        // Легаси-патч от старых версий: строка «Game dota_custommod» внутри SearchPaths.
        var legacy = original.Replace("\t\t{", "\t\t{\n\t\t\tGame\tdota_custommod");
        Assert.Contains("dota_custommod", legacy);

        var cleaned = GameInfoPatcher.RemovePatch(legacy);
        Assert.DoesNotContain("dota_custommod", cleaned);
        Assert.Contains("Game\t\tdota", cleaned);

        var crlf = legacy.Replace("\n", "\r\n");
        Assert.DoesNotContain("dota_custommod", GameInfoPatcher.RemovePatch(crlf));
    }

    [Fact]
    public void DotaInstallation_UsesAudioLanguageFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "wibe-lang-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "game", "dota_russian"));

        Assert.Equal("dota_russian", Wibe.Core.Dota.DotaInstallation.ResolveModFolderName(root, "russian"));
        Assert.Equal("dota_english", Wibe.Core.Dota.DotaInstallation.ResolveModFolderName(root, "english"));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ImageUrls_CoverFourTemplates()
    {
        var urls = Wibe.Core.Econ.ItemImages.ItemUrls("econ/items/axe/axe_lava_legion_commander_weapon");

        Assert.Equal(4, urls.Count);
        Assert.Contains(urls, u => u.EndsWith("/dota_react/items/axe/axe_lava_legion_commander_weapon.png", StringComparison.Ordinal));
        Assert.Contains(urls, u => u.Contains("axe_lava_legion_commander_weapon_lg.png", StringComparison.Ordinal));
    }
}
