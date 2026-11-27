using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Six targeted data fixes. These assert the cards the UI actually gets, not the parser's
/// intermediate output: no dead tabs, arcana cards present, Eidolons on their own shelf,
/// and every Dragon Knight equipment shelf populated from local item data.
/// </summary>
public sealed class SixTargetedFixesTests
{
    private static HeroFolderCatalog.View View(string folder)
    {
        var view = HeroFolderCatalog.Read(folder, folder);
        Assert.NotNull(view);
        return view!;
    }

    private static IReadOnlyList<HeroFolderCatalog.Card> Cards(string folder, string section) =>
        View(folder).Sections.First(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)).Cards;

    [Fact]
    public void Queen_of_pain_has_the_ristul_arcana_with_both_styles()
    {
        var card = Assert.Single(Cards("Queen Of Pain", "Back"), c => c.Id == 12930);
        Assert.Equal("Eminence of Ristul", card.Name);
        Assert.Equal("back", card.Slot);
        Assert.Equal(2, card.Styles);
        Assert.StartsWith("/api/crop?path=items/Queen%20Of%20Pain/Back/", card.Art);
        Assert.True(File.Exists(Path.Combine(HeroFolderCatalog.Root!, "..", "crops", "items", "Queen Of Pain", "Back", "12930.png")));
    }

    [Fact]
    public void Io_has_the_benevolent_companion_cube()
    {
        var card = Assert.Single(Cards("Io", "Head"), c => c.Id == 9235);
        Assert.Equal("Benevolent Companion", card.Name);
        Assert.Equal("head", card.Slot);
        Assert.StartsWith("/api/crop?path=items/Io/Head/", card.Art);
    }

    [Fact]
    public void Enigma_has_an_eidolon_shelf_with_real_ability2_items()
    {
        var cards = Cards("Enigma", "Eidolons");
        Assert.True(cards.Count >= 4, "Enigma/Eidolons: пустая или неполная вкладка");
        Assert.Contains(cards, c => c.Id == 6480);
        Assert.Contains(cards, c => c.Id == 9352);
        Assert.All(cards, c => Assert.Equal("ability2", c.Slot));
    }

    [Fact]
    public void Dragon_knight_has_every_equipment_shelf()
    {
        var view = View("Dragon Knight");
        var names = view.Sections.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "Fullsets", "Weapon", "Shield", "Head", "Shoulder", "Arms", "Back", "Dragon Form" })
        {
            Assert.Contains(expected, names);
        }

        var shield = Cards("Dragon Knight", "Shield");
        Assert.NotEmpty(shield);
        Assert.All(shield, c => Assert.Equal("offhand_weapon", c.Slot));
        var weapon = Cards("Dragon Knight", "Weapon");
        Assert.NotEmpty(weapon);
        Assert.All(weapon, c => Assert.Equal("weapon", c.Slot));
        var form = Cards("Dragon Knight", "Dragon Form");
        Assert.NotEmpty(form);
        Assert.All(form, c => Assert.Equal("shapeshift", c.Slot));
    }

    [Fact]
    public void Every_hero_page_has_zero_dead_tabs()
    {
        var root = HeroFolderCatalog.Root;
        Assert.False(string.IsNullOrWhiteSpace(root));
        var heroFolders = Directory.EnumerateDirectories(root!)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n) &&
                        !string.Equals(n, "Ability Effects", StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .ToList();
        Assert.Equal(139, heroFolders.Count);

        foreach (var folder in heroFolders)
        {
            var view = HeroFolderCatalog.Read(folder, folder);
            Assert.NotNull(view);
            Assert.NotEmpty(view!.Sections);
            foreach (var section in view.Sections)
            {
                Assert.True(section.Cards.Count > 0, $"{folder}/{section.Name}: вкладка с 0 карточек");
            }
        }
    }
}
