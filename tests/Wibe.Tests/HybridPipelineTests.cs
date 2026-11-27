using Wibe.Core.Items;
using Wibe.Core.Vdf;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Гибридный конвейер: npc_heroes (модель героя) + items_game (перенос персонажных
/// деталей в основные слоты). Тесты синтетические, без установленной Dota.
/// </summary>
public class HybridPipelineTests
{
    private const string Nl = "\n";

    [Fact]
    public void HeroModelPatcher_ReplacesTopLevelModel_AndKeepsPersonaBlock()
    {
        var text = string.Join(Nl, new[]
        {
            "\"DOTAHeroes\"",
            "{",
            "\t\"npc_dota_hero_invoker\"",
            "\t{",
            "\t\t\"Model\"\t\t\"models/heroes/invoker/invoker.vmdl\"",
            "\t\t\"Persona\"",
            "\t\t{",
            "\t\t\t\"1\"",
            "\t\t\t{",
            "\t\t\t\t\"Model\"\t\t\"models/heroes/invoker_kid/invoker_kid.vmdl\"",
            "\t\t\t}",
            "\t\t}",
            "\t}",
            "}",
            string.Empty,
        });

        var root = KeyValues.Parse(text);
        Assert.True(HeroModelPatcher.Apply(root, "npc_dota_hero_invoker", "models/heroes/invoker_kid/invoker_kid.vmdl_c"));

        var hero = root.Child("DOTAHeroes")!.Child("npc_dota_hero_invoker")!;
        Assert.Equal("models/heroes/invoker_kid/invoker_kid.vmdl", hero.String("Model"));
        Assert.Equal(
            "models/heroes/invoker_kid/invoker_kid.vmdl",
            hero.Child("Persona")!.Child("1")!.String("Model"));
    }

    [Fact]
    public void ApplyPersonaParts_TransfersNativeParts_AndClearsAdultSlots()
    {
        var text = string.Join(Nl, new[]
        {
            "\"items_game\"",
            "{",
            "\t\"items\"",
            "\t{",
            "\t\t\"1\"",
            "\t\t{",
            "\t\t\t\"name\"\t\t\"Body\"",
            "\t\t\t\"prefab\"\t\t\"default_item\"",
            "\t\t\t\"item_slot\"\t\t\"hero_base\"",
            "\t\t\t\"model_player\"\t\t\"models/heroes/invoker/invoker.vmdl\"",
            "\t\t\t\"used_by_heroes\"",
            "\t\t\t{",
            "\t\t\t\t\"npc_dota_hero_invoker\"\t\t\"1\"",
            "\t\t\t}",
            "\t\t}",
            "\t\t\"2\"",
            "\t\t{",
            "\t\t\t\"name\"\t\t\"Head\"",
            "\t\t\t\"prefab\"\t\t\"default_item\"",
            "\t\t\t\"item_slot\"\t\t\"head\"",
            "\t\t\t\"model_player\"\t\t\"models/heroes/invoker/invoker_hair.vmdl\"",
            "\t\t\t\"used_by_heroes\"",
            "\t\t\t{",
            "\t\t\t\t\"npc_dota_hero_invoker\"\t\t\"1\"",
            "\t\t\t}",
            "\t\t}",
            "\t\t\"3\"",
            "\t\t{",
            "\t\t\t\"name\"\t\t\"Kid Head\"",
            "\t\t\t\"prefab\"\t\t\"default_item\"",
            "\t\t\t\"item_slot\"\t\t\"head_persona_1\"",
            "\t\t\t\"model_player\"\t\t\"models/heroes/invoker_kid/invoker_kid_hair.vmdl\"",
            "\t\t\t\"visuals\"",
            "\t\t\t{",
            "\t\t\t\t\"asset_modifier\"",
            "\t\t\t\t{",
            "\t\t\t\t\t\"type\"\t\t\"entity_model\"",
            "\t\t\t\t\t\"modifier\"\t\t\"models/heroes/invoker_kid/invoker_kid_hair.vmdl\"",
            "\t\t\t\t}",
            "\t\t\t}",
            "\t\t\t\"used_by_heroes\"",
            "\t\t\t{",
            "\t\t\t\t\"npc_dota_hero_invoker\"\t\t\"1\"",
            "\t\t\t}",
            "\t\t}",
            "\t}",
            "}",
            string.Empty,
        });

        var root = KeyValues.Parse(text);
        var changed = ItemsGamePatcher.ApplyPersonaParts(
            root,
            "npc_dota_hero_invoker",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["head"] = "models/heroes/invoker_kid/invoker_kid_hair.vmdl_c",
            });

        var items = root.Child("items_game")!.Child("items")!;
        Assert.Equal(1, changed);
        Assert.Equal("models/heroes/invoker_kid/invoker_kid_hair.vmdl", items.Child("2")!.String("model_player"));
        Assert.NotNull(items.Child("2")!.Child("visuals"));
        Assert.Equal("models/heroes/invoker/invoker.vmdl", items.Child("1")!.String("model_player"));
        Assert.Equal(
            "models/heroes/invoker_kid/invoker_kid_hair.vmdl",
            items.Child("3")!.String("model_player"));
    }
}
