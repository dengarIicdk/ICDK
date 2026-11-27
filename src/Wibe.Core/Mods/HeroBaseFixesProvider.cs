using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wibe.Core.Mods;

/// <summary>Одна предсобранная база из dota2modmanager/hero-base-fixes.</summary>
public sealed record HeroBaseFix(string Id, string Hero, int ItemId, bool Persona, IReadOnlyList<int> Parts, int Styles = 1);

/// <summary>Файл базы: путь внутри VPK и git-хеш его содержимого.</summary>
public sealed record HeroBaseFile(string Path, string Sha1);

/// <summary>
/// Готовые базы Аркан/Личностей/проблемных Immortal из открытого репозитория
/// dota2modmanager/hero-base-fixes. База содержит уже пересобранные модели тела,
/// деталей и эффектов, поэтому её можно класть в языковой пак напрямую — без items_game.
///
/// Скачивание последовательное (без пула потоков), каждый файл проверяется по git-blob SHA1
/// и кэшируется на диск. Ошибка сети не валит сборку: вызывающий код откатывается на
/// извлечение модели из pak01.
/// </summary>
public sealed class HeroBaseFixesProvider
{
    public const string Repository = "dota2modmanager/hero-base-fixes";
    private const string Branch = "main";
    private const string ManifestUrl = "https://raw.githubusercontent.com/" + Repository + "/" + Branch + "/manifest.json";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _root;
    private Dictionary<string, List<HeroBaseFile>>? _manifest;
    private readonly HashSet<string> _ready = new(StringComparer.OrdinalIgnoreCase);

    public HeroBaseFixesProvider(string? cacheRoot = null)
    {
        _root = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WibeChanger", "cache", "hero-base-fixes");
    }

    /// <summary>Кэш рядом с иконками и каталогом, чтобы не качать базы повторно.</summary>
    public string CacheRoot => _root;

    /// <summary>Известные базы. Список повторяет arcana-bases.json движка dota2-mod-manager.</summary>
    public static IReadOnlyList<HeroBaseFix> Known { get; } = new[]
    {
        new HeroBaseFix("bladeform_legacy", "juggernaut", 9059, false, new[] { 9059, 30261, 7592 }),
        new HeroBaseFix("bladeform_legacy_v2", "juggernaut", 9059, false, new[] { 9059, 30261, 7592 }, Styles: 2),
        new HeroBaseFix("blades_of_voth_domosh", "legion_commander", 5810, false, new[] { 5810, 37140 }),
        new HeroBaseFix("claszian_apostasy", "faceless_void", 18033, false, new[] { 18033, 22822, 22823, 22824, 22825 }),
        new HeroBaseFix("claszian_apostasy_v2", "faceless_void", 18033, false, new[] { 18033, 22822, 22823, 22824, 22825 }, Styles: 2),
        new HeroBaseFix("compass_of_the_rising_gale_bundle", "windrunner", 13806, false, new[] { 13804, 13805, 13806, 13807, 13808, 32650 }),
        new HeroBaseFix("compass_of_the_rising_gale_bundle_v2", "windrunner", 13806, false, new[] { 13804, 13805, 13806, 13807, 13808, 32650 }, Styles: 2),
        new HeroBaseFix("dread_retribution", "drow_ranger", 19090, false, new[] { 19088, 19089, 19090, 19091, 19092, 19093, 19094, 37144 }),
        new HeroBaseFix("dread_retribution_v2", "drow_ranger", 19090, false, new[] { 19088, 19089, 19090, 19091, 19092, 19093, 19094, 37144 }, Styles: 2),
        new HeroBaseFix("feast_of_abscession", "pudge", 7756, false, new[] { 7756, 29687 }),
        new HeroBaseFix("feast_of_abscession_v2", "pudge", 7756, false, new[] { 7756, 29687 }, Styles: 2),
        new HeroBaseFix("fists_of_axe_unleashed", "axe", 12964, false, new[] { 12964, 12965, 12966, 12968 }),
        new HeroBaseFix("fractal_horns_of_inner_abysm", "terrorblade", 5957, false, new[] { 5957, 37161 }),
        new HeroBaseFix("frost_avalanche", "crystal_maiden", 7385, false, new[] { 7385 }),
        new HeroBaseFix("phantom_advent_v2", "spectre", 9662, false, new[] { 9662, 9663, 12312, 12313, 12314, 18750 }),
        new HeroBaseFix("planetfall", "earthshaker", 12692, false, new[] { 12692, 34379, 34380, 34381, 34382 }),
        new HeroBaseFix("planetfall_v2", "earthshaker", 12692, false, new[] { 12692, 34379, 34380, 34381, 34382 }, Styles: 2),
        new HeroBaseFix("the_devotions_of_dragonus", "skywrath_mage", 18539, false, new[] { 18539, 18540, 18541, 18542, 18543, 18544 }),
        new HeroBaseFix("the_devotions_of_dragonus_v2", "skywrath_mage", 18539, false, new[] { 18539, 18540, 18541, 18542, 18543, 18544 }, Styles: 2),
        new HeroBaseFix("the_magus_cypher", "rubick", 12451, false, new[] { 12451, 12427 }),
        new HeroBaseFix("voidstorm_asylum", "razor", 23095, false, new[] { 23095, 23096, 23097, 23098, 23099 }),
        new HeroBaseFix("voidstorm_asylum_v2", "razor", 23095, false, new[] { 23095, 23096, 23097, 23098, 23099 }, Styles: 2),
        new HeroBaseFix("acolyte_of_the_lost_arts", "invoker", 13042, true, new[] { 13042 }),
        new HeroBaseFix("axe_automaton_persona", "axe", 31367, true, new[] { 31367 }),
        new HeroBaseFix("bristleback_automaton_persona", "bristleback", 36214, true, new[] { 36214 }),
        new HeroBaseFix("conduit_of_the_blueheart", "crystal_maiden", 19205, true, new[] { 19205 }),
        new HeroBaseFix("davion_of_dragon_hold", "dragon_knight", 18113, true, new[] { 18113 }),
        new HeroBaseFix("legion_commander_automaton_persona", "legion_commander", 36191, true, new[] { 36191 }),
        new HeroBaseFix("mirana_of_nightsilver", "mirana", 18178, true, new[] { 18178 }),
        new HeroBaseFix("morphling_automaton_persona", "morphling", 36193, true, new[] { 36193 }),
        new HeroBaseFix("oracle_automaton_persona", "oracle", 31357, true, new[] { 31357 }),
        new HeroBaseFix("the_disciples_path", "antimage", 13783, true, new[] { 13783 }),
        new HeroBaseFix("the_toy_butcher", "pudge", 13786, true, new[] { 13786 }),
    };

    /// <summary>База для выбранного предмета и героя; null — готовой базы нет.</summary>
    public static HeroBaseFix? Find(int itemId, string? heroKey = null)
    {
        var hero = (heroKey ?? string.Empty).Trim().ToLowerInvariant();
        if (hero.StartsWith("npc_dota_hero_", StringComparison.Ordinal))
        {
            hero = hero["npc_dota_hero_".Length..];
        }

        var hit = Known.FirstOrDefault(b => b.ItemId == itemId &&
            (hero.Length == 0 || string.Equals(b.Hero, hero, StringComparison.OrdinalIgnoreCase)));
        return hit;
    }

    /// <summary>Скачивает manifest и возвращает карту «id базы → файлы».</summary>
    public async Task<IReadOnlyDictionary<string, List<HeroBaseFile>>> LoadManifestAsync(CancellationToken token = default)
    {
        if (_manifest is not null)
        {
            return _manifest;
        }

        Directory.CreateDirectory(_root);
        var manifestPath = Path.Combine(_root, "manifest.json");
        string? text = null;
        try
        {
            using var response = await Http.GetAsync(ManifestUrl, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                await File.WriteAllTextAsync(manifestPath, text, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // ниже попробуем локальную копию
        }

        text ??= File.Exists(manifestPath) ? await File.ReadAllTextAsync(manifestPath, token).ConfigureAwait(false) : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new IOException("не удалось получить manifest.json hero-base-fixes");
        }

        using var document = JsonDocument.Parse(text);
        var result = new Dictionary<string, List<HeroBaseFile>>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("bases", out var bases) || bases.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("manifest.json без секции bases");
        }

        foreach (var baseEntry in bases.EnumerateObject())
        {
            var files = new List<HeroBaseFile>();
            foreach (var file in baseEntry.Value.EnumerateObject())
            {
                var path = file.Name.Replace('\\', '/').TrimStart('/');
                var sha = file.Value.GetString() ?? string.Empty;
                if (!IsSafePath(path) || sha.Length != 40)
                {
                    continue;
                }

                files.Add(new HeroBaseFile(path, sha.ToLowerInvariant()));
            }

            if (files.Count > 0)
            {
                result[baseEntry.Name] = files;
            }
        }

        _manifest = result;
        return result;
    }

    /// <summary>
    /// Все файлы одной базы, скачанные и проверенные. Пусто при ошибке сети: сборка продолжит
    /// работу обычным извлечением модели из pak01.
    /// </summary>
    public async Task<(IReadOnlyList<(string Path, byte[] Data)> Files, string? Error)> LoadAsync(
        string baseId,
        CancellationToken token = default)
    {
        try
        {
            var manifest = await LoadManifestAsync(token).ConfigureAwait(false);
            if (!manifest.TryGetValue(baseId, out var list))
            {
                return (Array.Empty<(string, byte[])>(), $"базы {baseId} нет в manifest");
            }

            var files = new List<(string, byte[])>(list.Count);
            Directory.CreateDirectory(_root);
            foreach (var file in list)
            {
                token.ThrowIfCancellationRequested();
                var blob = Path.Combine(_root, "blobs", file.Sha1);
                byte[] data;
                if (File.Exists(blob))
                {
                    data = await File.ReadAllBytesAsync(blob, token).ConfigureAwait(false);
                    if (!BlobSha(data).Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        data = await DownloadAsync(baseId, file, blob, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    data = await DownloadAsync(baseId, file, blob, token).ConfigureAwait(false);
                }

                files.Add((file.Path, data));
            }

            _ready.Add(baseId);
            return (files, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException)
        {
            return (Array.Empty<(string, byte[])>(), ex.Message);
        }
    }

    public bool IsReady(string baseId) => _ready.Contains(baseId);

    private static async Task<byte[]> DownloadAsync(string baseId, HeroBaseFile file, string blob, CancellationToken token)
    {
        var url = "https://raw.githubusercontent.com/" + Repository + "/" + Branch + "/src/" + baseId + "/" +
                  string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{file.Path}: HTTP {(int)response.StatusCode}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        var actual = BlobSha(bytes);
        if (!actual.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{file.Path}: SHA1 {actual} вместо {file.Sha1}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        var temp = blob + ".part";
        await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
        File.Move(temp, blob, overwrite: true);
        return bytes;
    }

    /// <summary>git blob SHA1: "blob {len}\0" + данные. Именно этим хешем репозиторий называет файлы.</summary>
    public static string BlobSha(ReadOnlySpan<byte> data)
    {
        var header = Encoding.ASCII.GetBytes($"blob {data.Length}\0");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(header);
        sha.AppendData(data);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsSafePath(string path)
    {
        if (path.Length == 0 || path.Contains(':'))
        {
            return false;
        }

        var parts = path.Split('/');
        return parts.All(p => p.Length > 0 && p != "." && p != "..") &&
               (path.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("particles/", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WibeFittingRoom/0.9");
        return client;
    }
}
