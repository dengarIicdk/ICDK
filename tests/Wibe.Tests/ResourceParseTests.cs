using System.Diagnostics;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Mods;
using Xunit;
using Xunit.Abstractions;

namespace Wibe.Tests;

/// <summary>
/// Каждый ресурс собранного pak06 читается декомпилятором ValveResourceFormat.
///
/// Это тот же код, которым игра разбирает заголовки: если модель, материал или партикл
/// собран криво, тест падает здесь, а не Source 2 на рабочем столе. Проверка идёт по
/// настоящему паку, который собрало Rust-ядро, а не по отдельным файлам.
/// </summary>
public class ResourceParseTests
{
    private readonly ITestOutputHelper _output;

    public ResourceParseTests(ITestOutputHelper output) => _output = output;

    private static string ToolPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "bin", "Source2Viewer-CLI.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("не найден bin/Source2Viewer-CLI.exe рядом с проектом");
    }

    [Fact]
    public void Every_model_material_and_particle_in_the_pack_reads_back()
    {
        var install = DotaInstallation.Detect(out var error);
        Assert.True(install is not null && install.IsValid, "Dota 2 не найдена: " + error);
        var catalog = EconCatalog.Load(install!);

        // Мир (башни, крипы, эмблема) плюс аркана — самый широкий набор того, что сборка
        // пересобирает сама: модели, материалы стилей и партиклы.
        var world = new RustCoreModBuilder.WorldSelection(
            RadiantTower: 23195, DireTower: 23712, Emblem: 23238, RadiantCreep: 18876, DireCreep: 18839);
        var picks = new List<HeroPicks>
        {
            new("skeleton_king", new List<(string Slot, int ItemId)> { ("body", 13456), ("taunt", 24230) }),
            new("juggernaut", new List<(string Slot, int ItemId)> { ("taunt", 4994) }),
        };

        var dir = Path.Combine(Path.GetTempPath(), "wibe-resource-parse");
        Directory.CreateDirectory(dir);
        var pak = Path.Combine(dir, "parse.vpk");
        var log = new List<string>();
        var built = RustCoreModBuilder.Build(install!, catalog, picks, l =>
        {
            lock (log)
            {
                log.Add(l);
            }
        }, world, pak);
        Assert.True(built.Success, built.Message);

        var tool = ToolPath();
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-i", pak, "--test", "-e", "vmdl_c,vmat_c,vpcf_c,vagrp_c,vsurf_c",
                 })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15 * 60 * 1000), "декомпилятор не успел прочитать пак");
        var all = output + errors;

        var bad = all.Split('\n')
            .Where(l => l.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("Failed to", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("[ERR", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(bad.Count == 0, "VRF не прочитал часть ресурсов:\n" + string.Join('\n', bad.Take(20)));
        Assert.True(process.ExitCode == 0, "декомпилятор вернул " + process.ExitCode + ":\n" + all[^Math.Min(2000, all.Length)..]);

        // И он действительно прочитал файлы, а не пустой пак: модель, материал, партикл — все виды.
        var counted = all.Split((char)10)
            .Where(l => l.Contains("resources of version", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var line in counted)
        {
            _output.WriteLine(line.Trim());
        }

        Assert.Contains(counted, l => l.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(counted, l => l.Contains("Particle", StringComparison.OrdinalIgnoreCase));
        // в этой сборке нет ни одного материала, которые ядро писало бы само; если появится —
        // он обязан читаться тем же проходом
        Assert.DoesNotContain(counted, l => l.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
    }
}
