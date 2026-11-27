using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Wibe.Core.Tools;

public sealed record IconIndex(string BuildId, int Count, IReadOnlyDictionary<string, string> Map)
{
    public string? Find(string inventory)
    {
        if (string.IsNullOrWhiteSpace(inventory))
        {
            return null;
        }

        var key = inventory.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        return Map.TryGetValue(key, out var path) ? path : null;
    }
}

public sealed record IconExtractResult(bool Success, string Message, int Files, string Directory);

/// <summary>
/// Вытаскивает ВСЕ иконки косметики прямо из game/dota/pak01_dir.vpk: Source2Viewer-CLI
/// декодирует panorama/images/econ/**/*_png.vtex_c в PNG. Сеть не нужна вообще.
/// Результат: png/panorama/images/econ/.../*.png + png/icons_index.json (карта image_inventory → файл).
/// </summary>
public static class IconExtractor
{
    public const string IndexFileName = "icons_index.json";

    private static readonly string[] Filters =
    {
        "panorama/images/econ/",
        "panorama/images/heroes/",
    };

    public static string IndexPath(string outputRoot) => Path.Combine(outputRoot, IndexFileName);

    public static IconIndex LoadIndex(string outputRoot, string? buildId = null)
    {
        try
        {
            var path = IndexPath(outputRoot);
            if (!File.Exists(path))
            {
                return new IconIndex(buildId ?? string.Empty, 0, new Dictionary<string, string>());
            }

            var payload = JsonSerializer.Deserialize<IconIndexFile>(File.ReadAllBytes(path));
            if (payload is null)
            {
                return new IconIndex(buildId ?? string.Empty, 0, new Dictionary<string, string>());
            }

            if (buildId is not null && payload.BuildId.Length > 0 && !string.Equals(payload.BuildId, buildId, StringComparison.Ordinal))
            {
                return new IconIndex(buildId, 0, new Dictionary<string, string>());
            }

            return new IconIndex(payload.BuildId, payload.Count, payload.Map);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new IconIndex(buildId ?? string.Empty, 0, new Dictionary<string, string>());
        }
    }

    public static bool IsFresh(string outputRoot, string buildId)
    {
        var index = LoadIndex(outputRoot, buildId);
        return index.Count > 0 && string.Equals(index.BuildId, buildId, StringComparison.Ordinal);
    }

    /// <summary>Декодирует все иконки из пака игры в outputRoot. Идемпотентно: существующие файлы перезаписываются.</summary>
    public static IconExtractResult ExtractAll(
        string mainPakDir,
        string buildId,
        string outputRoot,
        Action<int, string>? progress = null,
        CancellationToken token = default)
    {
        var exe = Source2ViewerCli.Locate();
        if (exe is null)
        {
            return new IconExtractResult(false, "движок Source2Viewer-CLI не найден", 0, outputRoot);
        }

        var temp = Path.Combine(Path.GetTempPath(), "wibe-icons-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            progress?.Invoke(0, "распаковываю иконки из pak01_dir.vpk…");
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[]
                     {
                         "-i", mainPakDir,
                         "-f", string.Join(',', Filters),
                         "-d",
                         "-o", temp,
                         "--threads", "12",
                         "-q",
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new IconExtractResult(false, "не удалось запустить декодер", 0, outputRoot);
            }

            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            while (!process.WaitForExit(500))
            {
                if (token.IsCancellationRequested)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    return new IconExtractResult(false, "извлечение отменено", 0, outputRoot);
                }
            }

            _ = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                var first = errors.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return new IconExtractResult(false, $"декодер вернул код {process.ExitCode}: {first}", 0, outputRoot);
            }

            progress?.Invoke(0, "раскладываю PNG и строю индекс…");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var copied = 0;
            var sourceRoot = Path.Combine(temp, "panorama", "images");
            if (!Directory.Exists(sourceRoot))
            {
                return new IconExtractResult(false, "декодер не создал panorama/images", 0, outputRoot);
            }

            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.png", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(temp, file).Replace('\\', '/').ToLowerInvariant();
                if (relative.EndsWith("_png.png", StringComparison.Ordinal))
                {
                    relative = relative[..^"_png.png".Length] + ".png";
                }

                var destination = Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                copied++;

                AddKeys(map, relative);
                if (copied % 500 == 0)
                {
                    progress?.Invoke(copied, $"разложено {copied} иконок");
                }
            }

            var payload = new IconIndexFile
            {
                BuildId = buildId,
                Count = map.Count,
                ExtractedAt = DateTimeOffset.UtcNow,
                Map = map,
            };
            Directory.CreateDirectory(outputRoot);
            File.WriteAllBytes(
                IndexPath(outputRoot),
                JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = false }));

            return new IconExtractResult(true, $"иконок из VPK: {copied}, ключей в индексе: {map.Count}", copied, outputRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            return new IconExtractResult(false, ex.Message, 0, outputRoot);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Ключи для /api/img: «econ/items/axe/x/x», «econ/items/axe/x» (папка = имя файла),
    /// «hero:axe» для иконок героев.
    /// </summary>
    private static void AddKeys(Dictionary<string, string> map, string relative)
    {
        var withoutExt = relative[..^4];
        if (relative.StartsWith("panorama/images/econ/", StringComparison.Ordinal))
        {
            var econ = withoutExt["panorama/images/".Length..];
            map.TryAdd(econ, relative);

            var parts = econ.Split('/');
            if (parts.Length >= 2 && string.Equals(parts[^1], parts[^2], StringComparison.Ordinal))
            {
                map.TryAdd(string.Join('/', parts[..^1]), relative);
            }

            return;
        }

        if (relative.StartsWith("panorama/images/heroes/icons/npc_dota_hero_", StringComparison.Ordinal))
        {
            var name = withoutExt["panorama/images/heroes/icons/npc_dota_hero_".Length..];
            map.TryAdd("hero:" + name, relative);
        }
    }

    public sealed class IconIndexFile
    {
        public string BuildId { get; set; } = string.Empty;

        public int Count { get; set; }

        public DateTimeOffset ExtractedAt { get; set; }

        public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
