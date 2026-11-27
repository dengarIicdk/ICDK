using System.Diagnostics;
using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.Econ;

namespace Wibe.Server;

/// <summary>
/// Ленивая 3D-примерка: модель героя или предмета выгружается из пака игры в бинарный
/// <c>.glb</c> только когда её впервые показали, и остаётся в
/// <c>%LOCALAPPDATA%\ICDK\MeshCache\</c>. В установщик не попадает ни байта геометрии.
/// </summary>
public static class GlbPreviewService
{
    private static readonly object Gate = new();

    public static string CacheDirectory
    {
        get
        {
            var root = Path.Combine(VaultRoot(), "meshes");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>Корень кэша — тот же, что у иконок и каталога (см. <see cref="CacheLocator"/>).</summary>
    private static string VaultRoot() => CacheLocator.Root;




    /// <summary>Есть ли у .glb внешние текстуры, которых нет на диске (тогда нужен пересбор).</summary>
    private static bool HasMissingTextures(byte[] glb)
    {
        try
        {
            if (glb.Length < 20 || glb[0] != (byte)'g' || glb[1] != (byte)'l' || glb[2] != (byte)'T' || glb[3] != (byte)'F')
            {
                return false;
            }

            var jsonLength = BitConverter.ToInt32(glb, 12);
            if (jsonLength <= 0 || 20 + jsonLength > glb.Length)
            {
                return false;
            }

            var json = System.Text.Encoding.UTF8.GetString(glb, 20, jsonLength);
            var index = 0;
            while (true)
            {
                var at = json.IndexOf("\"uri\"", index, StringComparison.Ordinal);
                if (at < 0)
                {
                    return false;
                }

                var open = json.IndexOf('"', at + 5);
                var close = open < 0 ? -1 : json.IndexOf('"', open + 1);
                if (open < 0 || close < 0)
                {
                    return false;
                }

                var uri = json.Substring(open + 1, close - open - 1);
                index = close + 1;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Texture(uri) is null)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Имя папки кэша совпадает с моделью строго до "__" (или целиком для базовых частей).</summary>
    private static bool MatchesStem(string folderName, string stem)
    {
        if (string.Equals(folderName, stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var head = folderName.Split("__", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return string.Equals(head, stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PNG-текстура из кэша моделей (для loader.setResourcePath).</summary>
    public static byte[]? Texture(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var file = Path.GetFileName(Uri.UnescapeDataString(name).Split('?')[0]);
        foreach (var dir in new[] { Path.Combine(CacheDirectory, "tex"), CacheDirectory, Path.Combine(VaultRoot(), "temp", "meshes"), Path.Combine(VaultRoot(), "meshes") })
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var direct = Path.Combine(dir, file);
                if (File.Exists(direct))
                {
                    return File.ReadAllBytes(direct);
                }

                var found = Directory.EnumerateFiles(dir, file, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null)
                {
                    return File.ReadAllBytes(found);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
    /// <summary>Готовый .glb предмета из &lt;vault&gt;/temp/meshes/&lt;name&gt;__&lt;name&gt;/*.glb по имени модели.</summary>
    private static byte[]? ReadyItem(string model)
    {
        var stem = Path.GetFileNameWithoutExtension(model.Trim());
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var bases = new[] { Path.Combine(VaultRoot(), "temp", "meshes"), Path.Combine(VaultRoot(), "meshes") };
        foreach (var baseDir in bases)
        {
            try
            {
                if (!Directory.Exists(baseDir))
                {
                    continue;
                }

                foreach (var dir in Directory.EnumerateDirectories(baseDir, "*", SearchOption.TopDirectoryOnly))
                {
                    // имя папки: <модель>__<целевой_меш>; сравниваем строго левую часть до "__"
                    var dirName = Path.GetFileName(dir);
                    if (!MatchesStem(dirName, stem))
                    {
                        continue;
                    }

                    var file = Directory.EnumerateFiles(dir, "*.glb", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    file ??= Directory.EnumerateFiles(dir, "*.glb", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (file is not null)
                    {
                        return File.ReadAllBytes(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // кэш занят — пробуем следующий каталог
            }
        }

        return null;
    }

    /// <summary>Готовый .glb тела героя из общего кэша вида &lt;vault&gt;/temp/meshes/&lt;slug&gt;/*.glb.</summary>
    private static byte[]? ReadyHero(string folder)
    {
        var slug = Key(folder).Replace('-', '_');
        foreach (var baseDir in new[] { Path.Combine(VaultRoot(), "temp", "meshes"), Path.Combine(VaultRoot(), "meshes") })
        {
            try
            {
                if (!Directory.Exists(baseDir))
                {
                    continue;
                }

                var direct = Path.Combine(baseDir, slug);
                if (Directory.Exists(direct))
                {
                    var file = Directory.EnumerateFiles(direct, "*.glb", SearchOption.AllDirectories).FirstOrDefault();
                    if (file is not null)
                    {
                        var bytes = File.ReadAllBytes(file);
                        return bytes;
                    }
                }

                foreach (var dir in Directory.EnumerateDirectories(baseDir, "*" + slug + "*", SearchOption.TopDirectoryOnly))
                {
                    var file = Directory.EnumerateFiles(dir, "*.glb", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(f => Path.GetFileName(f).StartsWith(slug, StringComparison.OrdinalIgnoreCase));
                    if (file is not null)
                    {
                        return File.ReadAllBytes(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // кэш может быть занят — идём дальше по кандидатам
            }
        }

        return null;
    }

    /// <summary>Тело героя: <c>models/heroes/&lt;hero&gt;/&lt;hero&gt;.vmdl_c</c>.</summary>
    public static (byte[]? Glb, long Ms, string? Error) Hero(string folder)
    {
        var watch = Stopwatch.StartNew();
        // тёплая отдача не должна трогать пак: путь модели нужен только при холодном экспорте
        var ready = ReadyHero(folder);
        if (ready is not null)
        {
            try
            {
                var name = "hero-" + Key(folder);
                if (!File.Exists(Path.Combine(CacheDirectory, name + ".glb")))
                {
                    File.WriteAllBytes(Path.Combine(CacheDirectory, name + ".glb"), ready);
                }

                var sidecar = Path.Combine(CacheDirectory, name + "_meshinfo.json");
                if (!File.Exists(sidecar))
                {
                    File.WriteAllText(sidecar, "{\"mesh_groups\":[],\"mesh_labels\":[],\"slot\":\"\"}");
                }
            }
            catch (IOException) { }
        }

        var (glb, error) = ready is not null
            ? (ready, (string?)null)
            : Get("hero-" + Key(folder), () => HeroModelPath(folder), "body");
        watch.Stop();
        return (glb, watch.ElapsedMilliseconds, error);
    }

    /// <summary>Модель предмета по его <c>item_id</c>; style влияет только на кэш-ключ.</summary>
    public static (byte[]? Glb, long Ms, string? Error) Item(int itemId, int style = 1)
    {
        var fastName = "item-" + itemId + "-s" + Math.Max(1, style);
        var fastPath = Path.Combine(CacheDirectory, fastName + ".glb");
        if (File.Exists(fastPath))
        {
            try
            {
                var fast = File.ReadAllBytes(fastPath);
                if (fast.Length > 32)
                {
                    return (fast, 0, null);
                }
            }
            catch (IOException)
            {
            }
        }

        var watch = Stopwatch.StartNew();
        // каталог econ читается только при холодном экспорте — тёплая отдача это чтение файла
        var model = ItemModelPath(itemId);
        var name = "item-" + itemId + "-s" + Math.Max(1, style);
        var ready = model is null ? null : ReadyItem(model);
        if (ready is not null)
        {
            // найденное в общем кэше кладём и в наш, чтобы работали тёплые проверки и сайдкар
            var target = Path.Combine(CacheDirectory, name + ".glb");
            try
            {
                if (!File.Exists(target)) File.WriteAllBytes(target, ready);
                var sidecar = Path.Combine(CacheDirectory, name + "_meshinfo.json");
                if (!File.Exists(sidecar))
                {
                    File.WriteAllText(sidecar, "{\"mesh_groups\":[],\"mesh_labels\":[],\"slot\":\"\"}");
                }
            }
            catch (IOException) { }
        }

        var (glb, error) = ready is not null
            ? (ready, (string?)null)
            : Get(name, () => ItemModelPath(itemId), "item");
        watch.Stop();
        return (glb, watch.ElapsedMilliseconds, error);
    }

    private static string? ItemModelPath(int itemId)
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid)
        {
            return null;
        }

        var catalog = EconCatalog.Load(install);
        var item = catalog.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return null;
        }

        var model = string.IsNullOrWhiteSpace(item.CompiledModel) ? item.ExtraModels.FirstOrDefault() : item.CompiledModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return model.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? model : model + "_c";
    }


    /// <summary>
    /// Метаданные модели: `mesh_groups`, `mesh_labels`, слот. Читаются из сайдкара
    /// рядом с `.glb` в AssetVault/meshes, а если его нет — отдаётся пустой набор.
    /// </summary>
    public static object Meta(int itemId = 0, string? folder = null)
    {
        var name = itemId > 0 ? "item-" + itemId + "-s1" : "hero-" + Key(folder ?? string.Empty);
        var info = Path.Combine(CacheDirectory, name + "_meshinfo.json");
        if (!File.Exists(info))
        {
            return new { mesh_groups = Array.Empty<string>(), mesh_labels = Array.Empty<string>(), slot = string.Empty };
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(info));
            var root = doc.RootElement;
            string[] Read(string prop) => root.TryGetProperty(prop, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? value.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                : Array.Empty<string>();
            return new
            {
                mesh_groups = Read("mesh_groups"),
                mesh_labels = Read("mesh_labels"),
                slot = root.TryGetProperty("slot", out var slot) ? slot.GetString() ?? string.Empty : string.Empty,
            };
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return new { mesh_groups = Array.Empty<string>(), mesh_labels = Array.Empty<string>(), slot = string.Empty };
        }
    }
    private static string? HeroModelPath(string folder)
    {
        var install = DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid)
        {
            return null;
        }

        var key = Key(folder);
        foreach (var candidate in new[]
                 {
                     "models/heroes/" + key + "/" + key + ".vmdl_c",
                     "models/heroes/" + key.Replace("_", string.Empty, StringComparison.Ordinal) + "/" +
                     key.Replace("_", string.Empty, StringComparison.Ordinal) + ".vmdl_c",
                 })
        {
            if (Wibe.Core.Vpk.VpkIndex.Paths(install.MainPakDir).Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Key(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static (byte[]? Glb, string? Error) Get(string cacheName, Func<string?> modelProvider, string slot)
    {
        var cache = CacheDirectory;
        var glbPath = Path.Combine(cache, cacheName + ".glb");
        var infoPath = Path.Combine(cache, cacheName + "_meshinfo.json");
        if (File.Exists(glbPath))
        {
            try
            {
                var warm = File.ReadAllBytes(glbPath);
                return warm.Length > 12 ? (warm, null) : (null, "пустой glb в кэше");
            }
            catch (IOException)
            {
                return (null, "кэш недоступен");
            }
        }

        lock (Gate)
        {
            if (File.Exists(glbPath))
            {
                return (File.ReadAllBytes(glbPath), null);
            }

            // готовый внешний кэш: берём как есть, без экспорта
            var ready = slot == "body"
                ? CacheLocator.TryFindHero(Path.GetFileNameWithoutExtension(cacheName).Replace("hero-", string.Empty, StringComparison.OrdinalIgnoreCase))
                : null;
            if (ready is not null && File.Exists(ready))
            {
                var bytes = File.ReadAllBytes(ready);
                if (bytes.Length > 12 && bytes[0] == (byte)'g' && bytes[1] == (byte)'l' && bytes[2] == (byte)'T' && bytes[3] == (byte)'F')
                {
                    ImageStore.AtomicWrite(glbPath, bytes);
                    return (bytes, null);
                }
            }

            var modelPath = modelProvider();
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return (null, "модель не найдена в каталоге");
            }

            var install = DotaInstallation.Detect(out _);
            var exe = Wibe.Core.Tools.Source2ViewerCli.Locate();
            if (install is null || exe is null)
            {
                return (null, "нет пака или инспектора");
            }

            var temp = Path.Combine(Path.GetTempPath(), "icdk-glb-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(temp);
            try
            {
                var start = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                // материалы и текстуры лежат в том же VPK: экспорт должен идти по архиву, не по папке
                var pak = install.MainPakDir;
                if (Directory.Exists(pak))
                {
                    var candidate = Path.Combine(pak, "pak01_dir.vpk");
                    if (File.Exists(candidate))
                    {
                        pak = candidate;
                    }
                }

                foreach (var arg in new[]
                         {
                             "-i", pak, "-f", modelPath, "-o", temp, "-d",
                             "--gltf_export_format", "glb", "--gltf_export_materials",
                             "--gltf_textures_adapt", "--gltf_export_animations",
                         })
                {
                    start.ArgumentList.Add(arg);
                }

                using var process = Process.Start(start);
                if (process is null)
                {
                    return (null, "инспектор не запустился");
                }

                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(120_000);

                var produced = Directory.GetFiles(temp, "*.glb", SearchOption.AllDirectories).FirstOrDefault();
                if (produced is null)
                {
                    return (null, "инспектор не создал glb");
                }

                var glb = File.ReadAllBytes(produced);
                if (glb.Length < 12 || glb[0] != (byte)'g' || glb[1] != (byte)'l' || glb[2] != (byte)'T' || glb[3] != (byte)'F')
                {
                    return (null, "экспорт не является glTF-бинарём");
                }

                // рядом с .glb CLI кладёт текстуры .png со относительными uri — сохраняем их
                var texDir = Path.Combine(CacheDirectory, "tex");
                Directory.CreateDirectory(texDir);
                foreach (var png in Directory.GetFiles(temp, "*.png", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(texDir, Path.GetFileName(png));
                    try
                    {
                        if (!File.Exists(target))
                        {
                            ImageStore.AtomicWrite(target, File.ReadAllBytes(png));
                        }
                    }
                    catch (IOException)
                    {
                        // текстура занята — не мешаем экспорту
                    }
                }

                ImageStore.AtomicWrite(glbPath, glb);
                var info = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    model = modelPath,
                    slot,
                    bytes = glb.Length,
                    exported_at = DateTimeOffset.UtcNow.ToString("O"),
                });
                ImageStore.AtomicWrite(infoPath, info);
                return (glb, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, ex.Message);
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (IOException)
                {
                    // временная папка уберётся позже
                }
            }
        }
    }
}
