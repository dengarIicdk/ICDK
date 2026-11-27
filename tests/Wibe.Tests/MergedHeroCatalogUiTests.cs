using System.Text.Json;
using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Вкладки героя теперь показывают не только кропы, но и все проверенные шмотки из реестра
/// пака. Тест обходит каждую папку и проверяет полный охват: сумма уникальных item_id,
/// рабочие ссылки на картинку и непустой слот для примерки.
/// </summary>
public sealed class MergedHeroCatalogUiTests
{
    private static IEnumerable<string> Folders()
    {
        var root = HeroFolderCatalog.Root;
        if (root is null || !Directory.Exists(root))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(name) && !name.Equals("Ability Effects", StringComparison.OrdinalIgnoreCase))
            {
                yield return name;
            }
        }
    }

    [Fact]
    public void Every_hero_page_merges_the_whole_verified_catalog()
    {
        var folders = Folders().ToList();
        Assert.True(folders.Count >= 130, $"папок героев всего {folders.Count}");

        var ids = new HashSet<int>();
        var withIcon = 0;
        var merged = 0;
        var seenPerSection = new List<string>();

        foreach (var folder in folders)
        {
            var view = HeroFolderCatalog.Read(folder, folder);
            Assert.NotNull(view);

            foreach (var section in view!.Sections)
            {
                var inner = new HashSet<int>();
                foreach (var card in section.Cards)
                {
                    if (!inner.Add(card.Id))
                    {
                        seenPerSection.Add($"{folder}/{section.Name}: дубль item_id {card.Id}");
                    }

                    ids.Add(card.Id);
                    if (card.ImgUrl is { Length: > 0 })
                    {
                        merged++;
                        Assert.StartsWith("/api/vpk-icon?inv=", card.ImgUrl);
                        Assert.False(string.IsNullOrWhiteSpace(card.Slot), $"{folder}: у карточки {card.Id} пустой слот");
                        if (card.Art is { Length: > 0 })
                        {
                            withIcon++;
                        }
                    }
                }
            }
        }

        Console.WriteLine($"карточек по всем героям: {ids.Count}, из них добранных из пака: {merged}");
        Assert.True(seenPerSection.Count == 0, string.Join("; ", seenPerSection.Take(5)));
        Assert.True(ids.Count >= 10_900, $"уникальных карточек по всем героям {ids.Count} — каталог не врезан");
        Assert.True(merged >= 1_400, $"добранных карточек всего {merged}");
        Assert.Equal(merged, withIcon);
    }

    [Fact]
    public void Merged_cards_reach_the_api_and_the_icon_endpoint()
    {
        var view = Folders()
            .Select(f => HeroFolderCatalog.Read(f, f))
            .Where(v => v is not null)
            .SelectMany(v => v!.Sections)
            .SelectMany(s => s.Cards)
            .Where(c => c.ImgUrl is { Length: > 0 })
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .Take(3)
            .ToList();
        Assert.True(view.Count >= 3, "в каталоге нет добранных карточек");

        foreach (var card in view)
        {
            var json = JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            var img = doc.RootElement.GetProperty("imgUrl").GetString();
            Assert.False(string.IsNullOrWhiteSpace(img));

            var inv = Uri.UnescapeDataString(img!.Split("inv=", 2)[1]);
            var png = VpkIconService.Get(inv);
            Assert.True(png is not null, $"{card.Name}: картинка {inv} не достаётся из пака");
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png!.Take(4).ToArray());
        }
    }
}
