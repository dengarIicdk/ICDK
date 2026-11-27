using Wibe.Core.Policy;
using Wibe.Core.Tools;
using Wibe.Core.Vdf;
using Xunit;

namespace Wibe.Tests;

public class KeyValuesTests
{
    [Fact]
    public void Parses_boot_vcfg()
    {
        const string text = """
            "boot"
            {
            	"AudioLanguage"		"russian"
            	"UILanguage"		"russian"
            }
            """;

        var root = KeyValues.Parse(text);
        Assert.Equal("russian", root.Child("boot")?.String("AudioLanguage"));
    }

    [Fact]
    public void Parses_app_manifest_without_shifting_values()
    {
        // Реальный порядок полей appmanifest_570.acf: installdir идёт перед LastUpdated.
        const string text = """
            "AppState"
            {
            	"appid"		"570"
            	"name"		"Dota 2"
            	"installdir"		"dota 2 beta"
            	"LastUpdated"		"1795222382"
            	"SizeOnDisk"		"83911139675"
            	"buildid"		"26825343"
            	"InstalledDepots"
            	{
            		"373301"
            		{
            			"manifest"		"3697999485771247811"
            		}
            	}
            }
            """;

        var state = KeyValues.Parse(text).Child("AppState");
        Assert.NotNull(state);
        Assert.Equal("dota 2 beta", state!.String("installdir"));
        Assert.Equal("26825343", state.String("buildid"));
        Assert.Equal("Dota 2", state.String("name"));
        Assert.Equal(1, state.Child("InstalledDepots")?.Children.Count);
    }

    [Fact]
    public void Parses_libraryfolders_new_format()
    {
        const string text = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"Z:\\steam"
            		"label"		""
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """;

        var node = KeyValues.Parse(text).Child("libraryfolders")!;
        var paths = node.Children.Select(c => c.String("path")).ToList();
        Assert.Equal(new[] { "Z:\\steam", "D:\\SteamLibrary" }, paths);
    }

    [Fact]
    public void Parses_libraryfolders_old_format()
    {
        const string text = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"123"
            	"1"		"D:\\Games"
            }
            """;

        var node = KeyValues.Parse(text).Child("LibraryFolders")!;
        Assert.Contains(node.Children, c => c.Value == "D:\\Games");
    }
}

public class Source2ViewerCliTests
{
    [Fact]
    public void Parses_vpk_list_output()
    {
        const string output = """
            models/heroes/axe/axe.vmdl_c CRC:005890d3a8 size:1314278
            materials/default/default_normal_tga_7652cb.vtex_c CRC:00bb8fc1c5 size:1972
            """;

        var entries = Source2ViewerCli.Parse(output);
        Assert.Equal(2, entries.Count);
        Assert.Equal("models/heroes/axe/axe.vmdl_c", entries[0].Path);
        Assert.Equal(0x5890d3a8u, entries[0].Crc);
        Assert.Equal(1314278, entries[0].Size);
    }
}

public class SafetyPolicyTests
{
    [Theory]
    [InlineData("scripts/items/items_game.txt")]
    [InlineData("scripts/npc/npc_units.txt")]
    [InlineData("gameinfo.gi")]
    [InlineData("bin/win64/client.dll")]
    [InlineData("models/heroes/axe/client.dll")]
    [InlineData("maps/dota/worldnodes/tree_01.vmdl_c")]
    [InlineData("models/props_tree/tree_oak.vmdl_c")]
    public void Blocks_dangerous_paths(string path)
    {
        Assert.Equal(SafetyVerdict.Blocked, SafetyPolicy.Evaluate(path));
    }

    [Theory]
    [InlineData("models/heroes/axe/axe.vmdl_c")]
    [InlineData("models/courier/greevil/greevil.vmdl_c")]
    [InlineData("models/items/wards/alliance_ward/alliance_ward.vmdl_c")]
    public void Allows_cosmetics(string path)
    {
        Assert.Equal(SafetyVerdict.Allowed, SafetyPolicy.Evaluate(path));
    }

    [Theory]
    [InlineData("models/props_structures/dire_tower002.vmdl_c")]
    [InlineData("models/creeps/lane_creeps/creep_2021_dire/creep_2021_dire_melee.vmdl_c")]
    [InlineData("materials/models/props_tree/tree_oak_leaves_06_refl_psd_d7dd784b.vtex_c")]
    public void Warns_on_category_b(string path)
    {
        Assert.Equal(SafetyVerdict.Warn, SafetyPolicy.Evaluate(path));
    }

    [Fact]
    public void Normalizes_backslashes_and_case()
    {
        Assert.Equal(SafetyVerdict.Blocked, SafetyPolicy.Evaluate("SCRIPTS\\Items\\items_game.txt"));
    }
}
