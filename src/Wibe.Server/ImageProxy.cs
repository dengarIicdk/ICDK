using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Wibe.Core.Econ;

namespace Wibe.Server;

/// <summary>
/// Картинки для интерфейса. Иконки косметики берутся ТОЛЬКО с локального диска:
/// они один раз декодируются из pak01_dir.vpk (см. IconExtractor) и отдаются за миллисекунды.
/// Портреты героев — с CDN Valve с кешем на диске, а если сети нет — из локальной иконки героя.
/// </summary>
internal static class ImageProxy
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly ConcurrentDictionary<string, byte[]> Memory = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, bool> Missing = new(StringComparer.OrdinalIgnoreCase);

    public static string CacheDirectory => ImageStore.Directory;

    public static byte[] Placeholder { get; } = Encoding.UTF8.GetBytes("""
        <svg xmlns="http://www.w3.org/2000/svg" width="256" height="192" viewBox="0 0 256 192">
          <g fill="none" stroke="#c98a52" stroke-linecap="round" stroke-linejoin="round">
            <path d="M74 146l20-40 22 22 18-30 30 48" stroke-width="5" opacity="0.34"/>
            <circle cx="158" cy="62" r="15" stroke-width="5" opacity="0.34"/>
          </g>
          <text x="128" y="172" text-anchor="middle" font-family="Segoe UI, sans-serif" font-size="13"
                fill="#a8845c" opacity="0.5">нет фото</text>
        </svg>
        """);

    public const string PlaceholderType = "image/svg+xml";

    /// <summary>Иконка предмета: локальный PNG из VPK, иначе заглушка. Никаких сетевых запросов.</summary>
    public static Task<(byte[] Bytes, string ContentType)> GetAsync(
        string inventory,
        string? name = null,
        CancellationToken token = default,
        bool useMemory = true,
        bool allowMarket = true)
    {
        var file = ImageStore.FindItem(inventory);
        if (file is not null)
        {
            return ServeFileAsync(file, useMemory && inventory.Length < 160, token);
        }

        return Task.FromResult((Placeholder, PlaceholderType));
    }

    public static bool IsCached(string inventory, string? name = null) => ImageStore.FindItem(inventory) is not null;

    /// <summary>Портрет/кроп героя: кеш на диске → CDN → локальная иконка героя → заглушка.</summary>
    public static async Task<(byte[] Bytes, string ContentType)> GetHeroAsync(
        string folder,
        bool crop,
        CancellationToken token = default,
        bool useMemory = true)
    {
        var key = HeroKey(folder, crop);
        if (useMemory && Memory.TryGetValue(key, out var cached))
        {
            return (cached, "image/png");
        }

        foreach (var extension in new[] { ".png", ".jpg", ".webp" })
        {
            var existing = Path.Combine(CacheDirectory, Hash(key) + extension);
            if (!File.Exists(existing))
            {
                continue;
            }

            var fromDisk = await ServeFileAsync(existing, useMemory, token).ConfigureAwait(false);
            if (fromDisk.ContentType != PlaceholderType)
            {
                return fromDisk;
            }
        }

        if (!Missing.ContainsKey(key))
        {
            var url = crop ? ItemImages.HeroCrop(folder) : ItemImages.HeroPortrait(folder);
            var fetched = await TryDownloadAsync(url, token).ConfigureAwait(false);
            if (fetched is not null)
            {
                Save(key, fetched.Value.Bytes, fetched.Value.Type);
                if (useMemory)
                {
                    Memory[key] = fetched.Value.Bytes;
                }

                return (fetched.Value.Bytes, fetched.Value.Type);
            }

            Missing[key] = true;
        }

        var localIcon = ImageStore.FindHeroIcon(folder);
        if (localIcon is not null)
        {
            return await ServeFileAsync(localIcon, useMemory, token).ConfigureAwait(false);
        }

        return (Placeholder, PlaceholderType);
    }

    public static string HeroKey(string folder, bool crop) =>
        "hero:" + (folder ?? string.Empty).Trim().ToLowerInvariant() + (crop ? ":crop" : ":portrait");

    private static async Task<(byte[] Bytes, string ContentType)> ServeFileAsync(string file, bool useMemory, CancellationToken token)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
            var type = Detect(bytes) ?? "image/png";
            if (useMemory && bytes.Length <= 262_144)
            {
                Memory[file] = bytes;
            }

            return (bytes, type);
        }
        catch (IOException)
        {
            return (Placeholder, PlaceholderType);
        }
    }

    private static async Task<(byte[] Bytes, string Type)?> TryDownloadAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            var type = Detect(bytes);
            return type is null || bytes.Length <= 64 ? null : (bytes, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private static void Save(string key, byte[] bytes, string contentType)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var extension = contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => ".png",
            };

            var file = Path.Combine(CacheDirectory, Hash(key) + extension);
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static string? Detect(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length > 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        {
            return "image/webp";
        }

        return null;
    }

    private static string Hash(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
