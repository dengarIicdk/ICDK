namespace Wibe.Core.World;

/// <summary>
/// Единственная подставка WIBE: костяной трон Wraith King из его арканы. Глобального
/// пьедестала под всеми героями нет.
/// </summary>
public static class PedestalCatalog
{
    public const string WraithKingThrone = "models/items/wraith_king/arcana/wk_arc_throne_model.vmdl";

    /// <summary>Трон только для арканы Wraith King; null для всех остальных героев и предметов.</summary>
    public static string? ThroneFor(string hero, int itemId)
    {
        var folder = hero.StartsWith("npc_dota_hero_", StringComparison.Ordinal)
            ? hero["npc_dota_hero_".Length..]
            : hero;
        return folder.Equals("wraith_king", StringComparison.OrdinalIgnoreCase) && itemId == 13456
            ? WraithKingThrone
            : null;
    }
}
