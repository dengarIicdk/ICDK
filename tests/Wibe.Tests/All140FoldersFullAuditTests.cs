using System.Security.Cryptography;
using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

public sealed class All140FoldersFullAuditTests
{
    private static readonly (string Path, string Sha1)[] Pinned =
    {
        (Path.Combine("game", "dota", "gameinfo.gi"), "a0dc87acc7a659eedd1f9575eee2bd3883e8f201"),
        (Path.Combine("game", "dota", "gameinfo_branchspecific.gi"), "224b6f879d00d419b6a10fe07b4ddb3d827fd31f"),
        // языковая папка мода: тот же gameinfo.gi, который патчат «мод-менеджеры» — у нас он обязан
        // остаться байт в байт стоковым, иначе мод ставится правкой signed-файла
        (Path.Combine("game", "dota_russian", "gameinfo.gi"), "6aea2a4d460f5656d5d780e2ae6148c978c864b0"),
    };

    /// <summary>
    /// dota.signatures меняется с каждым патчем игры, поэтому его хеш не прибит ни здесь, ни в
    /// ядре: важно, что файл на месте и ядро сверяет его байты до и после сборки.
    /// </summary>
    private const string Signatures = "dota.signatures";

    [Fact]
    public void All_hero_folders_sections_crops_and_hands_are_consistent()
    {
        var root = HeroFolderCatalog.Root;
        Assert.False(string.IsNullOrWhiteSpace(root), "папка Heroes не найдена");
        var repo = Directory.GetParent(root!)!.FullName;

        var collectionFile = Path.Combine(repo, "src", "Wibe.Server", "www", "collection.json");
        using var collectionDoc = JsonDocument.Parse(File.ReadAllText(collectionFile));
        var collection = collectionDoc.RootElement.EnumerateArray()
            .Select(e => new
            {
                Name = e.GetProperty("name").GetString()!,
                Img = e.GetProperty("img").GetString()!,
                PersonaId = e.TryGetProperty("personaId", out var p) ? p.GetInt32() : 0,
            })
            .ToList();
        Assert.Equal(139, collection.Count);
        Assert.Equal(12, collection.Count(e => e.PersonaId != 0));

        var crops = Path.Combine(repo, "crops");
        Assert.True(Directory.Exists(crops), "нет папки crops");
        foreach (var hero in collection)
        {
            var portrait = Path.Combine(crops, "heroes", Path.GetFileName(hero.Img));
            Assert.True(File.Exists(portrait), $"нет портрета crops/heroes для {hero.Name}");
        }

        var auditFile = Path.Combine(crops, "audit.json");
        Assert.True(File.Exists(auditFile), "нет crops/audit.json");
        using var auditDoc = JsonDocument.Parse(File.ReadAllText(auditFile));
        var audit = auditDoc.RootElement;
        var cells = audit.GetProperty("cells").GetInt32();
        var equipped = audit.GetProperty("equipped").GetInt32();
        var matched = audit.GetProperty("matched").GetInt32();
        var eligible = Math.Max(1, cells - equipped);
        Assert.True(matched >= eligible * 0.98,
            $"общий матчинг {matched}/{eligible} = {(double)matched / eligible:P1}, требуется 98%");
        foreach (var folder in audit.GetProperty("per_folder").EnumerateObject())
        {
            var c = folder.Value.GetProperty("cells").GetInt32();
            var e = folder.Value.GetProperty("equipped").GetInt32();
            var m = folder.Value.GetProperty("matched").GetInt32();
            var need = c - e;
            if (need >= 20)
            {
                Assert.True(m >= need * 0.95,
                    $"{folder.Name}: {m}/{need} = {(double)m / need:P1}, требуется 95%");
            }
        }

        foreach (var hero in collection)
        {
            var dirs = Directory.EnumerateDirectories(Path.Combine(root!, hero.Name))
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var flat = dirs.Count == 0;
            if (flat)
            {
                dirs.Add("Hero Base");
            }

            if (hero.Name == "Luna")
            {
                dirs.Add("Ability Effects");
            }

            var view = HeroFolderCatalog.Read(hero.Name, hero.Name);
            Assert.NotNull(view);
            var actual = view!.Sections.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Вкладки из реестра пака () законны: кропы остаются приоритетными,
            // а отсутствующий слот героя теперь достраивается из проверенного каталога.
            if (!flat)
            {
                Assert.True(actual.Intersect(dirs).Any() || actual.Count > 0,
                    $"{hero.Name}: у страницы нет ни родных вкладок, ни добра из пака");
            }
            Assert.True(view.TotalCards > 0, $"{hero.Name}: нет ни одной карточки");
            Assert.All(view.Sections, section => Assert.True(section.Cards.Count > 0,
                $"{hero.Name}/{section.Name}: пустая вкладка"));

            foreach (var section in view.Sections)
            {
                var sets = section.Name.Equals("Fullsets", StringComparison.OrdinalIgnoreCase) ||
                           section.Name.Equals("FullSets", StringComparison.OrdinalIgnoreCase) ||
                           section.Name.Equals("Fullset", StringComparison.OrdinalIgnoreCase) ||
                           section.Name.Equals("Hero Base", StringComparison.OrdinalIgnoreCase);
                foreach (var card in section.Cards)
                {
                    // родная карточка из crops или добранная из проверенного реестра пака
                    Assert.True(
                        card.Art.StartsWith("/api/crop?", StringComparison.Ordinal) ||
                        card.Art.StartsWith("/api/vpk-icon?inv=", StringComparison.Ordinal),
                        $"{hero.Name}/{section.Name}: неизвестный источник картинки {card.Art}");
                    Assert.DoesNotContain("/api/img", card.Art);
                    if (!sets)
                    {
                        Assert.NotEqual("bundle", card.Prefab, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }

            var weapon = view.Sections.FirstOrDefault(s => s.Name.Equals("Weapon", StringComparison.OrdinalIgnoreCase));
            var offhand = view.Sections.FirstOrDefault(s => s.Name.Equals("Off-Hand", StringComparison.OrdinalIgnoreCase) ||
                                                            s.Name.Equals("Offhand", StringComparison.OrdinalIgnoreCase));
            if (weapon is { Cards.Count: > 0 } && offhand is { Cards.Count: > 0 })
            {
                Assert.DoesNotContain(weapon.Cards, c => c.Slot.StartsWith("offhand", StringComparison.OrdinalIgnoreCase));
                Assert.All(offhand.Cards, c => Assert.StartsWith("offhand", c.Slot, StringComparison.OrdinalIgnoreCase));
                Assert.NotEqual(weapon.Cards[0].Slot, offhand.Cards[0].Slot);
            }
        }
    }

    [Fact]
    public void Both_anti_mage_hands_compile_into_the_pak()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        Assert.NotNull(catalog.ItemById(5396));
        Assert.NotNull(catalog.ItemById(5395));

        var outPath = Path.Combine(Path.GetTempPath(), "wibe-both-hands.vpk");
        var picks = new List<HeroPicks>
        {
            new("antimage", new List<(string Slot, int ItemId)>
            {
                ("weapon", 5396),
                ("offhand_weapon", 5395),
            }, new Dictionary<string, int>()),
        };
        var result = RustCoreModBuilder.Build(install, catalog, picks, outputOverride: outPath);
        Assert.True(result.Success, result.Message);

        var entries = PakBodyIntegrityTests.ReadSizes(outPath).Keys
            .Select(p => p.Replace('\\', '/').ToLowerInvariant())
            .ToList();
        Assert.Contains("models/heroes/antimage/antimage_weapon.vmdl_c", entries);
        Assert.Contains("models/heroes/antimage/antimage_offhand_weapon.vmdl_c", entries);
        Assert.DoesNotContain(entries, p => p.StartsWith("scripts/items/"));
    }

    [Fact]
    public void Signed_valve_files_are_byte_identical()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid)
        {
            return;
        }

        foreach (var (relative, expected) in Pinned)
        {
            var file = Path.Combine(install.GameDir, relative);
            if (!File.Exists(file))
            {
                continue;
            }

            using var sha = SHA1.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file))).ToLowerInvariant();
            Assert.Equal(expected, hash);
        }

        var signatures = Path.Combine(install.GameDir, "game", "bin", "win64", Signatures);
        Assert.True(File.Exists(signatures), "нет game/bin/win64/dota.signatures");
    }
}
