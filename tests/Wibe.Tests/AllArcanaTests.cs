using System.Text.Json;
using System.Text.Json.Nodes;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;
using Xunit.Abstractions;

namespace Wibe.Tests;

/// <summary>
/// Полный прогон всех 25 аркан через настоящее Rust-ядро. Запускается только по явному запросу,
/// потому что каждая аркана собирается во всех своих стилях и это минуты:
///   WIBE_ARCANA_AUTOTEST=1 dotnet test tests/Wibe.Tests --filter All_arcanas
/// Ничего в игре не остаётся: паки пишутся во временную папку.
/// </summary>
public class AllArcanaTests
{
    private readonly ITestOutputHelper _output;

    public AllArcanaTests(ITestOutputHelper output) => _output = output;

    private static readonly (string Hero, int Item)[] Arcanas =
    {
        ("lina", 4794),
        ("legion_commander", 5810),
        ("terrorblade", 5957),
        ("techies", 6879),
        ("zuus", 6914),
        ("nevermore", 6996),
        ("phantom_assassin", 7247),
        ("crystal_maiden", 7385),
        ("pudge", 7756),
        ("monkey_king", 9050),
        ("juggernaut", 9059),
        ("wisp", 9235),
        ("spectre", 9662),
        ("rubick", 12451),
        ("earthshaker", 12692),
        ("queenofpain", 12930),
        ("skeleton_king", 13456),
        ("ogre_magi", 13670),
        ("windrunner", 13806),
        ("faceless_void", 18033),
        ("skywrath_mage", 18539),
        ("drow_ranger", 19090),
        ("vengefulspirit", 22718),
        ("razor", 23095),
        ("tidehunter", 37143),
    };

    [Fact]
    public void All_arcanas_build_in_all_styles_without_lost_slots_or_asset_errors()
    {
        if (Environment.GetEnvironmentVariable("WIBE_ARCANA_AUTOTEST") != "1")
        {
            return;
        }

        var install = DotaInstallation.Detect(out var error);
        Assert.True(install is not null && install.IsValid, "Dota 2 не найдена: " + error);
        var catalog = EconCatalog.Load(install!);
        var outDir = Path.Combine(Path.GetTempPath(), "wibe-arcana-autotest");
        Directory.CreateDirectory(outDir);

        var stock = PakBodyIntegrityTests.ReadSizes(install!.MainPakDir);
        var failures = new List<string>();
        var built = 0;
        foreach (var (hero, itemId) in Arcanas)
        {
            var item = catalog.ItemById(itemId);
            Assert.True(item is not null, $"нет предмета {itemId}");
            var styles = Math.Max(1, item!.Styles);
            for (var style = 0; style < styles; style++)
            {
                var outPath = Path.Combine(outDir, $"{hero}-{itemId}-s{style}.vpk");
                var picks = new List<HeroPicks>
                {
                    new(hero, new List<(string Slot, int ItemId)> { (item.Slot.Length == 0 ? "body" : item.Slot, itemId) },
                        style > 0 ? new Dictionary<string, int> { [item.Slot.Length == 0 ? "body" : item.Slot] = style } : null),
                };
                var log = new List<string>();
                var result = RustCoreModBuilder.Build(install, catalog, picks, l =>
                {
                    lock (log)
                    {
                        log.Add(l);
                    }
                }, outputOverride: outPath);
                built++;
                if (!result.Success)
                {
                    failures.Add($"{hero} {item.Name} style {style}: {result.Message}");
                    continue;
                }

                var manifestPath = Path.Combine(RustCoreModBuilder.InstallDirectory, "build_manifest.json");
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
                var count = (manifest?["picks"] as JsonArray)?.Count ?? 0;
                if (count == 0)
                {
                    failures.Add($"{hero} {item.Name} style {style}: пустой манифест");
                }

                // A file cannot carry arcana levels or a second merged model; that is a known
                // limit of the reference builder. A lost SLOT would mean a part of the set did
                // not reach the pack at all, and that is a failure.
                var lost = result.Left.SelectMany(l => l.Split(" | ")).Where(w =>
                    w.Contains("slot", StringComparison.OrdinalIgnoreCase)).ToList();
                if (lost.Count > 0)
                {
                    failures.Add($"{hero} {item.Name} style {style}: потеряно {string.Join(", ", lost)}");
                }

                var notes = result.Left.SelectMany(l => l.Split(" | ")).Where(w =>
                    w.Contains("model", StringComparison.OrdinalIgnoreCase)).ToList();
                if (notes.Count > 0)
                {
                    _output.WriteLine($"   без файла (эффект/второй меш): {string.Join(", ", notes)}");
                }

                // The body is the one file that must never be a part: a helm, legs or an empty
                // stand-in there means the whole hero disappears in the game. The hero's own
                // folder is not always <hero>: Drow is models/heroes/drow/drow_base.vmdl_c.
                var made = PakBodyIntegrityTests.ReadSizes(outPath);
                var bodies = made
                    .Where(e => e.Key.StartsWith("models/heroes/", StringComparison.OrdinalIgnoreCase)
                        && e.Key.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var biggest = bodies.OrderByDescending(e => e.Value).FirstOrDefault();
                if (biggest.Value < 64 * 1024)
                {
                    failures.Add($"{hero} {item.Name} style {style}: нет целого тела героя ({biggest.Key}={biggest.Value} b)");
                }

                foreach (var (path, size) in bodies)
                {
                    if (stock.TryGetValue(path, out var clean) && clean > 500 * 1024 && size < clean / 2)
                    {
                        failures.Add($"{hero} {item.Name} style {style}: {path} {size} против стока {clean}");
                    }
                }

                _output.WriteLine($"{hero,-20} {item.Name,-36} style {style}: {result.Files,3} файлов, тело {biggest.Value} b ({biggest.Key}), {count} предметов");
            }
        }

        _output.WriteLine($"собрано {built} вариантов");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
