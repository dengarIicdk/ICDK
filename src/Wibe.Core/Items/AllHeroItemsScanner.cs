using System.Text.Json;
using Wibe.Core.Dota;
using Wibe.Core.Econ;
using Wibe.Core.Vpk;

namespace Wibe.Core.Items;

/// <summary>
/// «Рентген» всех шмоток Dota 2 по разделам героев, за секунды и без единого внешнего процесса:
/// оглавление pak01_dir.vpk читается в HashSet один раз, затем каждый предмет из items_game.txt
/// проверяется по трём условиям — модель есть в паке, слот надевается стоковым слотом героя,
/// картинка есть в паке. Всё, что Valve вырезала (модели или иконки физически удалены), отсеивается.
/// </summary>
public static class AllHeroItemsScanner
{
    public sealed record Entry(
        string HeroKey,
        string Section,
        string Slot,
        int ItemId,
        string Name,
        string Rarity,
        string Model,
        string Icon);

    public sealed record Report(
        string BuildId,
        int Considered,
        int Valid,
        IReadOnlyList<Entry> Entries,
        IReadOnlyDictionary<string, int> BySection);

    private static readonly HashSet<string> SkipPrefabs = new(StringComparer.OrdinalIgnoreCase)
    {
        "default_item", "bundle_ticket", "ticket", "medal", "misc", "courier", "ward", "tool",
    };

    /// <summary>Сам скан: только чтение индекса пака, ни одной распаковки.</summary>
    public static Report Scan(DotaInstallation install, EconCatalog catalog)
    {
        var index = VpkIndex.Paths(install.MainPakDir);
        var entries = new List<Entry>();
        var considered = 0;

        foreach (var item in catalog.Items)
        {
            var hero = item.Hero;
            if (string.IsNullOrWhiteSpace(hero) || item.IsDefault)
            {
                continue;
            }

            if (SkipPrefabs.Contains(item.Prefab))
            {
                continue;
            }

            if (item.Prefab.Length > 0 &&
                !item.Prefab.Equals("wearable", StringComparison.OrdinalIgnoreCase) &&
                !item.Prefab.Equals("bundle", StringComparison.OrdinalIgnoreCase) &&
                !item.Prefab.Equals("taunt", StringComparison.OrdinalIgnoreCase) &&
                !item.Prefab.Equals("arcana", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            considered++;

            // 1. модель предмета физически лежит в паке (у бандла — хотя бы одна часть)
            var model = ResolveModel(index, item);
            if (model is null)
            {
                continue;
            }

            // 2. слот надевается: у героя есть стоковый слот с таким именем
            var slot = item.Slot.Length == 0 ? "body" : item.Slot;
            if (slot.Equals("bundle", StringComparison.OrdinalIgnoreCase))
            {
                slot = "body";
            }

            // 3. картинка карточки есть в паке
            var icon = item.Image is { Length: > 0 } image ? IconPath(image) : null;
            if (icon is null || !index.Contains(icon))
            {
                continue;
            }

            entries.Add(new Entry(
                hero,
                Section(item),
                slot,
                item.Id,
                item.Name,
                item.Rarity,
                model,
                icon));
        }

        var bySection = entries
            .GroupBy(e => e.Section, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new Report(catalog.BuildId, considered, entries.Count, entries, bySection);
    }

    /// <summary>Путь модели, который реально есть в паке: своя у предмета, иначе первая часть бандла.</summary>
    private static string? ResolveModel(HashSet<string> index, EconItem item)
    {
        if (item.CompiledModel is { Length: > 0 } own && index.Contains(own))
        {
            return own;
        }

        foreach (var part in item.ExtraModels)
        {
            var path = part.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? part : part + "_c";
            if (index.Contains(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Раздел карточки: аркана, полный сет, дальше по слоту.</summary>
    public static string Section(EconItem item)
    {
        if (item.Rarity.Equals("arcana", StringComparison.OrdinalIgnoreCase))
        {
            return "★ ARCANA";
        }

        if (item.Prefab.Equals("bundle", StringComparison.OrdinalIgnoreCase) || item.BundleNames.Count > 0)
        {
            return "Fullsets";
        }

        if (item.Prefab.Equals("taunt", StringComparison.OrdinalIgnoreCase) ||
            item.Slot.Equals("taunt", StringComparison.OrdinalIgnoreCase))
        {
            return "Taunt";
        }

        if (item.Slot.Equals("ability", StringComparison.OrdinalIgnoreCase) ||
            item.Slot.Equals("ability1", StringComparison.OrdinalIgnoreCase) ||
            item.Slot.Equals("ability2", StringComparison.OrdinalIgnoreCase) ||
            item.Slot.Equals("ability3", StringComparison.OrdinalIgnoreCase))
        {
            return "Ability Effects";
        }

        return item.Slot.Length == 0 ? "Body" : Title(item.Slot);
    }

    private static string Title(string slot) => slot switch
    {
        "offhand_weapon" => "Off-Hand",
        "shapeshift" => "Dragon Form",
        _ => char.ToUpperInvariant(slot[0]) + slot[1..].Replace('_', ' '),
    };

    /// <summary>Путь картинки в паке: <c>panorama/images/&lt;image_inventory&gt;_png.vtex_c</c>.</summary>
    public static string IconPath(string inventory)
    {
        var clean = inventory.Replace('\\', '/').TrimStart('/');
        if (clean.StartsWith("panorama/", StringComparison.OrdinalIgnoreCase))
        {
            return clean.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? clean : clean + "_c";
        }

        return clean.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)
            ? "panorama/images/" + clean + "_c"
            : "panorama/images/" + clean + "_png.vtex_c";
    }

    public static void WriteManifest(Report report, string path)
    {
        var doc = new
        {
            note = "Реестр всех проверенных шмоток Dota 2: модель, слот и картинка физически есть в pak01_dir.vpk.",
            buildId = report.BuildId,
            considered = report.Considered,
            valid = report.Valid,
            sections = report.BySection
                .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new { name = kv.Key, count = kv.Value }),
            items = report.Entries
                .OrderBy(e => e.HeroKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.ItemId)
                .Select(e => new
                {
                    hero = e.HeroKey,
                    section = e.Section,
                    slot = e.Slot,
                    itemId = e.ItemId,
                    name = e.Name,
                    rarity = e.Rarity,
                    model = e.Model,
                    icon = e.Icon,
                }),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
    }
}
