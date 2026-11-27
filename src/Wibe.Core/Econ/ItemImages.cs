namespace Wibe.Core.Econ;

/// <summary>
/// Готовые картинки с официальных CDN Valve. Никакой конвертации .vtex_c:
/// портреты героев и иконки предметов берутся из dota_react / steamcdn.
/// </summary>
public static class ItemImages
{
    private const string React = "https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react";
    private const string Legacy = "https://steamcdn-a.akamaihd.net/apps/570/icons";
    private const string Economy = "https://cdn.cloudflare.steamstatic.com/economy/image";

    public static string HeroPortrait(string folder) => $"{React}/heroes/{folder}.png";

    public static string HeroCrop(string folder) => $"{React}/heroes/crops/{folder}.png";

    /// <summary>
    /// image_inventory вида <c>econ/items/axe/ti9_immortal_head/axe_ti9_immortal_head</c>
    /// → <c>axe/ti9_immortal_head/axe_ti9_immortal_head</c>.
    /// </summary>
    public static string? RelativeFromInventory(string? inventory)
    {
        if (string.IsNullOrWhiteSpace(inventory))
        {
            return null;
        }

        var path = inventory.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        if (path.StartsWith("econ/items/", StringComparison.Ordinal))
        {
            return path["econ/items/".Length..];
        }

        if (path.StartsWith("econ/", StringComparison.Ordinal))
        {
            return path["econ/".Length..];
        }

        return path;
    }

    /// <summary>Кандидаты URL в порядке приоритета: dota_react, затем старый steamcdn.</summary>
    public static IReadOnlyList<string> ItemUrls(string? inventory)
    {
        var relative = RelativeFromInventory(inventory);
        if (relative is null)
        {
            return Array.Empty<string>();
        }

        var normalized = inventory!.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        var leaf = normalized.Split('/')[^1];

        // Известные шаблоны Valve: dota_react, steamcdn icons, source1-иконки.
        // Четвёртый источник (торговая площадка) подключается в ImageProxy по имени предмета.
        return new[]
        {
            $"{React}/items/{relative}.png",
            $"{Legacy}/{normalized}.png",
            $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/items/{leaf}_lg.png",
            $"{Legacy}/econ/items/{leaf}_lg.png",
        };
    }
}
