using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Wibe.Server;

/// <summary>
/// Официальные иконки косметики с торговой площадки Steam: market_hash_name → icon_url,
/// дальше картинка берётся с cdn.steamstatic (economy/image). Индекс кешируется на диск.
/// </summary>
internal static class MarketIcons
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly ConcurrentDictionary<string, string> Index = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, bool> Missing = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly object ThrottleLock = new();

    private static DateTime _lastRequest = DateTime.MinValue;

    private static readonly object SaveLock = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static int _dirty;

    private static long _blockedAt;

    private static string CacheFile => Path.Combine(ImageProxy.CacheDirectory, "market_icons.json");

    public static int Count => Index.Count;

    /// <summary>Steam только что ответил 403/429 — не тратим запросы впустую.</summary>
    public static bool Blocked =>
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _blockedAt) < TimeSpan.FromSeconds(60).Ticks;

    public static string IconUrl(string icon) => $"https://community.steamstatic.com/economy/image/{icon}/330x192";

    /// <summary>Ищет иконку по отображаемому имени предмета. null — на площадке не нашлось.</summary>
    public static async Task<string?> ResolveAsync(string name, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        LoadOnce();

        var key = Normalize(name);
        if (key.Length == 0)
        {
            return null;
        }

        if (Index.TryGetValue(key, out var known))
        {
            return known;
        }

        if (Missing.ContainsKey(key))
        {
            return null;
        }

        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Index.TryGetValue(key, out known))
            {
                return known;
            }

            foreach (var candidate in SearchQueries(name))
            {
                var found = await SearchAsync(candidate, key, token).ConfigureAwait(false);
                if (found is not null)
                {
                    return found;
                }
            }

            // 429/503 — это лимит Steam, а не «иконки нет»: пробуем в следующий раз.
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _blockedAt) < TimeSpan.FromSeconds(45).Ticks)
            {
                return null;
            }

            Missing[key] = true;
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IEnumerable<string> SearchQueries(string name)
    {
        var trimmed = name;

        // Каталог подписывает слоты хвостом « - Weapon», на площадке таких имён нет.
        var dash = trimmed.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 3 && trimmed[(dash + 3)..].Length is > 2 and < 14 && !trimmed[(dash + 3)..].Contains(' '))
        {
            trimmed = trimmed[..dash];
        }

        yield return trimmed;
        if (!string.Equals(trimmed, name, StringComparison.Ordinal))
        {
            yield return name;
        }

        foreach (var prefix in new[] { "Genuine ", "Inscribed ", "Frozen ", "Cursed ", "Auspex ", "Heroic ", "Corrupted ", "Exalted " })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            trimmed = trimmed[prefix.Length..];
            yield return trimmed;
        }

        // «Axe's Axe» → «Axe»
        var apostrophe = trimmed.IndexOf('\'');
        if (apostrophe > 2)
        {
            yield return trimmed[..apostrophe];
        }
    }

    private static async Task<string?> SearchAsync(string query, string key, CancellationToken token)
    {
        var url = "https://steamcommunity.com/market/search/render/?appid=570&norender=1&count=100" +
                  "&query=" + Uri.EscapeDataString(query);

        try
        {
            // Площадка банит за залповые запросы: не чаще одного поиска в 350 мс.
            lock (ThrottleLock)
            {
                var wait = TimeSpan.FromMilliseconds(350) - (DateTime.UtcNow - _lastRequest);
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }

                _lastRequest = DateTime.UtcNow;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WibeFittingRoom/0.5");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 403 or 429 or 503)
                {
                    Interlocked.Exchange(ref _blockedAt, DateTime.UtcNow.Ticks);
                }

                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? exact = null;
            foreach (var item in results.EnumerateArray())
            {
                var hashName = item.TryGetProperty("hash_name", out var hashNode) ? hashNode.GetString() : null;
                var icon = item.TryGetProperty("asset_description", out var asset) && asset.TryGetProperty("icon_url", out var iconNode)
                    ? iconNode.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(hashName) || string.IsNullOrWhiteSpace(icon))
                {
                    continue;
                }

                var normalized = Normalize(hashName);
                Index.TryAdd(normalized, icon!);
                MarkDirty();

                if (string.Equals(normalized, key, StringComparison.OrdinalIgnoreCase))
                {
                    exact = icon;
                }
            }

            SaveIfDirty();
            return exact ?? Index.GetValueOrDefault(key);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (char.IsWhiteSpace(c) || c is '\'' or '’' or '-' or '_' or '.')
            {
                builder.Append(' ');
            }
        }

        var text = builder.ToString();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text.Trim();
    }

    private static void LoadOnce()
    {
        if (_dirty > 0 || Index.Count > 0)
        {
            return;
        }

        lock (SaveLock)
        {
            if (Index.Count > 0)
            {
                return;
            }

            try
            {
                if (!File.Exists(CacheFile))
                {
                    _dirty = 1;
                    return;
                }

                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile, Encoding.UTF8));
                if (data is null)
                {
                    _dirty = 1;
                    return;
                }

                foreach (var pair in data)
                {
                    Index.TryAdd(pair.Key, pair.Value);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
            }
            finally
            {
                _dirty = 1;
            }
        }
    }

    private static void MarkDirty()
    {
        if (Interlocked.Increment(ref _dirty) % 25 == 0)
        {
            SaveIfDirty();
        }
    }

    private static void SaveIfDirty()
    {
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(ImageProxy.CacheDirectory);
                var snapshot = new Dictionary<string, string>(Index, StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(snapshot, Json), Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
    }
}
