using Wibe.Core.Vdf;

namespace Wibe.Core.Items;

/// <summary>
/// Патч scripts/npc/heroes/npc_dota_hero_*.txt: прямое указание модели героя.
/// Для Личностей и Аркан это гарантирует, что движок заспавнит нужный скелет
/// и анимации, а не взрослую модель под детской/арканной бронёй.
/// </summary>
public static class HeroModelPatcher
{
    public static bool Apply(KeyValuesNode root, string heroKey, string model)
    {
        var heroes = root.Child("DOTAHeroes") ?? root;
        var node = heroes.Children.FirstOrDefault(c => string.Equals(c.Key, heroKey, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            return false;
        }

        var normalized = model.Replace('\\', '/').Trim();
        if (normalized.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^2];
        }

        node.Children.RemoveAll(c => string.Equals(c.Key, "Model", StringComparison.OrdinalIgnoreCase));
        node.Children.Add(new KeyValuesNode("Model", normalized));
        return true;
    }
}
