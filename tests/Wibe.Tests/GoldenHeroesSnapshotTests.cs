using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// ЗАМОК НА РАБОЧИЕ МОДЕЛИ. Девять героев, которые в игре уже выглядят правильно, собираются
/// одним паком, и каждый выпущенный .vmdl_c сверяется с эталоном по SHA256 и размеру.
/// Любая правка общего кода сборки, изменившая хотя бы один байт у золотого героя, роняет тест:
/// значит правка не изолирована и её надо переписать, а не подкрутить эталон.
/// Эталон обновляется только осознанно: WIBE_GOLDEN_UPDATE=1 (и тогда в git видно, что именно сдвинулось).
/// </summary>
public sealed class GoldenHeroesSnapshotTests
{
    /// <summary>Кто в золотом списке и что у него надето. Порядок и содержимое — часть эталона.</summary>
    private static readonly (string Hero, (string Slot, int ItemId)[] Picks, (string Slot, int Style)[] Styles)[] Golden =
    {
        // Axe: микс со скрина — голова, броня, пояс, оружие и тело.
        ("axe", new[]
        {
            ("head", 13543), ("armor", 25824), ("belt", 25825), ("weapon", 25828), ("body", 12964),
        }, Array.Empty<(string, int)>()),

        // Pudge: аркана Feast of Abscession плюс сет вокруг неё.
        ("pudge", new[]
        {
            ("body", 7756), ("weapon", 8010), ("back", 26782), ("head", 9252), ("shoulder", 9231), ("belt", 12806),
        }, Array.Empty<(string, int)>()),

        // Queen of Pain: Eminence of Ristul, второй стиль.
        ("queenofpain", new[] { ("body", 12930), ("head", 29615), ("weapon", 12337) },
            new[] { ("body", 1) }),

        // Drow Ranger: Dread Retribution, второй стиль.
        ("drow_ranger", new[] { ("body", 19090), ("weapon", 37144), ("head", 19094), ("legs", 19091) },
            new[] { ("body", 1) }),

        // Phantom Assassin: Manifold Paradox.
        ("phantom_assassin", new[] { ("body", 7247), ("head", 36070), ("weapon", 19180) },
            Array.Empty<(string, int)>()),

        // Terrorblade: Fractal Horns of Inner Abysm.
        ("terrorblade", new[] { ("body", 5957) }, Array.Empty<(string, int)>()),

        // Juggernaut: Bladeform Legacy.
        ("juggernaut", new[] { ("body", 9059), ("weapon", 12296), ("head", 30261) },
            Array.Empty<(string, int)>()),

        // Invoker: персона Acolyte of the Lost Arts (Kid) и голова под неё.
        ("invoker", new[] { ("body", 13042), ("head", 7986) }, Array.Empty<(string, int)>()),

        // Anti-Mage: персона The Disciple's Path (Wei) плюс оружие.
        ("antimage", new[] { ("body", 13783), ("weapon", 7277) }, Array.Empty<(string, int)>()),
    };

    private const string BaselineName = "golden_heroes.json";

    private static bool Update => Environment.GetEnvironmentVariable("WIBE_GOLDEN_UPDATE") == "1";

    private static string BaselinePath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 6 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir, "tests", "Wibe.Tests", BaselineName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                var here = Path.Combine(dir, BaselineName);
                if (File.Exists(here) && !dir.EndsWith("bin", StringComparison.OrdinalIgnoreCase) &&
                    !dir.Contains(Path.Combine("bin", "Debug"), StringComparison.OrdinalIgnoreCase))
                {
                    return here;
                }

                dir = Path.GetDirectoryName(dir);
            }

            // нет файла — кладём рядом с исходником теста, а не в bin
            var root = Directory.GetCurrentDirectory();
            while (root is not null && !File.Exists(Path.Combine(root, "Directory.Build.props")))
            {
                root = Path.GetDirectoryName(root);
            }

            return Path.Combine(root ?? Directory.GetCurrentDirectory(), "tests", "Wibe.Tests", BaselineName);
        }
    }

    [Fact]
    public void Golden_heroes_are_byte_identical_to_the_snapshot()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return; // без игры нечего собирать
        }

        var catalog = EconCatalog.Load(install);
        var loadout = new List<HeroPicks>();
        foreach (var (hero, picks, styles) in Golden)
        {
            foreach (var (_, id) in picks)
            {
                Assert.True(catalog.ItemById(id) is not null, $"{hero}: в каталоге нет предмета {id}");
            }

            loadout.Add(new HeroPicks(
                hero,
                picks.ToList(),
                styles.Length == 0 ? null : styles.ToDictionary(s => s.Slot, s => s.Style)));
        }

        var outPath = Path.Combine(Path.GetTempPath(), "wibe-golden-heroes.vpk");
        var log = new List<string>();
        var result = RustCoreModBuilder.Build(install, catalog, loadout, line =>
        {
            lock (log)
            {
                log.Add(line);
            }
        }, outputOverride: outPath);

        Assert.True(result.Success, "золотая сборка не прошла: " + result.Message + " | " + string.Join(" / ", log.TakeLast(6)));

        var made = Snapshot(outPath);
        Assert.True(made.Count >= 40, $"в золотой сборке всего {made.Count} моделей — это не девять героев");

        if (Update || !File.Exists(BaselinePath))
        {
            WriteBaseline(made);
            return;
        }

        var expected = ReadBaseline();
        var regressions = new List<string>();
        foreach (var (path, want) in expected)
        {
            if (!made.TryGetValue(path, out var have))
            {
                regressions.Add($"{path}: пропал (эталон {want.Size} байт)");
            }
            else if (have.Size != want.Size)
            {
                regressions.Add($"{path}: размер {have.Size} вместо {want.Size}");
            }
            else if (!string.Equals(have.Sha256, want.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                regressions.Add($"{path}: SHA256 {have.Sha256[..12]}… вместо {want.Sha256[..12]}…");
            }
        }

        foreach (var path in made.Keys.Where(p => !expected.ContainsKey(p)))
        {
            regressions.Add($"{path}: появился, в эталоне его нет");
        }

        Assert.True(
            regressions.Count == 0,
            "золотые герои изменились — правка не изолирована:" + Environment.NewLine +
            string.Join(Environment.NewLine, regressions.Take(25)));
    }

    /// <summary>Путь модели → (размер, SHA256). Группы моделей/героев сохраняются как есть.</summary>
    private static SortedDictionary<string, ModelHash> Snapshot(string pak)
    {
        var sizes = PakBodyIntegrityTests.ReadSizes(pak);
        var result = new SortedDictionary<string, ModelHash>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, size) in sizes)
        {
            if (!path.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = PakBodyIntegrityTests.ReadEntry(pak, path);
            if (bytes is null)
            {
                continue;
            }

            result[path] = new ModelHash(size, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        return result;
    }

    private static void WriteBaseline(SortedDictionary<string, ModelHash> made)
    {
        var models = new JsonArray();
        foreach (var (path, hash) in made)
        {
            models.Add(new JsonObject
            {
                ["path"] = path,
                ["size"] = hash.Size,
                ["sha256"] = hash.Sha256,
            });
        }

        var heroes = new JsonArray();
        foreach (var (hero, picks, styles) in Golden)
        {
            var pickRows = new JsonArray();
            foreach (var (slot, id) in picks)
            {
                pickRows.Add(new JsonObject { ["slot"] = slot, ["item_id"] = id });
            }

            heroes.Add(new JsonObject
            {
                ["hero"] = hero,
                ["picks"] = pickRows,
                ["styles"] = new JsonObject(styles.Select(s => KeyValuePair.Create(s.Slot, JsonNode.Parse(s.Style.ToString())))),
            });
        }

        var doc = new JsonObject
        {
            ["note"] = "Эталон рабочих героев. Менять только вместе с осознанным изменением сборки: WIBE_GOLDEN_UPDATE=1.",
            ["heroes"] = heroes,
            ["models"] = models,
        };

        var file = BaselinePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, ModelHash> ReadBaseline()
    {
        var text = File.ReadAllText(BaselinePath);
        var doc = JsonNode.Parse(text) as JsonObject;
        var result = new Dictionary<string, ModelHash>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in (doc?["models"] as JsonArray) ?? new JsonArray())
        {
            if (node is not JsonObject row)
            {
                continue;
            }

            result[row["path"]!.GetValue<string>()] = new ModelHash(
                row["size"]!.GetValue<long>(),
                row["sha256"]!.GetValue<string>());
        }

        return result;
    }

    private readonly record struct ModelHash(long Size, string Sha256);
}
