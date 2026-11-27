using System.Text;
using Wibe.Core.Items;
using Wibe.Core.Mods;
using Wibe.Core.Vdf;
using Xunit;

namespace Wibe.Tests;

public class PlainFileBuilderTests
{
    [Fact]
    public void BaseCatalog_ContainsThreeRequestedBases()
    {
        Assert.Equal(33, HeroBaseFixesProvider.Known.Count);
        Assert.Equal("bladeform_legacy", HeroBaseFixesProvider.Find(9059)?.Id);
        Assert.Equal("acolyte_of_the_lost_arts", HeroBaseFixesProvider.Find(13042)?.Id);
        Assert.Equal("fists_of_axe_unleashed", HeroBaseFixesProvider.Find(12964)?.Id);
        Assert.True(HeroBaseFixesProvider.Find(13042)?.Persona);
    }

    [Fact]
    public void BlobSha_MatchesGitForEmptyFile()
    {
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391",
            HeroBaseFixesProvider.BlobSha(ReadOnlySpan<byte>.Empty));
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a",
            HeroBaseFixesProvider.BlobSha(Encoding.UTF8.GetBytes("hello\n")));
    }

    [Theory]
    [InlineData("scripts/items/items_game.txt", true)]
    [InlineData("scripts/npc/heroes/npc_dota_hero_axe.txt", true)]
    [InlineData("models/heroes/axe/axe.vmdl_c", false)]
    [InlineData("particles/econ/items/axe/axe.vpcf_c", false)]
    [InlineData("scripts/npc/portraits.txt", false)]
    public void ForbiddenScripts_AreNeverPacked(string path, bool forbidden)
    {
        Assert.Equal(forbidden, PlainFileItemBuilder.IsForbidden(path));
    }

    [Fact]
    public void PersonaLayout_InvokerOwnSlotsMapToStockHosts()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WibeChanger", "cache", "econ", "items_game.txt");
        if (!File.Exists(path))
        {
            return;
        }

        var root = KeyValues.ParseFile(path);
        var itemsRoot = (root.Child("items_game") ?? root).Child("items");
        var selected = itemsRoot!.Children.FirstOrDefault(n => n.Key == "13042");
        Assert.NotNull(selected);
        var number = PersonaLayoutBuilder.PersonaNumber(selected);
        Assert.True(number > 0, $"persona number {number}, slot={PersonaLayoutBuilder.SlotOf(selected!)}");
        var layout = PersonaLayoutBuilder.Build(root, "npc_dota_hero_invoker", 13042);
        Assert.NotNull(layout);
        Assert.Equal(1, layout!.Number);
        Assert.Contains(layout.Parts, p => p.Slot == "head_persona_1" && p.HostModel.Contains("invoker_hair"));
    }

    [Fact]
    public void PortraitSwap_ReplacesWholeBlockAndKeepsNestedBraces()
    {
        const string text = "\"Portraits\"\n{\n\t\"models/heroes/axe/axe.vmdl\"\n\t{\n\t\t\"PortraitHideHero\"\t\t\"0\"\n\t}\n" +
                            "\t\"models/heroes/invoker_kid/invoker_kid.vmdl\"\n\t{\n\t\t\"PortraitHideHero\"\t\t\"1\"\n" +
                            "\t\t\"cameras\"\n\t\t{\n\t\t\t\"default\"\n\t\t\t{\n\t\t\t\t\"PortraitFOV\"\t\t\"20.0\"\n\t\t\t}\n\t\t}\n\t}\n}\n";

        var patched = PortraitTxtPatcher.Apply(text,
            new[] { new PortraitSwap("models/heroes/invoker_kid/invoker_kid.vmdl", "models/heroes/axe/axe.vmdl") });

        Assert.NotNull(patched);
        Assert.Contains("\"models/heroes/axe/axe.vmdl\"", patched);
        Assert.Contains("\"PortraitFOV\"\t\t\"20.0\"", patched);
        Assert.Equal(2, patched!.Split("\"models/heroes/").Length - 1);
    }

    [Fact]
    public void PortraitSwap_AppendsMissingTargetBlock()
    {
        const string text = "\"Portraits\"\n{\n\t\"models/heroes/axe/axe.vmdl\"\n\t{\n\t\t\"PortraitHideHero\"\t\t\"0\"\n\t}\n}\n";
        var patched = PortraitTxtPatcher.Apply(text,
            new[] { new PortraitSwap("models/heroes/axe/axe.vmdl", "models/heroes/axe/axe_arcana.vmdl") });

        Assert.NotNull(patched);
        Assert.Contains("\"models/heroes/axe/axe_arcana.vmdl\"", patched);
    }
}
