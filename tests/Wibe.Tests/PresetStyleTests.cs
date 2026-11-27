using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Пресет — это снимок всей примерки, а не только предметов: иначе загруженная сборка
/// теряет выбранный облик (второй стиль арканы) и выглядит не так, как её сохраняли.
/// </summary>
public class PresetStyleTests
{
    private static PresetStore Store(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "wibe-preset-" + Guid.NewGuid().ToString("n")[..8], "presets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new PresetStore(path);
    }

    [Fact]
    public void A_preset_remembers_the_style_of_every_piece()
    {
        var store = Store(out var path);
        var heroes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["skeleton_king"] = new(StringComparer.OrdinalIgnoreCase) { ["body"] = 13456 },
            ["axe"] = new(StringComparer.OrdinalIgnoreCase) { ["persona_selector"] = 31367 },
        };
        var styles = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["skeleton_king"] = new(StringComparer.OrdinalIgnoreCase) { ["body"] = 1, ["shoulder"] = 1 },
        };

        var saved = store.Save("второй стиль", heroes, null, styles);
        Assert.Equal(2, saved.Styles!["skeleton_king"].Count);

        // и после перезапуска файла на диске стиль на месте
        var again = new PresetStore(path).All.Single(p => p.Name == "второй стиль");
        Assert.NotNull(again.Styles);
        Assert.Equal(1, again.Styles!["skeleton_king"]["body"]);
        Assert.Equal(1, again.Styles["skeleton_king"]["shoulder"]);
        Assert.Equal(13456, again.Heroes["skeleton_king"]["body"]);
    }

    [Fact]
    public void Loading_a_preset_takes_its_styles_and_drops_the_old_ones()
    {
        var store = Store(out var path);
        var loadoutPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "selected_loadout.json");
        var loadout = new LoadoutStore(loadoutPath);
        loadout.SetHero("skeleton_king", new[] { new KeyValuePair<string, int>("body", 13456) },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["body"] = 1 });
        loadout.SetHero("axe", new[] { new KeyValuePair<string, int>("persona_selector", 31367) });

        var preset = store.Save("кровавый", loadout.Heroes, loadout.WorldFor(), loadout.Styles());
        Assert.Empty(loadout.Styles().Where(p => p.Key == "axe"));

        loadout.Replace(preset.Heroes, preset.World, preset.Styles);
        Assert.Equal(13456, loadout.Heroes["skeleton_king"]["body"]);
        Assert.Equal(1, loadout.StylesForHero("skeleton_king")["body"]);
        Assert.True(loadout.HasHero("axe"));
    }

    [Fact]
    public void A_hero_page_reads_the_picks_saved_under_his_real_name()
    {
        // страница wraith_king, а в таблице герой — skeleton_king: примерка обязана быть видна
        var path = Path.Combine(Path.GetTempPath(), "wibe-alias-" + Guid.NewGuid().ToString("n")[..8], "selected_loadout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var loadout = new LoadoutStore(path);
        loadout.FolderAlias = f => f.Equals("wraith_king", StringComparison.OrdinalIgnoreCase) ? "skeleton_king" : f;

        loadout.SetHero("skeleton_king", new[] { new KeyValuePair<string, int>("body", 13456) },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["body"] = 1 });

        Assert.True(loadout.HasHero("wraith_king"));
        Assert.Equal(13456, loadout.ForHero("wraith_king")["body"]);
        Assert.Equal(1, loadout.StylesForHero("wraith_king")["body"]);

        // и запись с чужого имени ложится на то же место, а не рядом с ним
        loadout.SetHero("wraith_king", new[] { new KeyValuePair<string, int>("body", 13456), new KeyValuePair<string, int>("head", 24231) });
        Assert.Equal(1, loadout.Heroes.Count);
        Assert.Equal(2, loadout.Heroes["skeleton_king"].Count);
    }
}
