using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// «СКАН И СОХРАН» по всем героям каталога. Герои собираются батчами (25 ключей за один вызов
/// ядра: чтение pak01 — самая дорогая часть сборки), затем каждый проверяется по своим путям.
/// Пять критериев: инспектор открывает пак; заменённое тело не пустышка; кости модели слота живут
/// в скелете тела; у тела есть блок анимаций; нет живых loadout-партиклов и есть иконки слотов.
///
/// Обычный <c>dotnet test</c> скан не запускает. Включение:
///   WIBE_SCAN_ALL=1                    — проход по каталогу
///   WIBE_SCAN_HEROES="axe,pudge,..."   — только эти папки (калибровка)
///   WIBE_SCAN_LIMIT=20                 — первые N папок
///   WIBE_SCAN_WRITE_LOCKS=1            — дописать прошедших в locked_heroes_snapshot.json
/// Отчёт: tests/Wibe.Tests/scan_report.json
/// </summary>
public sealed class ScanAndLockTests
{
    private static readonly string Inspector = Path.Combine(
        @"Z:\Free-Pi-Projects\Dota_VPK\Source Code\Source2viewerCLI", "Source2Viewer-CLI.exe");

    private static bool Enabled => Environment.GetEnvironmentVariable("WIBE_SCAN_ALL") == "1";
    private static bool WriteLocks => Environment.GetEnvironmentVariable("WIBE_SCAN_WRITE_LOCKS") == "1";
    private static int Limit => int.TryParse(Environment.GetEnvironmentVariable("WIBE_SCAN_LIMIT"), out var n) ? n : int.MaxValue;

    private static string[] Only => (Environment.GetEnvironmentVariable("WIBE_SCAN_HEROES") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private const int BatchSize = 25;

    [Fact]
    public void Scan_every_hero_and_keep_the_healthy_ones_locked()
    {
        if (!Enabled)
        {
            return;
        }

        var install = DotaInstallation.Detect(out var why);
        Assert.True(install is not null && install.IsValid, "Dota 2 не найдена: " + why);
        Assert.True(File.Exists(Inspector), "нет инспектора: " + Inspector);

        var catalog = EconCatalog.Load(install!);
        var root = HeroFolderCatalog.Root;
        Assert.False(string.IsNullOrWhiteSpace(root), "нет папки Heroes");

        var all = Directory.EnumerateDirectories(root!)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Where(n => !n.Equals("Ability Effects", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var folders = (Only.Length > 0 ? Only.ToList() : all).Take(Limit).ToList();
        // папка каталога -> точные папки Valve из стоковых default_item этого HeroKey
        var map = new Dictionary<string, (string HeroKey, string[] Dirs)>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in all)
        {
            var view = HeroFolderCatalog.Read(folder, folder);
            if (view is null)
            {
                continue;
            }

            var dirs = catalog.Items
                .Where(i => i.IsDefault && i.Model is { Length: > 0 } &&
                            i.Hero.Equals(view.HeroKey, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Model!)
                .Where(m => m.StartsWith("models/heroes/", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Split('/')[2])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            map[folder] = (view.HeroKey, dirs);
        }


        var report = new JsonArray();
        var healthy = 0;
        var outPath = Path.Combine(Path.GetTempPath(), "wibe-scan.vpk");

        for (var at = 0; at < folders.Count; at += BatchSize)
        {
            var batch = folders.Skip(at).Take(BatchSize).ToList();
            var picked = new Dictionary<string, List<(string Slot, int Id, int Style)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in batch)
            {
                picked[folder] = Picks(folder, catalog);
            }

            var loadout = picked
                .Where(e => e.Value.Count > 0)
                .Select(e => new HeroPicks(
                    e.Key,
                    e.Value.Select(p => (p.Slot, p.Id)).ToList(),
                    e.Value.Any(p => p.Style > 0)
                        ? e.Value.Where(p => p.Style > 0).ToDictionary(p => p.Slot, p => p.Style)
                        : null))
                .ToList();

            if (loadout.Count == 0)
            {
                foreach (var folder in batch)
                {
                    report.Add(new JsonObject { ["hero"] = folder, ["status"] = "нет карточек" });
                }

                continue;
            }

            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }

            var built = RustCoreModBuilder.Build(install!, catalog, loadout, outputOverride: outPath);
            if (!built.Success)
            {
                foreach (var folder in batch)
                {
                    report.Add(new JsonObject
                    {
                        ["hero"] = folder,
                        ["status"] = "батч не собрался",
                        ["defects"] = new JsonArray(built.Message),
                    });
                }

                continue;
            }

            var sizes = PakBodyIntegrityTests.ReadSizes(outPath);
            var inspectorOk = InspectorLists(outPath);

            foreach (var folder in batch)
            {
                var verdict = new JsonObject { ["hero"] = folder };
                var defects = new JsonArray();
                var mine = picked[folder];
                if (mine.Count == 0)
                {
                    verdict["status"] = "нет карточек";
                    report.Add(verdict);
                    continue;
                }

                if (!inspectorOk)
                {
                    defects.Add("инспектор не открыл пак (1)");
                }

                var dirs = map.TryGetValue(folder, out var info) ? info.Dirs : Array.Empty<string>();
                string[] Own(string prefix) => dirs.Select(d => prefix + d + "/").ToArray();
                var heroPrefixes = Own("models/heroes/");
                var particlePrefixes = Own("particles/units/heroes/hero_");
                var econPrefixes = Own("particles/econ/items/");

                var bodies = Bodies(dirs, sizes);
                foreach (var (path, size) in bodies)
                {
                    if (size < 15 * 1024)
                    {
                        defects.Add($"{path}: {size} байт — пустышка вместо тела (2)");
                        continue;
                    }

                    if (!HasAnimations(outPath, path))
                    {
                        defects.Add($"{path}: нет блока анимаций (4)");
                    }
                }

                // критерий 3: кости модели слота живут в скелете тела (ловит посох Monkey King)
                var bodyPaths = bodies.Select(b => b.Path).ToList();
                var fat = sizes
                    .Where(e => e.Value > 600 * 1024 && e.Key.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Key)
                    .Where(p => heroPrefixes.Any(x => p.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                    .Where(p => !bodyPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    .Take(4)
                    .ToList();
                if (fat.Count > 0 && bodyPaths.Count > 0)
                {
                    var bodyBones = Bones(outPath, bodyPaths[0]);
                    if (bodyBones is { Count: > 0 })
                    {
                        foreach (var path in fat)
                        {
                            if (Bones(outPath, path) is { Count: > 0 } slotBones &&
                                !slotBones.Any(b => bodyBones.Contains(b)))
                            {
                                defects.Add($"{path}: ни одна кость привязки не живёт в скелете тела (3)");
                            }
                        }
                    }
                }

                var liveLoadout = sizes
                    .Where(e => e.Key.EndsWith(".vpcf_c", StringComparison.OrdinalIgnoreCase) &&
                                e.Key.Contains("loadout", StringComparison.OrdinalIgnoreCase) &&
                                e.Value > 1024)
                    .Where(e => particlePrefixes.Any(x => e.Key.StartsWith(x, StringComparison.OrdinalIgnoreCase)) ||
                                econPrefixes.Any(x => e.Key.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                    .Select(e => e.Key)
                    .ToList();
                if (liveLoadout.Count > 0)
                {
                    defects.Add("живые *_loadout*.vpcf_c: " + string.Join(", ", liveLoadout.Take(2)) + " (5)");
                }

                var wantsIcons = mine.Any(p => !p.Slot.StartsWith("ability", StringComparison.OrdinalIgnoreCase) &&
                                               !p.Slot.Equals("taunt", StringComparison.OrdinalIgnoreCase));
                if (wantsIcons && !sizes.Keys.Any(p => p.StartsWith("panorama/images/econ/", StringComparison.OrdinalIgnoreCase)))
                {
                    defects.Add("нет подменённых иконок слотов (5)");
                }

                if (defects.Count > 0)
                {
                    verdict["status"] = "отбракован";
                    verdict["defects"] = defects;
                    report.Add(verdict);
                    continue;
                }

                var snapshot = new SortedDictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (path, size) in sizes.Where(e =>
                             e.Key.StartsWith("models/heroes/", StringComparison.OrdinalIgnoreCase) ||
                             e.Key.StartsWith("models/items/", StringComparison.OrdinalIgnoreCase) ||
                             e.Key.StartsWith("panorama/images/econ/", StringComparison.OrdinalIgnoreCase)))
                {
                    var bytes = PakBodyIntegrityTests.ReadEntry(outPath, path);
                    if (bytes is not null)
                    {
                        snapshot[path] = (size, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                    }
                }

                verdict["status"] = "ок";
                report.Add(verdict);
                healthy++;
                if (WriteLocks)
                {
                    AppendToRegistry(new List<(string Hero, List<(string Slot, int Id)> Picks,
                        Dictionary<string, int> Styles, SortedDictionary<string, (long Size, string Sha)> Models)>
                    {
                        (folder, mine.Select(p => (p.Slot, p.Id)).ToList(),
                            mine.Where(p => p.Style > 0).ToDictionary(p => p.Slot, p => p.Style), snapshot),
                    });
                }
            }
        }

        File.WriteAllText(Path.Combine(RepoRoot(), "tests", "Wibe.Tests", "scan_report.json"), new JsonObject
        {
            ["heroes"] = report,
            ["healthy"] = healthy,
            ["failed"] = report.Count(r => (string?)r!["status"] == "отбракован"),
        }.ToJsonString());

        Assert.True(healthy > 0, "ни один герой не прошёл скан — смотри scan_report.json");
    }

    /// <summary>Заменённые тела: <c>models/heroes/&lt;dir&gt;/&lt;dir&gt;.vmdl_c</c> и <c>*_base.vmdl_c</c> своей папки.</summary>
    private static List<(string Path, long Size)> Bodies(string[] dirs, Dictionary<string, long> sizes)
    {
        var result = new List<(string, long)>();
        foreach (var (path, size) in sizes)
        {
            if (!path.StartsWith("models/heroes/", StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = path.Split('/');
            if (parts.Length != 4 || !dirs.Contains(parts[2], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(parts[3]);
            if (name.Equals(parts[2], StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_base", StringComparison.OrdinalIgnoreCase))
            {
                result.Add((path, size));
            }
        }

        return result;
    }

    /// <summary>Имена костей модели из декомпилированного текста.</summary>
    private static HashSet<string>? Bones(string pak, string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wibe-bones", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "m.vmdl");
            var start = new ProcessStartInfo(Inspector)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-i", pak, "-f", path, "-o", target, "-d" })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(180_000);
            if (!File.Exists(target))
            {
                return null;
            }

            return Regex.Matches(File.ReadAllText(target), "bone_name = \"([^\"]+)\"")
                .Select(m => m.Groups[1].Value.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // временная папка уберётся позже
            }
        }
    }

    /// <summary>Есть ли у модели анимации: блок ANIM/ASEQ или внешняя группа анимаций.</summary>
    private static bool HasAnimations(string pak, string path)
    {
        var text = InspectorBlocks(pak, path);
        if (text is null)
        {
            return false;
        }

        return text.Contains("Block: ANIM", StringComparison.Ordinal) ||
               text.Contains("Block: ASEQ", StringComparison.Ordinal) ||
               text.Contains(".vagrp_c", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Текст инспектора по блокам ресурса (флаги -b срабатывают только с фильтром).</summary>
    private static string? InspectorBlocks(string pak, string path)
    {
        var start = new ProcessStartInfo(Inspector)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-i", pak, "-f", path, "-b", "DATA" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        var text = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(180_000);
        return process.HasExited && process.ExitCode == 0 ? text : null;
    }

    private static bool InspectorLists(string pak)
    {
        var start = new ProcessStartInfo(Inspector)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-i", pak, "--vpk_list" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        var text = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(300_000);
        return process.HasExited && process.ExitCode == 0 &&
               text.Contains(".vmdl_c", StringComparison.OrdinalIgnoreCase);
    }

    private static List<(string Slot, int Id, int Style)> Picks(string folder, EconCatalog catalog)
    {
        var result = new List<(string Slot, int Id, int Style)>();
        var view = HeroFolderCatalog.Read(folder, folder);
        if (view is null)
        {
            return result;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in view.Sections)
        {
            foreach (var card in section.Cards)
            {
                var slot = card.Slot;
                if (slot.Length == 0 || slot.Equals("bundle", StringComparison.OrdinalIgnoreCase))
                {
                    slot = "body";
                }

                if (used.Contains(slot) || catalog.ItemById(card.Id) is null)
                {
                    continue;
                }

                used.Add(slot);
                result.Add((slot, card.Id, card.Styles > 1 ? 1 : 0));
                break;
            }

            if (result.Count >= 6)
            {
                break;
            }
        }

        return result;
    }

    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? Directory.GetCurrentDirectory();
    }

    internal static (string Name, int Size)? Block(byte[] file, string tag)
    {
        if (file.Length < 32)
        {
            return null;
        }

        try
        {
            var table = 8 + (int)BitConverter.ToUInt32(file, 8);
            var count = (int)BitConverter.ToUInt32(file, 12);
            if (table < 16 || table >= file.Length || count < 0 || count > 4096)
            {
                return null;
            }

            for (var i = 0; i < count && table + i * 12 + 12 <= file.Length; i++)
            {
                var entry = table + i * 12;
                var name = Encoding.ASCII.GetString(file, entry, 4).TrimEnd('\0', '\u007f', ' ');
                if (string.Equals(name, tag, StringComparison.Ordinal))
                {
                    return (name, (int)BitConverter.ToUInt32(file, entry + 8));
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AppendToRegistry(
        List<(string Hero, List<(string Slot, int Id)> Picks, Dictionary<string, int> Styles,
            SortedDictionary<string, (long Size, string Sha)> Models)> heroes)
    {
        var path = Path.Combine(RepoRoot(), "tests", "Wibe.Tests", "locked_heroes_snapshot.json");
        var doc = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
            : new JsonObject
            {
                ["note"] = "Реестр замков: герои, прошедшие скан «пять из пяти».",
                ["heroes"] = new JsonArray(),
                ["models"] = new JsonArray(),
            };

        var rows = doc["heroes"] as JsonArray ?? new JsonArray();
        var models = doc["models"] as JsonArray ?? new JsonArray();
        var locked = rows.OfType<JsonObject>().Select(h => (string?)h["hero"]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var hero in heroes)
        {
            if (!locked.Add(hero.Hero))
            {
                continue;
            }

            var pickRows = new JsonArray();
            foreach (var (slot, id) in hero.Picks)
            {
                pickRows.Add(new JsonObject { ["slot"] = slot, ["item_id"] = id });
            }

            var ownModels = new JsonArray();
            foreach (var (model, hash) in hero.Models)
            {
                ownModels.Add(new JsonObject { ["path"] = model, ["size"] = hash.Size, ["sha256"] = hash.Sha });
            }

            rows.Add(new JsonObject
            {
                ["hero"] = hero.Hero,
                ["models"] = ownModels,
                ["picks"] = pickRows,
                ["styles"] = new JsonObject(hero.Styles.Select(s => KeyValuePair.Create(s.Key, JsonNode.Parse(s.Value.ToString())))),
                ["scanned"] = "пять из пяти",
            });

            foreach (var (model, hash) in hero.Models)
            {
                models.Add(new JsonObject { ["path"] = model, ["size"] = hash.Size, ["sha256"] = hash.Sha });
            }
        }

        doc["heroes"] = rows;
        doc["models"] = models;
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        }));
    }
}
