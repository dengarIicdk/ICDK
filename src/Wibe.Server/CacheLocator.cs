using Wibe.Core.Cache;

namespace Wibe.Server;

/// <summary>
/// Готовый локальный кэш: папка с `.glb` героев и косметики.
/// Путь берётся из настроек (`cachePath`), переменной окружения или стандартной папки ICDK.
/// Кэш только читается — ничего чужого не меняем и не удаляем.
/// </summary>
public static class CacheLocator
{
    private static readonly object Gate = new();
    private static string? _root;
    private static Dictionary<string, string>? _items;
    private static Dictionary<string, string>? _heroes;

    public static string Root
    {
        get
        {
            lock (Gate)
            {
                return _root ??= Resolve();
            }
        }
    }

    /// <summary>Пересобрать индекс после смены папки кэша в настройках.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _root = null;
            _items = null;
            _heroes = null;
        }
    }

    // Единственная точка правды: WIBE_CACHE → settings.json → X:\ICDK_AssetVault на дисках.
    internal static string Resolve() => AssetVaultLocator.Resolve();

    /// <summary>Готовый glb героя: файл, в имени которого есть ключ героя.</summary>
    public static string? TryFindHero(string folder)
    {
        var index = HeroIndex();
        return index.TryGetValue(Key(folder), out var path) ? path : null;
    }

    /// <summary>Готовый glb предмета по его локализованному имени.</summary>
    public static string? TryFindItem(string name)
    {
        var index = ItemIndex();
        return index.TryGetValue(Key(name), out var path) ? path : null;
    }

    /// <summary>Сколько готовых моделей и иконок лежит в папке кэша.</summary>
    public static (int Models, int Icons, long Bytes) Describe()
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            return (0, 0, 0);
        }

        var models = 0;
        var icons = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // файл занят — вес просто не учитываем
            }

            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".glb")
            {
                models++;
            }
            else if (ext == ".png" || ext == ".webp")
            {
                icons++;
            }
        }

        return (models, icons, bytes);
    }

    private static Dictionary<string, string> HeroIndex()
    {
        if (_heroes is not null)
        {
            return _heroes;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Enumerate(root => root))
        {
            var name = Path.GetFileName(file);
            // hero-antimage.glb | antimage.glb | hero_antimage.glb
            var key = Key(Path.GetFileNameWithoutExtension(name).Replace("hero-", string.Empty, StringComparison.OrdinalIgnoreCase));
            if (!map.ContainsKey(key))
            {
                map[key] = file;
            }
        }

        return _heroes = map;
    }

    private static Dictionary<string, string> ItemIndex()
    {
        if (_items is not null)
        {
            return _items;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Enumerate(root => root))
        {
            // 2023panther_shadow_armor__2023panther_shadow_armor.glb
            var stem = Path.GetFileNameWithoutExtension(file);
            var parts = stem.Split("__", StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var key = Key(part);
                if (key.Length > 2 && !map.ContainsKey(key))
                {
                    map[key] = file;
                }
            }
        }

        return _items = map;
    }

    private static IEnumerable<string> Enumerate(Func<string, string> _)
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(root, "*.glb", SearchOption.AllDirectories).Take(60_000).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static string Key(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
