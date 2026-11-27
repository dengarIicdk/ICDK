using Wibe.Core.Vdf;

namespace Wibe.Core.Items;

public sealed record ItemRemap(string HeroKey, string Slot, int ItemId, string ItemName);

public sealed record RemapOutcome(string Slot, int ItemId, bool Applied, string Detail);

/// <summary>
/// Правка items_game.txt: у базового предмета героя (prefab default_item) подменяются
/// визуалы (visuals), модель и иконка на выбранную косметику. Так клиент рисует скин,
/// даже если предмет не надет — серверу и инвентарю это не видно.
///
/// Отдельно поддержаны Арканы и Личности: у них новая модель героя лежит в
/// visuals → asset_modifier с type entity_model, а старые базовые слоты (волосы, броня,
/// оружие) продолжают рендериться и ломают вид. Поэтому visuals переносятся целиком,
/// а model_player у конфликтующих базовых слотов очищается.
/// </summary>
public static class ItemsGamePatcher
{
    public static IReadOnlyList<RemapOutcome> Apply(KeyValuesNode root, IReadOnlyList<ItemRemap> remaps)
    {
        var items = Items(root);
        if (items is null)
        {
            return Array.Empty<RemapOutcome>();
        }

        var byId = new Dictionary<int, KeyValuesNode>();
        foreach (var node in items.Children)
        {
            if (int.TryParse(node.Key, out var id))
            {
                byId[id] = node;
            }
        }

        var outcomes = new List<RemapOutcome>();
        foreach (var group in remaps.GroupBy(r => r.HeroKey, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var remap in group)
            {
                if (!byId.TryGetValue(remap.ItemId, out var chosen))
                {
                    outcomes.Add(new RemapOutcome(remap.Slot, remap.ItemId, false, "предмет не найден в items_game"));
                    continue;
                }

                var baseItem = FindBaseItem(items, remap.HeroKey, remap.Slot);
                if (baseItem is null && IsEntityChosen(chosen))
                {
                    // У части героев (Invoker, Anti-Mage) нет default_item с hero_base —
                    // вешаем visuals Арканы/Личности на любой базовый предмет героя.
                    baseItem = FindAnyBase(items, remap.HeroKey);
                }

                if (baseItem is null)
                {
                    outcomes.Add(new RemapOutcome(remap.Slot, remap.ItemId, false, "у героя нет базового предмета этого слота"));
                    continue;
                }

                var isBody = string.Equals(NormalizeSlot(remap.Slot), "body", StringComparison.OrdinalIgnoreCase);
                var (detail, _, _) = ApplyToBase(baseItem, chosen, isBody);
                outcomes.Add(new RemapOutcome(remap.Slot, remap.ItemId, true, detail));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Личность: взрослые базовые слоты героя больше не должны рисоваться, а родные
    /// персонажные детали (head_persona_1… ) переносятся в основные слоты (head…),
    /// чтобы Invoker Kid / Wei были полностью одеты в свою одежду.
    /// </summary>
    public static int ApplyPersonaParts(KeyValuesNode root, string heroKey, IReadOnlyDictionary<string, string> partModels)
    {
        var items = Items(root);
        if (items is null)
        {
            return 0;
        }

        var nodes = items.Children
            .Where(n => string.Equals(n.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase) && ForHero(n, heroKey))
            .ToList();

        foreach (var node in nodes)
        {
            var slot = EffectiveSlot(node);
            if (slot.Contains("persona", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(slot, "body", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(node.String("model_player")))
            {
                node.Children.RemoveAll(c => c.Key == "model_player");
                node.Children.Add(new KeyValuesNode("model_player", string.Empty));
            }
        }

        var changed = 0;
        foreach (var (mainSlot, rawModel) in partModels)
        {
            var model = ToSourceModel(rawModel);
            if (model.Length == 0)
            {
                continue;
            }

            var baseNode = nodes.FirstOrDefault(n =>
                !EffectiveSlot(n).Contains("persona", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(EffectiveSlot(n), mainSlot, StringComparison.OrdinalIgnoreCase));
            if (baseNode is null)
            {
                continue;
            }

            baseNode.Children.RemoveAll(c => c.Key == "model_player");
            baseNode.Children.Add(new KeyValuesNode("model_player", model));

            var personaNode = nodes.FirstOrDefault(n =>
                string.Equals(EffectiveSlot(n), mainSlot + "_persona_1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(EffectiveSlot(n), mainSlot + "_persona", StringComparison.OrdinalIgnoreCase)) ??
                nodes.FirstOrDefault(n =>
                    EffectiveSlot(n).Contains("persona", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ToSourceModel(n.String("model_player") ?? string.Empty), model, StringComparison.OrdinalIgnoreCase));

            var visuals = personaNode?.Child("visuals");
            if (visuals is not null)
            {
                baseNode.Children.RemoveAll(c => c.Key == "visuals");
                baseNode.Children.Add(Clone(visuals));
            }

            changed++;
        }

        return changed;
    }

    private static string ToSourceModel(string value)
    {
        var path = value.Replace('\\', '/').Trim().ToLowerInvariant();
        return path.EndsWith("_c", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)
            ? path[..^2]
            : path;
    }

    /// <summary>Все модели, на которые ссылается предмет (model_player + visuals → asset_modifier).</summary>
    public static IReadOnlyList<string> CollectModelAssets(KeyValuesNode root, int itemId)
    {
        var items = Items(root);
        var node = items?.Children.FirstOrDefault(n => int.TryParse(n.Key, out var id) && id == itemId);
        if (node is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        AddModel(result, node.String("model_player"));

        var visuals = node.Child("visuals");
        if (visuals is not null)
        {
            foreach (var modifier in visuals.Children)
            {
                AddModel(result, modifier.String("modifier"));
                AddModel(result, modifier.String("asset"));
            }
        }

        return result;
    }

    private static void AddModel(List<string> list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var path = value.Replace('\\', '/').Trim().ToLowerInvariant();
        if (!path.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(path);
        }
    }

    private static (string Detail, bool Entity, bool Persona) ApplyToBase(KeyValuesNode baseItem, KeyValuesNode chosen, bool body = false)
    {
        var parts = new List<string>();

        var visuals = chosen.Child("visuals");
        var chosenModel = chosen.String("model_player");

        var entityModel = FindEntityModel(visuals) ?? string.Empty;
        var entity = entityModel.Length > 0;

        var persona = visuals?.Children.Any(c =>
            string.Equals(c.String("type"), "persona", StringComparison.OrdinalIgnoreCase)) ?? false;

        if (visuals is not null)
        {
            // Переносим visuals целиком: entity_model, persona, particle, particle_create,
            // ability_icon, sound, activity, skin, hero_model_change — всё, что нужно Аркане.
            baseItem.Children.RemoveAll(c => c.Key == "visuals");
            baseItem.Children.Add(Clone(visuals));
            parts.Add(entity ? "visuals (entity_model)" : "visuals");
        }

        // Тело всегда должно ссылаться на entity_model напрямую: это обходит случай,
        // когда движок берёт стоковую модель слота из game/dota вместо языкового пака.
        var model = entity && (body || string.IsNullOrWhiteSpace(chosenModel))
            ? entityModel
            : chosenModel;

        if (!string.IsNullOrWhiteSpace(model))
        {
            baseItem.Children.RemoveAll(c => c.Key == "model_player");
            baseItem.Children.Add(new KeyValuesNode("model_player", model));
            parts.Add("model_player");
        }

        var image = chosen.String("image_inventory");
        if (!string.IsNullOrEmpty(image))
        {
            baseItem.Children.RemoveAll(c => c.Key is "image_inventory" or "display_name");
            baseItem.Children.Add(new KeyValuesNode("image_inventory", image));
            parts.Add("image_inventory");
        }

        return (parts.Count == 0 ? "нечего менять" : string.Join(", ", parts), entity, persona);
    }

    private static string? FindEntityModel(KeyValuesNode? node)
    {
        if (node is null)
        {
            return null;
        }

        foreach (var child in node.Children)
        {
            if (string.Equals(child.String("type"), "entity_model", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(child.String("modifier")))
            {
                return child.String("modifier");
            }

            var nested = FindEntityModel(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool IsEntityChosen(KeyValuesNode chosen)
    {
        var visuals = chosen.Child("visuals");
        return visuals is not null && visuals.Children.Any(c =>
            string.Equals(c.String("type"), "entity_model", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(c.String("modifier")));
    }

    private static KeyValuesNode? Items(KeyValuesNode root)
    {
        var game = root.Child("items_game") ?? root;
        return game.Child("items");
    }

    /// <summary>
    /// Базовый предмет героя: prefab default_item, тот же слот. Для Аркан/Личностей
    /// hero_base и persona_selector считаются слотом «body».
    /// </summary>
    private static KeyValuesNode? FindBaseItem(KeyValuesNode items, string heroKey, string slot)
    {
        var wantSlot = NormalizeSlot(slot);
        var fallback = (KeyValuesNode?)null;

        foreach (var node in items.Children)
        {
            if (!string.Equals(node.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase) ||
                !ForHero(node, heroKey))
            {
                continue;
            }

            var effective = EffectiveSlot(node);
            if (string.Equals(effective, wantSlot, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (effective == "misc")
            {
                fallback ??= node;
            }
        }

        return fallback ?? (wantSlot == "misc" ? FindAnyBase(items, heroKey) : null);
    }

    private static KeyValuesNode? FindAnyBase(KeyValuesNode items, string heroKey) =>
        items.Children.FirstOrDefault(n =>
            string.Equals(n.String("prefab"), "default_item", StringComparison.OrdinalIgnoreCase) &&
            ForHero(n, heroKey));

    private static bool ForHero(KeyValuesNode node, string heroKey)
    {
        var heroes = node.Child("used_by_heroes");
        return heroes is null ||
               heroes.Children.Any(c => string.Equals(c.Key, heroKey, StringComparison.OrdinalIgnoreCase) && c.Value != "0");
    }

    private static string EffectiveSlot(KeyValuesNode node)
    {
        var declared = NormalizeSlot(node.String("item_slot") ?? string.Empty);
        if (declared is "hero_base" or "persona_selector" or "persona")
        {
            return "body";
        }

        return declared.Length > 0
            ? declared
            : InferSlot(node.String("model_player") ?? string.Empty);
    }

    private static string NormalizeSlot(string slot)
    {
        var value = slot.Trim().ToLowerInvariant();
        return value switch
        {
            "" => "misc",
            "shoulders" => "shoulder",
            "offhand_weapon_1" => "offhand_weapon",
            _ => value,
        };
    }

    private static string InferSlot(string model)
    {
        var file = Path.GetFileNameWithoutExtension(model).ToLowerInvariant();
        foreach (var slot in new[] { "offhand_weapon", "weapon", "armor", "head", "belt", "back", "tail", "mount", "legs", "arms", "shoulder", "neck", "gloves", "hair" })
        {
            if (file.Contains(slot, StringComparison.Ordinal))
            {
                return slot;
            }
        }

        return "misc";
    }

    private static KeyValuesNode Clone(KeyValuesNode node)
    {
        var copy = new KeyValuesNode(node.Key, node.Value);
        foreach (var child in node.Children)
        {
            copy.Children.Add(Clone(child));
        }

        return copy;
    }
}
