using System.Diagnostics;
using System.Text;

namespace Wibe.Core.Tools;

public sealed record VpkEntryInfo(string Path, uint Crc, long Size);

/// <summary>
/// Обёртка над bin/Source2Viewer-CLI.exe — проверенный, самый быстрый способ получить индекс VPK
/// (388 776 файлов за ~2 с). В M1 его заменит ValveResourceFormat в процессе, но как fallback
/// и как «второе мнение» он остаётся навсегда.
/// </summary>
public static class Source2ViewerCli
{
    public const string ExeName = "Source2Viewer-CLI.exe";

    public static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("WIBE_S2V");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var dir in CandidateDirectories())
        {
            var candidate = Path.Combine(dir, ExeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Движок может быть вшит в одиночный exe — распакуем при первом обращении.
        return EngineBundle.Ensure();
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "bin");
        yield return Directory.GetCurrentDirectory();
        yield return Path.Combine(Directory.GetCurrentDirectory(), "bin");
    }

    public static IReadOnlyList<VpkEntryInfo> ListVpk(string vpkPath, string? exePath = null, TimeSpan? timeout = null)
    {
        exePath ??= Locate() ?? throw new FileNotFoundException($"{ExeName} не найден (положи в bin/ или задай WIBE_S2V)");

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(vpkPath);
        psi.ArgumentList.Add("--vpk_list");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить " + ExeName);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(3)).TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // уже завершился
            }

            throw new TimeoutException($"{ExeName} не ответил за отведённое время на {Path.GetFileName(vpkPath)}");
        }

        var text = output.GetAwaiter().GetResult();
        var errors = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            var first = errors.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.Trim().Length > 0);
            throw new InvalidOperationException($"{ExeName} вернул код {process.ExitCode}: {first}");
        }

        return Parse(text);
    }

    /// <summary>
    /// Извлекает один ассет из игры один-в-один (скомпилированный, без декомпиляции).
    /// Возвращает путь к извлечённому файлу.
    /// </summary>
    public static string ExtractAsset(string sourceVpk, string assetPath, string outputDir, string? exePath = null)
    {
        exePath ??= Locate() ?? throw new FileNotFoundException($"{ExeName} не найден");
        Run(exePath, new[] { "-i", sourceVpk, "-f", assetPath, "-o", outputDir }, TimeSpan.FromMinutes(5));

        var expected = Path.Combine(outputDir, assetPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(expected))
        {
            // CLI иногда кладёт файл с сохранением регистра/иным разделителем — ищем по имени.
            var fileName = Path.GetFileName(assetPath);
            var found = Directory.Exists(outputDir)
                ? Directory.EnumerateFiles(outputDir, fileName, SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (found is null)
            {
                throw new FileNotFoundException($"не удалось извлечь {assetPath} из {Path.GetFileName(sourceVpk)}");
            }

            expected = found;
        }

        return expected;
    }

    /// <summary>Извлекает ассет и конвертирует его в «человеческий» формат (текстура → PNG, модель → glTF/текст).</summary>
    public static void CreateVpk(string stagingDir, string outputVpk, string? exePath = null, long chunkSizeBytes = 0)
    {
        exePath ??= Locate() ?? throw new FileNotFoundException($"{ExeName} не найден");
        var args = new List<string> { "-i", stagingDir, "--vpk_create", outputVpk };
        if (chunkSizeBytes > 0)
        {
            args.Add("--vpk_create_chunk_size");
            args.Add(chunkSizeBytes.ToString());
        }

        Run(exePath, args, TimeSpan.FromMinutes(10));
        if (!File.Exists(outputVpk))
        {
            throw new InvalidOperationException($"VPK не создан: {outputVpk}");
        }
    }

    private static void Run(string exePath, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить " + ExeName);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"{ExeName} не ответил за {timeout.TotalSeconds:0} с");
        }

        _ = stdout.GetAwaiter().GetResult();
        var errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            var first = errors.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.Trim().Length > 0);
            throw new InvalidOperationException($"{ExeName} код {process.ExitCode}: {first}");
        }
    }

    /// <summary>Строки формата "models/heroes/axe/axe.vmdl_c CRC:005890d3a8 size:1314278".</summary>
    public static IReadOnlyList<VpkEntryInfo> Parse(string output)
    {
        var result = new List<VpkEntryInfo>(4096);
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var crcIndex = text.LastIndexOf(" CRC:", StringComparison.Ordinal);
            if (crcIndex <= 0)
            {
                continue;
            }

            var path = text[..crcIndex].Trim();
            var rest = text[(crcIndex + 5)..];

            var sizeIndex = rest.LastIndexOf(" size:", StringComparison.Ordinal);
            var crcText = sizeIndex >= 0 ? rest[..sizeIndex] : rest;
            var sizeText = sizeIndex >= 0 ? rest[(sizeIndex + 6)..] : "0";

            if (!uint.TryParse(crcText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var crc))
            {
                continue;
            }

            _ = long.TryParse(sizeText.Trim(), out var size);
            result.Add(new VpkEntryInfo(path, crc, size));
        }

        return result;
    }
}
