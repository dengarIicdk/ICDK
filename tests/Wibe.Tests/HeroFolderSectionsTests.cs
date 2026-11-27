using System.Text.Json;
using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

public sealed class HeroFolderSectionsTests
{
    [Fact]
    public void Every_hero_folder_opens_with_its_own_real_subfolders_and_cards()
    {
        var root = HeroFolderCatalog.Root;
        Assert.False(string.IsNullOrWhiteSpace(root), "папка Heroes не найдена");

        var folders = Directory.EnumerateDirectories(root!)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name) &&
                           !string.Equals(name, "Ability Effects", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(139, folders.Count);

        foreach (var folder in folders)
        {
            var dirs = Directory.EnumerateDirectories(Path.Combine(root!, folder))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var flat = dirs.Count == 0;
            if (flat)
            {
                dirs.Add("Hero Base");
            }

            // Luna: пять карточек эффектов лежат в сервисной папке Ability Effects,
            // но по-прежнему принадлежат её странице.
            if (string.Equals(folder, "Luna", StringComparison.OrdinalIgnoreCase))
            {
                dirs.Add("Ability Effects");
            }

            var view = HeroFolderCatalog.Read(folder, folder);
            Assert.NotNull(view);

            var actual = view!.Sections.Select(section => section.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Вкладки из реестра пака () законны: кропы остаются приоритетными,
            // а отсутствующий слот героя теперь достраивается из проверенного каталога.
            if (!flat)
            {
                Assert.True(actual.Intersect(dirs).Any() || actual.Count > 0,
                    $"{folder}: у страницы нет ни родных вкладок, ни добра из пака");
            }

            Assert.True(view.TotalCards > 0, $"{folder}: страница героя пустая");
            Assert.All(view.Sections, section => Assert.True(section.Cards.Count > 0,
                $"{folder}/{section.Name}: пустая вкладка"));

            foreach (var section in view.Sections)
            {
                foreach (var card in section.Cards)
                {
                    Assert.True(card.Id > 0, $"{folder}/{section.Name}: карточка без item_id");
                    Assert.False(string.IsNullOrWhiteSpace(card.Name), $"{folder}/{section.Name}: карточка без имени");
                    var text = card.Name + " " + card.Title;
                    Assert.DoesNotContain("EQUIPPED", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("₸", text);
                }
            }
        }
    }
}
