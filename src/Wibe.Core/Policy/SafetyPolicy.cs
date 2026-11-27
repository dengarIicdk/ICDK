namespace Wibe.Core.Policy;

public enum SafetyVerdict
{
    Allowed,
    Warn,     // категория B: ставим, но с предупреждением
    Blocked,  // категория C: не ставим никогда
}

/// <summary>
/// Жёсткие правила: что нельзя класть в VPK-мод.
/// Это не «рекомендации в тексте», а фильтр, через который проходит любой импорт.
/// </summary>
public static class SafetyPolicy
{
    private static readonly string[] HardBlockedPrefixes =
    {
        "scripts/",              // items_game.txt, npc_*.txt, любой геймплей
        "panorama/layout/",      // HUD: подменённая раскладка отбирает ПКМ и хоткеи
        "panorama/scripts/",     // JS панорамы: то же, плюс перехват кликов
        "panorama/styles/",      // CSS панорамы
        "cfg/",                  // конфиги игрока (бинды, камера)
        "events/",               // события игры
        "gameinfo",              // gameinfo.gi / gameinfo_branchspecific.gi
        "bin/",                  // dll/exe
        "dota.signatures",
        "system.signatures",
        "resourcecompiler/",
    };

    private static readonly string[] HardBlockedExtensions =
    {
        ".dll", ".exe", ".so", ".vdf", ".gi", ".signatures", ".acf", ".sys", ".bat", ".ps1",
    };

    /// <summary>Категория C: меняет видимость/читаемость боя (жёсткий блок).</summary>
    private static readonly string[] CategoryCSubstrings =
    {
        "maps/dota/worldnodes/",  // мир карты (деревья/препятствия)
        "/fog",
        "/river",
        "/water",
        "/grass",
        "/foliage",
    };

    /// <summary>Категория B: видно в бою, ставим с предупреждением.</summary>
    private static readonly string[] CategoryBSubstrings =
    {
        "models/props_nature/",   // камни/кусты/костёр — косметика без преимущества
        "maps/reef_assets/",
        "maps/summer_assets/",
        "maps/cavern_assets/",
        "maps/jungle_assets/",
        "maps/journey_assets/",
        "maps/ti10_assets/",
        "maps/crownfall_assets/",
        "maps/aghs21_assets/",
        "models/props_structures/",
        "models/creeps/",
        "particles/",
    };

    /// <summary>Служебные модели, которые нельзя подменять (иначе ломаются эффекты).</summary>
    private static readonly string[] DoNotTouchSubstrings =
    {
        "_destruction_lev",
        "_glow.vmdl",
        "_energy_effect",
        "invisiblebox",
    };

    public static SafetyVerdict Evaluate(string vpkPath)
    {
        var path = vpkPath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

        foreach (var prefix in HardBlockedPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return SafetyVerdict.Blocked;
            }
        }

        var ext = System.IO.Path.GetExtension(path);
        if (HardBlockedExtensions.Contains(ext, StringComparer.Ordinal))
        {
            return SafetyVerdict.Blocked;
        }

        // Модель дерева (а не текстура) — это уже «прозрачные деревья».
        if (path.Contains("models/props_tree/", StringComparison.Ordinal) &&
            (path.EndsWith(".vmdl", StringComparison.Ordinal) || path.EndsWith(".vmdl_c", StringComparison.Ordinal)))
        {
            return SafetyVerdict.Blocked;
        }

        // Текстуры/материалы деревьев — перекраска, категория B.
        if (path.Contains("models/props_tree/", StringComparison.Ordinal))
        {
            return SafetyVerdict.Warn;
        }

        foreach (var fragment in CategoryCSubstrings)
        {
            if (path.Contains(fragment, StringComparison.Ordinal))
            {
                return SafetyVerdict.Blocked;
            }
        }

        foreach (var fragment in DoNotTouchSubstrings)
        {
            if (path.Contains(fragment, StringComparison.Ordinal))
            {
                return SafetyVerdict.Blocked;
            }
        }

        foreach (var fragment in CategoryBSubstrings)
        {
            if (path.Contains(fragment, StringComparison.Ordinal))
            {
                return SafetyVerdict.Warn;
            }
        }

        return SafetyVerdict.Allowed;
    }

    public static bool IsBlocked(string vpkPath) => Evaluate(vpkPath) == SafetyVerdict.Blocked;

    public static string Explain(string vpkPath)
    {
        var path = vpkPath.Replace('\\', '/').ToLowerInvariant();
        foreach (var prefix in HardBlockedPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return $"путь из запрещённой зоны '{prefix}' — влияет на игру, а не на вид";
            }
        }

        var ext = System.IO.Path.GetExtension(path);
        if (HardBlockedExtensions.Contains(ext, StringComparer.Ordinal))
        {
            return $"исполняемый/подписанный файл '{ext}' — это уже не косметика";
        }

        // Модель дерева (а не текстура) — это уже «прозрачные деревья».
        if (path.Contains("models/props_tree/", StringComparison.Ordinal) &&
            (path.EndsWith(".vmdl", StringComparison.Ordinal) || path.EndsWith(".vmdl_c", StringComparison.Ordinal)))
        {
            return "модель дерева — «прозрачные деревья» это чит";
        }

        if (path.Contains("models/props_tree/", StringComparison.Ordinal))
        {
            return "текстура дерева — категория B (перекраска без преимущества)";
        }

        foreach (var fragment in CategoryCSubstrings)
        {
            if (path.Contains(fragment, StringComparison.Ordinal))
            {
                return "меняет видимость (деревья/туман/вода) — прямое преимущество в игре";
            }
        }

        foreach (var fragment in CategoryBSubstrings)
        {
            if (path.Contains(fragment, StringComparison.Ordinal))
            {
                return "категория B: видно в бою (строения/крипы/террейн/частицы)";
            }
        }

        foreach (var fragment in DoNotTouchSubstrings)
        {
            if (path.Contains(fragment, StringComparison.Ordinal))
            {
                return "служебная модель (эффекты/разрушение) — сломает визуал";
            }
        }

        return "разрешено";
    }
}
