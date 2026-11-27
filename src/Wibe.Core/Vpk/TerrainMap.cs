namespace Wibe.Core.Vpk;

/// <summary>
/// Ландшафт поверх настоящей карты.
///
/// Пак ландшафта — это не карта: в нём нет ни сетки навигации (maps/dota.gnv), ни блока
/// сущностей (maps/dota.vhcg), ни компилированной карты (maps/dota.vmap_c), а есть только
/// внешний вид мира — узлы (maps/dota/worldnodes/*), расстановка (maps/dota/entities/*),
/// рендер мира (maps/dota/world.vrman_c) и его описание (maps/dota/world.vwrld_c). Поэтому
/// ландшафт ставится не вместо карты, а на неё: карта остаётся стоковой и проходимой, а
/// файлы мира берутся из пака ландшафта. Так работает и сама игра, у которой все ландшафты
/// лежат теми же путями внутри maps/dota/.
/// </summary>
public static class TerrainMap
{
    /// <summary>Пути карты, которые нельзя брать из ландшафта: по ним игра ходит и стреляет.</summary>
    private static readonly string[] Keep =
    {
        "maps/dota.vmap_c",
        "maps/dota.gnv",
        "maps/dota.vhcg",
        "maps/dota.trm",
    };

    /// <summary>Есть ли в паке мир ландшафта, который можно положить на карту игры.</summary>
    public static bool HasTerrainWorld(string pak)
    {
        var paths = VpkIndex.Paths(pak);
        return paths.Contains("maps/dota/world.vwrld_c")
               && paths.Contains("maps/dota/world.vrman_c")
               && paths.Any(p => p.StartsWith("maps/dota/worldnodes/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Мир ландшафта поверх стоковой карты, записанный в outPath. Сколько файлов вышло.</summary>
    public static int Build(string stockMap, string terrainPack, string outPath, out int overlaid, out int added)
    {
        var map = VpkReader.ReadAll(stockMap);
        var terrain = VpkReader.ReadAll(terrainPack);
        overlaid = 0;
        added = 0;
        foreach (var (path, data) in terrain)
        {
            if (Keep.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!path.StartsWith("maps/dota/", StringComparison.OrdinalIgnoreCase))
            {
                continue;                        // чужие пути в карту не тащим
            }

            if (map.ContainsKey(path))
            {
                overlaid++;
            }
            else
            {
                added++;
            }

            map[path] = data;
        }

        VpkWriter.Write(outPath, map);
        return map.Count;
    }
}
