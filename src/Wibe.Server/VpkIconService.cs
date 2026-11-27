using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Wibe.Server;

/// <summary>
/// Иконка предмета из пака игры, по требованию и навсегда в кэше. Внутри pak01_dir.vpk лежит
/// скомпилированная текстура <c>panorama/images/&lt;image_inventory&gt;_png.vtex_c</c>; достаём её
/// одиночным вызовом инспектора только когда картинку впервые попросили, а готовый PNG кладём в
/// %LOCALAPPDATA%\ICDK\IconCache\&lt;sha1&gt;.png. Повторная отдача — чтение файла, без процессов.
/// </summary>
public static class VpkIconService
{
    private static readonly object Gate = new();

    public static string CacheDirectory
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ICDK", "IconCache");
            System.IO.Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>PNG картинки предмета: из кэша или добытый из пака. null — такой иконки нет в паке.</summary>
    public static byte[]? Get(string inventory)
    {
        var inv = Normalize(inventory);
        if (inv.Length == 0)
        {
            return null;
        }

        var cache = Path.Combine(CacheDirectory, Hash(inv) + ".png");
        if (File.Exists(cache))
        {
            try
            {
                var warm = File.ReadAllBytes(cache);
                return warm.Length > 8 && warm[0] == 0x89 && warm[1] == 0x50 ? warm : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        lock (Gate)
        {
            if (File.Exists(cache))
            {
                return File.ReadAllBytes(cache);
            }

            return Extract(inv, cache);
        }
    }

    /// <summary>Сколько миллисекунд заняла отдача (для теста и лога).</summary>
    public static (byte[]? Png, long Ms) Timed(string inventory)
    {
        var watch = Stopwatch.StartNew();
        var png = Get(inventory);
        watch.Stop();
        return (png, watch.ElapsedMilliseconds);
    }

    private static byte[]? Extract(string inv, string cache)
    {
        var install = Wibe.Core.Dota.DotaInstallation.Detect(out _);
        if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
        {
            return null;
        }

        var exe = Wibe.Core.Tools.Source2ViewerCli.Locate();
        if (exe is null)
        {
            return null;
        }

        var temp = Path.Combine(Path.GetTempPath(), "icdk-icon-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            var target = Path.Combine(temp, "icon.png");
            var start = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-i", install.MainPakDir, "-f", IconPath(inv), "-o", target, "-d", "-q" })
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
            process.WaitForExit(60_000);
            if (!File.Exists(target))
            {
                return null;
            }

            var png = File.ReadAllBytes(target);
            if (png.Length < 64 || png[0] != 0x89 || png[1] != 0x50 || png[2] != (byte)'N' || png[3] != (byte)'G')
            {
                return null;
            }

            ImageStore.AtomicWrite(cache, png);
            return png;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(temp, true);
            }
            catch (IOException)
            {
                // временная папка уберётся позже
            }
        }
    }

    /// <summary>Компилированный путь картинки в паке: <c>panorama/images/&lt;inv&gt;_png.vtex_c</c>.</summary>
    public static string IconPath(string inventory)
    {
        var clean = Normalize(inventory);
        if (clean.StartsWith("panorama/", StringComparison.OrdinalIgnoreCase))
        {
            return clean.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? clean : clean + "_c";
        }

        return clean.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)
            ? "panorama/images/" + clean + "_c"
            : "panorama/images/" + clean + "_png.vtex_c";
    }

    private static string Normalize(string inventory)
    {
        var clean = (inventory ?? string.Empty).Replace('\\', '/').Trim();
        while (clean.StartsWith("/", StringComparison.Ordinal))
        {
            clean = clean[1..];
        }

        return clean.TrimStart('.', ' ');
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()))).ToLowerInvariant();
}
