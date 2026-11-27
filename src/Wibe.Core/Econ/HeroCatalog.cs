using System.Text;
using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.Tools;
using Wibe.Core.Vdf;

namespace Wibe.Core.Econ;

public sealed record HeroInfo(string Folder, string Name, string Image = "")
{
    /// <summary>Горизонтальный портрет с CDN Valve.</summary>
    public string Portrait => string.IsNullOrEmpty(Image)
        ? $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/{Folder}.png"
        : "https://cdn.cloudflare.steamstatic.com" + Image.Split('?')[0];

    /// <summary>Вертикальный портрет (crop) с CDN Valve.</summary>
    public string Crop => Portrait.Replace("/heroes/", "/heroes/crops/");
}

/// <summary>
/// Список настоящих героев Dota 2 (без технических юнитов и алиасов):
/// dotaconstants (имена и картинки) → файлы npc_heroes.txt (#base) → индекс пака.
/// Всё кешируется на диск, поэтому офлайн-запуск тоже работает.
/// </summary>
public static class HeroCatalog
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["abyssal_underlord"] = "Underlord",
        ["antimage"] = "Anti-Mage",
        ["centaur"] = "Centaur Warrunner",
        ["doom_bringer"] = "Doom",
        ["furion"] = "Nature's Prophet",
        ["life_stealer"] = "Lifestealer",
        ["magnataur"] = "Magnus",
        ["necrolyte"] = "Necrophos",
        ["nevermore"] = "Shadow Fiend",
        ["obsidian_destroyer"] = "Outworld Destroyer",
        ["queenofpain"] = "Queen of Pain",
        ["rattletrap"] = "Clockwerk",
        ["shredder"] = "Timbersaw",
        ["skeleton_king"] = "Wraith King",
        ["treant"] = "Treant Protector",
        ["troll_warlord"] = "Troll Warlord",
        ["vengefulspirit"] = "Vengeful Spirit",
        ["windrunner"] = "Windranger",
        ["wisp"] = "Io",
        ["zuus"] = "Zeus",
        ["earthshaker"] = "Earthshaker",
        ["ogre_magi"] = "Ogre Magi",
        ["keeper_of_the_light"] = "Keeper of the Light",
        ["legion_commander"] = "Legion Commander",
        ["lone_druid"] = "Lone Druid",
        ["sand_king"] = "Sand King",
        ["shadow_demon"] = "Shadow Demon",
        ["shadow_shaman"] = "Shadow Shaman",
        ["spirit_breaker"] = "Spirit Breaker",
        ["storm_spirit"] = "Storm Spirit",
        ["ember_spirit"] = "Ember Spirit",
        ["earth_spirit"] = "Earth Spirit",
        ["void_spirit"] = "Void Spirit",
        ["templar_assassin"] = "Templar Assassin",
        ["phantom_assassin"] = "Phantom Assassin",
        ["phantom_lancer"] = "Phantom Lancer",
        ["monkey_king"] = "Monkey King",
        ["chaos_knight"] = "Chaos Knight",
        ["dragon_knight"] = "Dragon Knight",
        ["crystal_maiden"] = "Crystal Maiden",
        ["death_prophet"] = "Death Prophet",
        ["dark_seer"] = "Dark Seer",
        ["dark_willow"] = "Dark Willow",
        ["drow_ranger"] = "Drow Ranger",
        ["witch_doctor"] = "Witch Doctor",
        ["witch_slayer"] = "Witch Slayer",
        ["winter_wyvern"] = "Winter Wyvern",
        ["ancient_apparition"] = "Ancient Apparition",
        ["arc_warden"] = "Arc Warden",
        ["bounty_hunter"] = "Bounty Hunter",
        ["naga_siren"] = "Naga Siren",
        ["skywrath_mage"] = "Skywrath Mage",
        ["faceless_void"] = "Faceless Void",
        ["primal_beast"] = "Primal Beast",
        ["ember_spirit"] = "Ember Spirit",
        ["keeper_of_the_light"] = "Keeper of the Light",
        ["tusk"] = "Tusk",
        ["elder_titan"] = "Elder Titan",
        ["nyx_assassin"] = "Nyx Assassin",
        ["slark"] = "Slark",
    };

    public static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "cache");

    public static string CacheFile => Path.Combine(CacheDirectory, "heroes_v3.json");

    public static string LogFile => Path.Combine(CacheDirectory, "heroes.log");

    public static async Task<IReadOnlyList<HeroInfo>> LoadAsync(
        DotaInstallation install,
        IReadOnlyCollection<string> fallbackFolders,
        CancellationToken token = default)
    {
        var cached = ReadCache();
        if (cached.Count > 0)
        {
            Log($"cache: {cached.Count}");
            return cached;
        }

        var remote = await FetchDotaconstantsAsync(token).ConfigureAwait(false);
        Log($"dotaconstants: {remote.Count}");
        if (remote.Count >= 100)
        {
            WriteCache(remote);
            return remote;
        }

        var local = ReadNpcHeroes(install);
        Log($"npc_heroes: {local.Count}");
        if (local.Count >= 100)
        {
            WriteCache(local);
            return local;
        }

        var fallback = Merge(fallbackFolders);
        Log($"index: {fallback.Count}");
        return fallback;
    }

    /// <summary>Канонический список героев: 127 записей с локализованными именами и путями картинок.</summary>
    private static async Task<List<HeroInfo>> FetchDotaconstantsAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WibeFittingRoom/0.5");

            using var response = await http
                .GetAsync("https://raw.githubusercontent.com/odota/dotaconstants/master/build/heroes.json", token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log("dotaconstants status: " + (int)response.StatusCode);
                return new List<HeroInfo>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new List<HeroInfo>();
            }

            var heroes = new List<HeroInfo>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var node = property.Value;
                var name = node.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                if (name is null || !name.StartsWith("npc_dota_hero_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var localized = node.TryGetProperty("localized_name", out var localizedNode) ? localizedNode.GetString() : null;
                var image = node.TryGetProperty("img", out var imageNode) ? imageNode.GetString() ?? string.Empty : string.Empty;
                var folder = name["npc_dota_hero_".Length..].ToLowerInvariant();
                if (folder.Length == 0)
                {
                    continue;
                }

                heroes.Add(new HeroInfo(
                    folder,
                    string.IsNullOrWhiteSpace(localized) ? DisplayName(folder) : localized!,
                    image));
            }

            return heroes
                .GroupBy(h => h.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Log("dotaconstants error: " + ex.GetType().Name + " " + ex.Message);
            return new List<HeroInfo>();
        }
    }

    /// <summary>npc_heroes.txt — это #base-манифест: имена файлов = папки героев.</summary>
    private static List<HeroInfo> ReadNpcHeroes(DotaInstallation install)
    {
        try
        {
            var file = Path.Combine(CacheDirectory, "econ", "npc_heroes.txt");
            if (!File.Exists(file))
            {
                var raw = Path.Combine(CacheDirectory, "econ", "raw-npc");
                Directory.CreateDirectory(raw);
                var extracted = Source2ViewerCli.ExtractAsset(install.MainPakDir, "scripts/npc/npc_heroes.txt", raw);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.Copy(extracted, file, overwrite: true);
            }

            var result = new List<HeroInfo>();
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("#base", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var start = trimmed.IndexOf('"');
                var end = trimmed.LastIndexOf('"');
                if (start < 0 || end <= start)
                {
                    continue;
                }

                var relative = trimmed[(start + 1)..end].Replace('\\', '/');
                var fileName = relative.Split('/').Last();
                if (!fileName.StartsWith("npc_dota_hero_", StringComparison.OrdinalIgnoreCase) ||
                    !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var folder = fileName["npc_dota_hero_".Length..^4].ToLowerInvariant();
                if (folder is "base" or "target_dummy" or "temp")
                {
                    continue;
                }

                result.Add(new HeroInfo(folder, DisplayName(folder)));
            }

            return result
                .GroupBy(h => h.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or FileNotFoundException)
        {
            Log("npc_heroes error: " + ex.GetType().Name + " " + ex.Message);
            return new List<HeroInfo>();
        }
    }

    private static List<HeroInfo> Merge(IReadOnlyCollection<string> folders) =>
        folders
            .Where(f => !f.Contains("base", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains("dummy", StringComparison.OrdinalIgnoreCase))
            .Select(f => new HeroInfo(f, DisplayName(f)))
            .GroupBy(h => h.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<HeroInfo> ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) > CacheLifetime)
            {
                return new List<HeroInfo>();
            }

            var data = JsonSerializer.Deserialize<List<HeroInfo>>(File.ReadAllText(CacheFile, Encoding.UTF8));
            return data is { Count: >= 100 and <= 140 } ? data : new List<HeroInfo>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new List<HeroInfo>();
        }
    }

    private static void WriteCache(List<HeroInfo> heroes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(heroes), Encoding.UTF8);
        }
        catch (IOException)
        {
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.AppendAllText(LogFile, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    public static string DisplayName(string folder) =>
        DisplayNames.TryGetValue(folder, out var known) ? known : Prettify(folder);

    public static string Prettify(string folder)
    {
        var words = folder.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
