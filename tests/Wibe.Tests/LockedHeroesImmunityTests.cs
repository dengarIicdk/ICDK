using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// ИММУНИТЕТ ЗАЛОЧЕННЫХ ГЕРОЕВ. Файл <c>locked_heroes_snapshot.json</c> — реестр героев, которые
/// прошли скан «пять из пяти»: точный список выходных моделей, их размеры и SHA256. Каждый такой
/// герой собирается заново и обязан совпасть байт в байт. Эталон НИКОГДА не перезаписывается
/// автоматически: герой, попавший в реестр, по определению не чинится.
/// </summary>
public sealed class LockedHeroesImmunityTests
{
    private const string RegistryName = "locked_heroes_snapshot.json";

    private static string RegistryPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 6 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir, "tests", "Wibe.Tests", RegistryName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = Path.GetDirectoryName(dir);
            }

            var root = Directory.GetCurrentDirectory();
            while (root is not null && !File.Exists(Path.Combine(root, "Directory.Build.props")))
            {
                root = Path.GetDirectoryName(root);
            }

            return Path.Combine(root ?? Directory.GetCurrentDirectory(), "tests", "Wibe.Tests", RegistryName);
        }
    }

    internal static JsonObject LoadRegistry()
    {
        Assert.True(File.Exists(RegistryPath), "нет реестра замков: " + RegistryPath);
        return JsonNode.Parse(File.ReadAllText(RegistryPath)) as JsonObject
               ?? throw new InvalidOperationException("реестр замков не читается");
    }

    /// <summary>Герои из реестра с их наборами: (герой, слоты, стили).</summary>
    internal static IEnumerable<(string Hero, HeroPicks Picks)> Locked()
    {
        var doc = LoadRegistry();
        foreach (var node in (doc["heroes"] as JsonArray) ?? new JsonArray())
        {
            if (node is not JsonObject row)
            {
                continue;
            }

            var hero = row["hero"]!.GetValue<string>();
            var picks = new List<(string, int)>();
            foreach (var p in (row["picks"] as JsonArray) ?? new JsonArray())
            {
                picks.Add((p!["slot"]!.GetValue<string>(), p["item_id"]!.GetValue<int>()));
            }

            var styles = new Dictionary<string, int>();
            foreach (var s in (row["styles"] as JsonObject) ?? new JsonObject())
            {
                styles[s.Key] = s.Value!.GetValue<int>();
            }

            yield return (hero, new HeroPicks(hero, picks, styles.Count == 0 ? null : styles));
        }
    }

    [Fact]
    public void Locked_heroes_never_change_by_a_single_byte()
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var catalog = EconCatalog.Load(install);
        var doc = LoadRegistry();
        // путь может принадлежать двум строкам реестра (базовый герой и его персона делят
        // models/heroes/<dir>/), поэтому каждый путь сверяется только там, где он единственный
        var claims = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hero in (doc["heroes"] as JsonArray) ?? new JsonArray())
        {
            foreach (var m in (hero!["models"] as JsonArray) ?? new JsonArray())
            {
                var path = m!["path"]!.GetValue<string>();
                claims[path] = claims.TryGetValue(path, out var n) ? n + 1 : 1;
            }
        }

        var expected = new Dictionary<string, (long Size, string Sha)>(StringComparer.OrdinalIgnoreCase);
        foreach (var hero in (doc["heroes"] as JsonArray) ?? new JsonArray())
        {
            foreach (var m in (hero!["models"] as JsonArray) ?? new JsonArray())
            {
                var path = m!["path"]!.GetValue<string>();
                if (claims[path] != 1 || expected.ContainsKey(path))
                {
                    continue;
                }

                expected[path] = (m["size"]!.GetValue<long>(), m["sha256"]!.GetValue<string>());
            }
        }

        if (expected.Count == 0)
        {
            return;
        }

        var locked = Locked().ToList();
        var outPath = Path.Combine(Path.GetTempPath(), "wibe-locked-heroes.vpk");
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in locked.Chunk(25))
        {
            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }

            var result = RustCoreModBuilder.Build(install, catalog, chunk.Select(c => c.Picks).ToList(),
                outputOverride: outPath);
            Assert.True(result.Success, "сборка залоченных героев не прошла: " + result.Message);
            foreach (var (path, size) in PakBodyIntegrityTests.ReadSizes(outPath))
            {
                sizes[path] = size;
                if (expected.ContainsKey(path))
                {
                    var data = PakBodyIntegrityTests.ReadEntry(outPath, path);
                    if (data is not null)
                    {
                        hashes[path] = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                    }
                }
            }
        }

        var broken = new List<string>();
        foreach (var (path, want) in expected)
        {
            if (!sizes.TryGetValue(path, out var have))
            {
                broken.Add($"{path}: пропал из пака");
                continue;
            }

            if (have != want.Size)
            {
                broken.Add($"{path}: {have} байт вместо {want.Size}");
                continue;
            }

            var sha = hashes.TryGetValue(path, out var cached) ? cached : "";
            if (!string.Equals(sha, want.Sha, StringComparison.OrdinalIgnoreCase))
            {
                broken.Add($"{path}: SHA256 {sha[..12]}… вместо {want.Sha[..12]}…");
            }
        }

        Assert.True(broken.Count == 0,
            "залоченные герои изменились — правка не изолирована:" + Environment.NewLine +
            string.Join(Environment.NewLine, broken.Take(25)));

        Assert.True(locked.Count >= 9, $"в реестре замков всего {locked.Count} героев");
    }
}
