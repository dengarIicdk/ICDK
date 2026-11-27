using System.Text.Json;

namespace Wibe.Server;

/// <summary>
/// Вкладки страницы героя = реальные подпапки Heroes/&lt;Папка&gt;. Старый каталог
/// используется только как источник уже распознанных карточек, но никогда не
/// добавляет вкладку, которой нет на диске.
/// </summary>
public static class HeroFolderCatalog
{
    public sealed record Card(int Id, string Name, string Slot, string Rarity, string Art, string Ability = "", string? Title = null, string Prefab = "", int Styles = 1, string? ImgUrl = null);
    public sealed record Section(string Name, IReadOnlyList<Card> Cards);
    public sealed record View(string Folder, string HeroKey, string Name, bool IsPersona, int PersonaId, IReadOnlyList<Section> Sections)
    {
        public int TotalCards => Sections.Sum(s => s.Cards.Count);
    }

    private sealed class SeedSection
    {
        public string? Name { get; set; }
        public List<SeedCard>? Cards { get; set; }
    }

    private sealed class SeedCard
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Slot { get; set; }
        public string? Rarity { get; set; }
        public string? Art { get; set; }
        public string? Ability { get; set; }
        public string? Title { get; set; }
        public string? Prefab { get; set; }
        public int Styles { get; set; }
    }

    private sealed class CollectionEntry
    {
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public string? Hero { get; set; }
        public string? Img { get; set; }
        public int PersonaId { get; set; }
    }

    private static readonly JsonSerializerOptions Reads = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<string?> RootLazy = new(FindRoot);
    private static readonly Lazy<Dictionary<string, List<CollectionEntry>>> CollectionLazy = new(LoadCollection);
    private static readonly Lazy<Dictionary<string, List<SeedSection>>> SeedsLazy = new(LoadSeeds);
    private static readonly Lazy<Dictionary<string, List<SeedCard>>> DefaultsLazy = new(LoadDefaults);

    public static string? Root => RootLazy.Value;

    public static View? Read(string heroKey, string? requestedFolder)
    {
        var root = Root;
        if (root is null)
        {
            return ReadFromSeeds(heroKey, requestedFolder);
        }

        var folder = ResolveFolder(root, heroKey, requestedFolder);
        if (folder is null)
        {
            return null;
        }

        var dirs = Directory.EnumerateDirectories(Path.Combine(root, folder))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seeds = SeedsLazy.Value;
        seeds.TryGetValue(folder, out var source);
        var bySection = source?
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, SeedSection>(StringComparer.OrdinalIgnoreCase);

        var sections = dirs
            .Select(dir => new Section(dir, bySection.TryGetValue(dir, out var sec) ? Clean(sec) : Array.Empty<Card>()))
            .ToList();

        // Плоская папка без подкаталогов (Dragon Knight) — все разделы из уже
        // распознанных карточек: у него реальные слоты (Weapon, Shield, Head,
        // Dragon Form) не имеют одноимённых папок на диске.
        if (sections.Count == 0 && source is { Count: > 0 })
        {
            foreach (var seed in source.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
            {
                var cards = Clean(seed);
                if (cards.Count > 0)
                {
                    sections.Add(new Section(seed.Name!, cards));
                }
            }
        }

        // Luna — единственное исключение: Ability Effects лежит в сервисной папке,
        // но её пять карточек по-прежнему принадлежат странице Luna.
        if (string.Equals(folder, "Luna", StringComparison.OrdinalIgnoreCase) &&
            !sections.Any(s => string.Equals(s.Name, "Ability Effects", StringComparison.OrdinalIgnoreCase)) &&
            bySection.TryGetValue("Ability Effects", out var ability))
        {
            var cards = Clean(ability);
            if (cards.Count > 0)
            {
                sections.Add(new Section("Ability Effects", cards));
            }
        }

        var collection = CollectionLazy.Value.Values.SelectMany(v => v)
            .FirstOrDefault(e => string.Equals(e.Name, folder, StringComparison.OrdinalIgnoreCase));
        var personaId = collection?.PersonaId ?? 0;
        var name = collection?.Name ?? folder;

        // Папка Личности почти всегда содержит её собственный DEFAULT-сет.
        // Если OCR не нашёл там ни одной покупной карточки, показываем саму
        // Личность как выбираемую базу — её item_id и есть базовая вещь.
        if (personaId != 0)
        {
            var sets = sections.FirstOrDefault(s => string.Equals(s.Name, "Fullsets", StringComparison.OrdinalIgnoreCase))
                       ?? sections.FirstOrDefault();
            if (sets is not null && sets.Cards.Count == 0)
            {
                var art = string.IsNullOrWhiteSpace(collection?.Img)
                    ? $"/api/crop?path=heroes/{Uri.EscapeDataString(collection?.Slug ?? Slug(folder))}.jpg"
                    : collection!.Img!.Replace("/art/heroes/", "/api/crop?path=heroes/", StringComparison.OrdinalIgnoreCase);
                var card = new Card(personaId, name, "persona_selector", "arcana", art, Prefab: "wearable");
                sections = sections
                    .Select(s => ReferenceEquals(s, sets) ? new Section(s.Name, new[] { card }) : s)
                    .ToList();
            }
        }

        // У Kez и Largo в папке лежит только серая карточка EQUIPPED. Их настоящие
        // базовые item_id берём из локального каталога econ, иначе страница пустая.
        if (sections.Sum(s => s.Cards.Count) == 0 &&
            DefaultsLazy.Value.TryGetValue(folder, out var defaults) && defaults.Count > 0)
        {
            var cards = defaults.Select(card => new Card(
                card.Id,
                card.Name ?? string.Empty,
                string.IsNullOrWhiteSpace(card.Slot) ? "misc" : card.Slot,
                card.Rarity ?? string.Empty,
                $"/api/crop?path=heroes/{Uri.EscapeDataString(heroKey)}.jpg",
                Prefab: "default_item")).ToList();
            var first = sections.FirstOrDefault(s => string.Equals(s.Name, "Fullsets", StringComparison.OrdinalIgnoreCase))
                        ?? sections.FirstOrDefault();
            if (first is null)
            {
                sections.Add(new Section("Fullsets", cards));
            }
            else
            {
                var index = sections.IndexOf(first);
                sections[index] = first with { Cards = cards };
            }
        }

        sections = MergeRegistryExtras(sections, heroKey, folder, personaId);

        // Папка с одной серой карточкой EQUIPPED не должна оставлять мёртвую
        // вкладку с нулём. Пустые колонки убираем из выдачи целиком.
        sections = sections.Where(section => section.Cards.Count > 0).ToList();

        return new View(folder, heroKey, name, personaId != 0, personaId, sections);
    }


    /// <summary>
    /// Все проверенные шмотки героя из реестра <see cref="Wibe.Core.Items.AllHeroItemsScanner"/>:
    /// инвентарь пака читается один раз на процесс, дальше это словарь в памяти.
    /// </summary>
    private static readonly Lazy<Dictionary<string, List<Wibe.Core.Items.AllHeroItemsScanner.Entry>>> ExtrasLazy =
        new(BuildRegistryExtras);


    /// <summary>
    /// Ключи героя в разных системах: реестр пака зовёт его npc_dota_hero_abaddon,
    /// папка кропов — Abaddon, каталог — abaddon. Сравниваем по «только буквы и цифры».
    /// </summary>

    /// <summary>
    /// Папки Личностей: в главной сетке они не нужны — переключатель живёт внутри
    /// страницы базового героя. Записи замка героев это не затрагивает.
    /// </summary>
    public static HashSet<string> PersonaFolders()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in CollectionLazy.Value.Values.SelectMany(v => v))
        {
            if (!string.IsNullOrWhiteSpace(entry.Name) && entry.PersonaId != 0)
            {
                set.Add(entry.Name);
            }
        }

        return set;
    }

    /// <summary>Страницы базовых героев без дублей Личностей.</summary>
    public static List<string> BaseHeroFolders()
    {
        var personas = PersonaFolders();
        return CollectionLazy.Value.Values.SelectMany(v => v)
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.PersonaId == 0 && !personas.Contains(e.Name!))
            .Select(e => e.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    private static IEnumerable<string> Aliases(string? heroKey)
    {
        var raw = (heroKey ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            yield break;
        }

        yield return Key(raw);

        var catalog = CollectionLazy.Value.Values.SelectMany(v => v)
            .FirstOrDefault(e =>
                string.Equals(Key(e.Name), Key(raw), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Key(e.Slug), Key(raw), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Key(e.Hero), Key(raw), StringComparison.OrdinalIgnoreCase));
        if (catalog is null)
        {
            yield break;
        }

        foreach (var key in new[] { catalog.Name, catalog.Slug, catalog.Hero })
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                yield return Key(key);
            }
        }
    }

    /// <summary>Ключ героя: реестр зовёт его npc_dota_hero_abaddon, папка — Abaddon.</summary>
    private static string Key(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var prefix in new[] { "npc_dota_hero_", "npc_dota_", "npc_" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static Dictionary<string, List<Wibe.Core.Items.AllHeroItemsScanner.Entry>> BuildRegistryExtras()
    {
        var map = new Dictionary<string, List<Wibe.Core.Items.AllHeroItemsScanner.Entry>>(StringComparer.OrdinalIgnoreCase);
        var entries = RegistryEntries();
        if (entries is null)
        {
            return map;
        }

        foreach (var entry in entries)
        {
            foreach (var alias in Aliases(entry.HeroKey))
            {
                if (!map.TryGetValue(alias, out var list))
                {
                    map[alias] = list = new List<Wibe.Core.Items.AllHeroItemsScanner.Entry>();
                }

                list.Add(entry);
            }
        }

        return map;
    }

    /// <summary>
    /// Реестр проверенных шмоток: сначала залоченный манифест, а если его рядом нет —
    /// скан инвентаря пака игры.
    /// </summary>
    private static List<Wibe.Core.Items.AllHeroItemsScanner.Entry>? RegistryEntries()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var at = new DirectoryInfo(start); at is not null; at = at.Parent)
            {
                var candidate = Path.Combine(at.FullName, "tests", "Wibe.Tests", "locked_catalog_manifest.json");
                if (File.Exists(candidate))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(candidate));
                        var items = new List<Wibe.Core.Items.AllHeroItemsScanner.Entry>();
                        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                        {
                            items.Add(new Wibe.Core.Items.AllHeroItemsScanner.Entry(
                                item.GetProperty("hero").GetString() ?? string.Empty,
                                item.GetProperty("section").GetString() ?? "Body",
                                item.GetProperty("slot").GetString() ?? string.Empty,
                                item.GetProperty("itemId").GetInt32(),
                                item.GetProperty("name").GetString() ?? string.Empty,
                                item.GetProperty("rarity").GetString() ?? string.Empty,
                                item.GetProperty("model").GetString() ?? string.Empty,
                                item.GetProperty("icon").GetString() ?? string.Empty));
                        }

                        if (items.Count > 0)
                        {
                            return items;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        // битый манифест — пробуем скан пака
                    }
                }
            }
        }

        try
        {
            var install = Wibe.Core.Dota.DotaInstallation.Detect(out _);
            if (install is null || !install.IsValid || !File.Exists(install.MainPakDir))
            {
                return null;
            }

            return Wibe.Core.Items.AllHeroItemsScanner.Scan(
                install, Wibe.Core.Econ.EconCatalog.Load(install)).Entries.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Достраивает вкладки героя проверенными предметами из пака, которых нет среди кропов.
    /// Родные карточки всегда идут первыми, дубли по item_id отбрасываются, отсутствующая
    /// вкладка слота создаётся. Страницы Личностей остаются на своих кропах.
    /// </summary>
    private static List<Section> MergeRegistryExtras(List<Section> sections, string heroKey, string folder, int personaId)
    {
        if (personaId != 0)
        {
            return sections;
        }

        var extras = ExtrasLazy.Value;
        List<Wibe.Core.Items.AllHeroItemsScanner.Entry>? all = null;
        foreach (var alias in Aliases(heroKey).Concat(Aliases(folder)))
        {
            if (extras.TryGetValue(alias, out all) && all.Count > 0)
            {
                break;
            }

            all = null;
        }

        if (all is null)
        {
            return sections;
        }

        var have = new HashSet<int>(sections.SelectMany(s => s.Cards).Select(c => c.Id));
        var merged = sections.ToList();
        foreach (var group in all.GroupBy(e => e.Section, StringComparer.OrdinalIgnoreCase))
        {
            var cards = new List<Card>();
            foreach (var entry in group)
            {
                if (!have.Add(entry.ItemId))
                {
                    continue;
                }

                var icon = IconUrl(entry.Icon);
                cards.Add(new Card(
                    entry.ItemId,
                    entry.Name,
                    entry.Slot,
                    entry.Rarity,
                    icon,
                    Prefab: "wearable",
                    ImgUrl: icon));
            }

            if (cards.Count == 0)
            {
                continue;
            }

            var at = merged.FindIndex(s => string.Equals(s.Name, group.Key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                merged[at] = merged[at] with { Cards = merged[at].Cards.Concat(cards).ToList() };
            }
            else
            {
                merged.Add(new Section(group.Key, cards));
            }
        }

        return merged;
    }

    /// <summary>Ссылка на отдачу картинки из пака по требованию.</summary>
    private static string IconUrl(string iconPath)
    {
        var inv = iconPath
            .Replace("panorama/images/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_png.vtex_c", string.Empty, StringComparison.OrdinalIgnoreCase);
        return "/api/vpk-icon?inv=" + Uri.EscapeDataString(inv);
    }

    private static View? ReadFromSeeds(string heroKey, string? requestedFolder)
    {
        var collection = CollectionLazy.Value.Values.SelectMany(v => v).ToList();
        var requested = (requestedFolder ?? string.Empty).Trim();
        var entry = collection.FirstOrDefault(e =>
                        string.Equals(e.Name, requested, StringComparison.OrdinalIgnoreCase)) ??
                    collection.FirstOrDefault(e =>
                        string.Equals(e.Slug, heroKey, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Hero, heroKey, StringComparison.OrdinalIgnoreCase));
        var folder = entry?.Name ?? (requested.Length > 0 ? requested : heroKey);
        if (!SeedsLazy.Value.TryGetValue(folder, out var source) || source.Count == 0)
        {
            return null;
        }

        var sections = source
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new Section(s.Name!, Clean(s)))
            .Where(s => s.Cards.Count > 0)
            .ToList();
        if (sections.Count == 0)
        {
            return null;
        }

        var personaId = entry?.PersonaId ?? 0;
        return new View(folder, heroKey, entry?.Name ?? folder, personaId != 0, personaId, sections);
    }

    public static string? ResolveFolder(string heroKey, string? requestedFolder)
    {
        var root = Root;
        return root is null ? null : ResolveFolder(root, heroKey, requestedFolder);
    }

    private static string? ResolveFolder(string root, string heroKey, string? requestedFolder)
    {
        var folders = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedFolder))
        {
            var exact = folders.FirstOrDefault(n => string.Equals(n, requestedFolder, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        var entries = CollectionLazy.Value.Values.SelectMany(v => v).ToList();
        var baseHero = entries.FirstOrDefault(e =>
                string.Equals(e.Slug, heroKey, StringComparison.OrdinalIgnoreCase) && e.PersonaId == 0)
            ?? entries.FirstOrDefault(e =>
                string.Equals(e.Hero, heroKey, StringComparison.OrdinalIgnoreCase) && e.PersonaId == 0);

        var wanted = requestedFolder ?? baseHero?.Name;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var exact = folders.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return folders.FirstOrDefault(n => string.Equals(Slug(n), heroKey, StringComparison.OrdinalIgnoreCase));
    }

    private static List<Card> Clean(SeedSection section)
    {
        var cards = new List<Card>();
        foreach (var card in section.Cards ?? new List<SeedCard>())
        {
            if (card.Id <= 0 || string.IsNullOrWhiteSpace(card.Name))
            {
                continue;
            }

            var name = card.Name.Trim();
            var title = string.IsNullOrWhiteSpace(card.Title) ? name : card.Title.Trim();
            if (LooksGrey(name) || LooksGrey(title))
            {
                continue;
            }

            cards.Add(new Card(
                card.Id,
                name,
                string.IsNullOrWhiteSpace(card.Slot) ? "misc" : card.Slot,
                card.Rarity ?? string.Empty,
                string.IsNullOrWhiteSpace(card.Art) ? $"/art/items/{card.Id}.jpg" : card.Art,
                card.Ability ?? string.Empty,
                name,
                card.Prefab ?? string.Empty,
                card.Styles > 0 ? card.Styles : 1));
        }

        return cards;
    }

    private static bool LooksGrey(string text)
    {
        if (text.Contains("EQUIPPED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.Any(c => c is '₸' or '$' or '¥' or '€');
    }

    private static string Slug(string value) =>
        new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

    private static string? FindRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("WIBE_HEROES_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var at = new DirectoryInfo(start);
            for (var up = 0; at is not null && up < 7; up++, at = at.Parent)
            {
                var candidate = Path.Combine(at.FullName, "Heroes");
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    if (Directory.EnumerateDirectories(candidate).Take(100).Count() >= 100)
                    {
                        return candidate;
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        return null;
    }

    private static Dictionary<string, List<CollectionEntry>> LoadCollection()
    {
        var list = ReadJson<List<CollectionEntry>>("collection.json") ?? new List<CollectionEntry>();
        return list
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .GroupBy(e => e.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<SeedSection>> LoadSeeds()
    {
        var raw = ReadJson<Dictionary<string, Dictionary<string, SeedSection>>>("hero_sections.json")
            ?? new Dictionary<string, Dictionary<string, SeedSection>>();
        var result = new Dictionary<string, List<SeedSection>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hero, sections) in raw)
        {
            result[hero] = sections
                .Select(kv => new SeedSection { Name = kv.Key, Cards = kv.Value.Cards })
                .Where(s => s.Cards is { Count: > 0 })
                .ToList();
        }

        return result;
    }

    private static Dictionary<string, List<SeedCard>> LoadDefaults()
    {
        var raw = ReadJson<Dictionary<string, List<SeedCard>>>("hero_default_cards.json")
            ?? new Dictionary<string, List<SeedCard>>();
        return new Dictionary<string, List<SeedCard>>(raw, StringComparer.OrdinalIgnoreCase);
    }

    private static T? ReadJson<T>(string name)
    {
        try
        {
            string? text = null;
            var file = Path.Combine(AppContext.BaseDirectory, "www", name);
            if (File.Exists(file))
            {
                text = File.ReadAllText(file);
            }
            else
            {
                using var stream = typeof(HeroFolderCatalog).Assembly.GetManifestResourceStream("www." + name);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    text = reader.ReadToEnd();
                }
            }

            return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Reads);
        }
        catch (Exception)
        {
            return default;
        }
    }
}
