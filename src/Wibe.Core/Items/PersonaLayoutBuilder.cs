using Wibe.Core.Vdf;

namespace Wibe.Core.Items;

/// <summary>Один слот Личности: её собственный слот, стоковый слот-хозяин и родной предмет.</summary>
public sealed record PersonaPart(
    string Slot,
    string Part,
    string HostId,
    string HostModel,
    string? GivenId,
    string? GivenModel,
    bool Modelled);

/// <summary>Раскладка Личности по данным items_game, повторяющая persona_layouts из mm-core.</summary>
public sealed record PersonaLayout(
    string Hero,
    int Number,
    string SelectorId,
    IReadOnlyList<PersonaPart> Parts,
    IReadOnlyList<string> FreeModels);

/// <summary>
/// Точный порт crates/mm-core/src/item_builder/item_builder_slots.rs::layout_of:
/// собственные слоты персоны сортируются по HOST_ORDER, свободные стоковые слоты — по тому же
/// порядку и номеру предмета, затем четыре прохода подбора хозяина (точный слот, алиас,
/// модель есть у персоны и у хозяина, модель есть у персоны). Никаких догадок по имени героя.
/// </summary>
public static class PersonaLayoutBuilder
{
    private const string Selector = "persona_selector";

    private static readonly HashSet<string> NotHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        Selector, "summon", "voice", "hero_base", "costume",
    };

    private static readonly string[] HostOrder =
    {
        "head", "body_head", "hair", "neck", "shoulder", "arms", "back", "weapon",
        "offhand_weapon", "armor", "belt", "legs", "mount", "misc",
    };

    public static PersonaLayout? Build(KeyValuesNode root, string heroKey, int selectedItemId)
    {
        var itemsRoot = (root.Child("items_game") ?? root).Child("items");
        if (itemsRoot is null)
        {
            return null;
        }

        var hero = NormalizeHero(heroKey);
        var heroItems = itemsRoot.Children
            .Where(n => int.TryParse(n.Key, out _) && ForHero(n, hero))
            .ToList();
        var selected = heroItems.FirstOrDefault(n => int.TryParse(n.Key, out var id) && id == selectedItemId);
        if (selected is null)
        {
            return null;
        }

        var number = PersonaNumber(selected);
        if (number <= 0)
        {
            return null;
        }

        var selector = heroItems.FirstOrDefault(n =>
            string.Equals(n.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase) &&
            SlotOf(n) == Selector);
        if (selector is null)
        {
            return null;
        }

        var suffix = "_persona_" + number;
        var own = new Dictionary<string, Own>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in heroItems)
        {
            var slot = SlotOf(item);
            if (!slot.EndsWith(suffix, StringComparison.Ordinal) || slot.Length == suffix.Length)
            {
                continue;
            }

            var part = slot[..^suffix.Length];
            if (part == "voice")
            {
                continue;
            }

            if (!own.TryGetValue(slot, out var entry))
            {
                entry = new Own();
                own[slot] = entry;
            }

            if (string.Equals(item.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase))
            {
                entry.Given ??= item;
            }
            else if (!string.IsNullOrWhiteSpace(item.String("name")))
            {
                entry.Options.Add(item);
            }
        }

        var free = heroItems
            .Where(n =>
            {
                var slot = SlotOf(n);
                return string.Equals(n.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase) &&
                       !HasPersonaSuffix(slot) && !NotHosts.Contains(slot);
            })
            .OrderBy(n => Rank(SlotOf(n)))
            .ThenBy(n => ItemNumber(n.Key))
            .ToList();

        var slots = own.Keys
            .OrderBy(s => Rank(PartOf(s)))
            .ToList();
        var hosts = new Dictionary<string, KeyValuesNode>(StringComparer.OrdinalIgnoreCase);
        var summon = heroItems.FirstOrDefault(n =>
            string.Equals(n.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase) &&
            SlotOf(n) == "summon");
        foreach (var slot in slots)
        {
            if (PartOf(slot) == "summon" && summon is not null)
            {
                hosts[slot] = summon;
            }
        }

        for (var pass = 0; pass < 4; pass++)
        {
            foreach (var slot in slots)
            {
                var part = PartOf(slot);
                if (hosts.ContainsKey(slot) || part == "summon")
                {
                    continue;
                }

                var ownPart = own[slot];
                bool Fits(KeyValuesNode host) => pass switch
                {
                    0 => SlotOf(host) == part,
                    1 => MatchSlot(SlotOf(host)) == MatchSlot(part),
                    2 => Modelled(ownPart) && !string.IsNullOrWhiteSpace(host.String("model_player")),
                    _ => Modelled(ownPart),
                };

                var at = free.FindIndex(Fits);
                if (at < 0)
                {
                    continue;
                }

                hosts[slot] = free[at];
                free.RemoveAt(at);
            }
        }

        var parts = new List<PersonaPart>();
        foreach (var slot in slots)
        {
            if (!hosts.TryGetValue(slot, out var host))
            {
                continue;
            }

            var model = host.String("model_player") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            var ownPart = own[slot];
            parts.Add(new PersonaPart(
                slot,
                PartOf(slot),
                host.Key,
                model,
                ownPart.Given?.Key,
                ownPart.Given?.String("model_player"),
                Modelled(ownPart)));
        }

        return new PersonaLayout(hero, number, selector.Key, parts,
            free.Select(n => n.String("model_player") ?? string.Empty).Where(m => m.Length > 0).ToList());
    }

    public static int PersonaNumber(KeyValuesNode? item)
    {
        var visuals = item?.Child("visuals");
        if (visuals is null)
        {
            return 0;
        }

        foreach (var modifier in Modifiers(visuals))
        {
            if (!string.Equals((modifier.String("type") ?? string.Empty).Trim(), "persona", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse((modifier.String("persona") ?? string.Empty).Trim(), out var number) && number > 0)
            {
                return number;
            }
        }

        return 0;
    }

    public static string SlotOf(KeyValuesNode node) =>
        (node.String("item_slot") ?? string.Empty).Trim().ToLowerInvariant();

    public static string MatchSlot(string slot) => slot switch
    {
        "offhand_weapon" or "offhand" => "offhand",
        "shoulder" or "shoulders" => "shoulders",
        "arm" or "arms" => "arms",
        _ => slot,
    };

    private static string PartOf(string slot)
    {
        var at = slot.LastIndexOf("_persona_", StringComparison.Ordinal);
        return at > 0 ? slot[..at] : slot;
    }

    private static bool HasPersonaSuffix(string slot)
    {
        var at = slot.LastIndexOf("_persona_", StringComparison.Ordinal);
        return at > 0 && slot[(at + "_persona_".Length)..].All(char.IsAsciiDigit);
    }

    private static bool Modelled(Own own) =>
        (own.Given is not null && !string.IsNullOrWhiteSpace(own.Given.String("model_player"))) ||
        own.Options.Any(o => !string.IsNullOrWhiteSpace(o.String("model_player")));

    private static int Rank(string slot) => Array.IndexOf(HostOrder, slot) is var i && i >= 0 ? i : HostOrder.Length;

    private static double ItemNumber(string key) =>
        double.TryParse(key, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : double.NaN;

    private static bool ForHero(KeyValuesNode node, string hero)
    {
        var heroes = node.Child("used_by_heroes");
        if (heroes is null)
        {
            return true;
        }

        return heroes.Children.Any(c =>
        {
            var key = c.Key.ToLowerInvariant();
            const string prefix = "npc_dota_hero_";
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                key = key[prefix.Length..];
            }

            return string.Equals(key, hero, StringComparison.OrdinalIgnoreCase) && c.Value != "0";
        });
    }

    private static IEnumerable<KeyValuesNode> Modifiers(KeyValuesNode visuals)
    {
        foreach (var child in visuals.Children)
        {
            if (child.Key.StartsWith("asset_modifier", StringComparison.OrdinalIgnoreCase) && child.IsBlock)
            {
                yield return child;
            }

            if (child.IsBlock)
            {
                foreach (var nested in Modifiers(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string NormalizeHero(string hero)
    {
        var value = hero.Trim().ToLowerInvariant();
        const string prefix = "npc_dota_hero_";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }

    private sealed class Own
    {
        public KeyValuesNode? Given { get; set; }
        public List<KeyValuesNode> Options { get; } = new();
    }
}
