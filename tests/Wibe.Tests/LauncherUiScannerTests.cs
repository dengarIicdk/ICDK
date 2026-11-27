using System.Drawing;
using Wibe.Server;
using Wibe.Server.Launcher;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Сканер оболочки лаунчера: перетаскивание безрамочного окна, отсутствие дублей
/// Личностей в главной сетке и живость переключателей Личностей внутри базовых героев.
/// </summary>
public sealed class LauncherUiScannerTests
{
    [Fact]
    public void Headless_drag_moves_the_window_by_the_cursor_delta()
    {
        var start = new Point(400, 300);
        var cursor = new Point(1000, 500);

        Assert.Equal(new Point(400, 300), WindowDragMath.Next(start, cursor, cursor));
        Assert.Equal(new Point(460, 340), WindowDragMath.Next(start, cursor, new Point(1060, 540)));
        Assert.Equal(new Point(370, 290), WindowDragMath.Next(start, cursor, new Point(970, 490)));

        // из развёрнутого окна хватаем за ту же долю ширины, чтобы не прыгало
        var form = new Rectangle(0, 0, 1600, 900);
        var ratio = WindowDragMath.GripRatio(form, new Point(800, 10));
        Assert.InRange(ratio, 0.45, 0.55);
        Assert.InRange(WindowDragMath.GripRatio(form, new Point(-50, 10)), 0.05, 0.06);
    }

    [Fact]
    public void Main_grid_has_no_persona_duplicates_and_every_persona_switch_still_works()
    {
        var personas = HeroFolderCatalog.PersonaFolders();
        Assert.True(personas.Count >= 10, $"личностей в каталоге всего {personas.Count}");

        var baseHeroes = HeroFolderCatalog.BaseHeroFolders();
        Assert.True(baseHeroes.Count >= 120, $"базовых героев всего {baseHeroes.Count}");
        Assert.Equal(baseHeroes.Count, baseHeroes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(baseHeroes, name => personas.Contains(name));

        // каждая Личность открывается со своей страницы и не теряет разделы
        var checkedPersonas = 0;
        foreach (var persona in personas.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var view = HeroFolderCatalog.Read(persona, persona);
            if (view is null)
            {
                continue;
            }

            checkedPersonas++;
            Assert.True(view.IsPersona, $"{persona}: страница не считает себя Личностью");
            Assert.True(view.PersonaId != 0, $"{persona}: нет persona_id");
            Assert.True(view.Sections.Count > 0, $"{persona}: нет ни одной вкладки");
            Assert.True(view.TotalCards > 0, $"{persona}: нет ни одной карточки");
            Assert.All(view.Sections, s => Assert.True(s.Cards.Count > 0, $"{persona}/{s.Name}: пустая вкладка"));
        }

        Assert.True(checkedPersonas >= 10, $"проверено личностей {checkedPersonas}");
    }

    [Fact]
    public void Back_and_compile_do_not_fight_over_the_screen()
    {
        var root = AppContext.BaseDirectory;
        for (var at = new DirectoryInfo(root); at is not null; at = at.Parent)
        {
            var candidate = Path.Combine(at.FullName, "src", "Wibe.Server", "www", "app.js");
            if (!File.Exists(candidate))
            {
                continue;
            }

            var js = File.ReadAllText(candidate);

            // сборка не имеет права открывать экран героя
            var compile = js.IndexOf("async function compileAll()", StringComparison.Ordinal);
            Assert.True(compile > 0, "в app.js нет compileAll");
            var tail = js[compile..];
            var end = tail.IndexOf("async function decompileAll", StringComparison.Ordinal);
            var body = end > 0 ? tail[..end] : tail;
            Assert.DoesNotContain("openHero(", body);

            // «Назад» работает и когда адрес уже корневой
            Assert.Contains("renderHeroes();", js);
            Assert.Contains("Escape", js);
            return;
        }

        Assert.Fail("не найден www/app.js");
    }
}
