using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wibe.Core.Dota;
using Wibe.Core.Econ;

namespace Wibe.Core.Mods;

/// <summary>
/// Мост к оригинальному Rust-ядру конструктора предметов (dota2-mod-manager, mm-core).
/// Ядро лежит внутри нашего exe как EmbeddedResource и распаковывается при первом запуске.
/// Наружу отдаёт ровно один pak06_dir.vpk в языковой папке; gameinfo.gi не трогается.
/// </summary>
public static class RustCoreModBuilder
{
    private const string ResourceName = "Wibe.RustBuilder.exe";
    private const string ExeName = "wibe-rust-builder.exe";
    private static readonly object Gate = new();

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "rust-core");

    public static string InstalledExe => Path.Combine(InstallDirectory, ExeName);

    public static string BasesDirectory => Path.Combine(InstallDirectory, "bases");

    public static string LogPath => Path.Combine(InstallDirectory, "rust-core.log");

    public static string? LastError { get; private set; }

    /// <summary>Распаковывает ядро рядом с кэшем; повторно — только если байты ресурса изменились.</summary>
    public static string? Ensure()
    {
        lock (Gate)
        {
            try
            {
                var assembly = typeof(RustCoreModBuilder).Assembly;
                using var stream = assembly.GetManifestResourceStream(ResourceName);
                if (stream is null)
                {
                    LastError = "ресурс ядра не найден, есть: " +
                                string.Join(", ", assembly.GetManifestResourceNames().Where(n => n.Contains("Rust", StringComparison.OrdinalIgnoreCase)));
                    Log(LastError);
                    return null;
                }

                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var bytes = memory.ToArray();
                var stampPath = Path.Combine(InstallDirectory, "core.sha256");
                var stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (File.Exists(InstalledExe) && File.Exists(stampPath) &&
                    string.Equals(File.ReadAllText(stampPath).Trim(), stamp, StringComparison.OrdinalIgnoreCase))
                {
                    return InstalledExe;
                }

                Directory.CreateDirectory(InstallDirectory);
                var tmp = InstalledExe + ".part";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(InstalledExe))
                {
                    File.Delete(InstalledExe);
                }

                File.Move(tmp, InstalledExe);
                File.WriteAllText(stampPath, stamp);
                return InstalledExe;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                Log(LastError);
                return null;
            }
        }
    }

    public sealed record Result(
        bool Success,
        string Message,
        int Files,
        long VpkSize,
        string? PakName,
        IReadOnlyList<string> Left);

    public sealed record PickRow(string Hero, string? Slot, string ItemId, string? Name, string? EffectId, int Style, bool RareTaunt = false);

    /// <summary>Мировая косметика: башни сторон и эмблема. null — как в игре.</summary>
    public sealed record WorldSelection(int? RadiantTower, int? DireTower, int? Emblem, int? RadiantCreep = null, int? DireCreep = null)
    {
        public bool IsEmpty => RadiantTower is null && DireTower is null && Emblem is null
                               && RadiantCreep is null && DireCreep is null;
    }

    /// <summary>
    /// Байты трёх подписанных Valve файлов и отсутствие чужих pak64. Снимок берётся до запуска
    /// ядра и сверяется после: любое расхождение — откат (пак удаляется) и ошибка, а не тихая порча.
    /// </summary>
    private static class ClientFiles
    {
        private const string GameInfo = "game/dota/gameinfo.gi";
        private const string BranchInfo = "game/dota/gameinfo_branchspecific.gi";
        private const string Signatures = "game/bin/win64/dota.signatures";

        // dota.signatures меняется с каждым обновлением игры, поэтому его хеш не прибит:
        // важно лишь, что файл на месте и остаётся байт-в-байт тем же до и после сборки.
        private static readonly (string Rel, string Sha1)[] Pinned =
        {
            (GameInfo, "a0dc87acc7a659eedd1f9575eee2bd3883e8f201"),
            (BranchInfo, "224b6f879d00d419b6a10fe07b4ddb3d827fd31f"),
        };

        public sealed record State(string GameInfo, string BranchInfo, string Signatures, string Pak64);

        public static string Sha1(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream)).ToLowerInvariant();
        }

        private static string HashOrMissing(string install, string rel)
        {
            var path = Path.Combine(install, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? Sha1(path) : "";
        }

        private static string FirstPak64(string install)
        {
            var game = Path.Combine(install, "game");
            if (!Directory.Exists(game))
            {
                return "";
            }

            foreach (var dir in Directory.EnumerateDirectories(game))
            {
                var pak = Path.Combine(dir, "pak64_dir.vpk");
                if (File.Exists(pak))
                {
                    return pak;
                }
            }

            return "";
        }

        public static State Snapshot(string install) => new(
            HashOrMissing(install, GameInfo),
            HashOrMissing(install, BranchInfo),
            HashOrMissing(install, Signatures),
            FirstPak64(install));

        /// <summary>Возвращает null, если клиент чист, иначе — что именно изменилось.</summary>
        public static string? Diff(State before, State after)
        {
            var problems = new List<string>();
            if (before.GameInfo != after.GameInfo)
            {
                problems.Add("gameinfo.gi");
            }

            if (before.BranchInfo != after.BranchInfo)
            {
                problems.Add("gameinfo_branchspecific.gi");
            }

            if (before.Signatures != after.Signatures)
            {
                problems.Add("dota.signatures");
            }

            if (after.Pak64.Length > 0)
            {
                problems.Add("появился " + after.Pak64);
            }

            return problems.Count == 0 ? null : string.Join(", ", problems);
        }

        public static string? PinnedMismatch(State state)
        {
            var problems = new List<string>();
            if (!string.Equals(state.GameInfo, Pinned[0].Sha1, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(GameInfo);
            }

            if (!string.Equals(state.BranchInfo, Pinned[1].Sha1, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(BranchInfo);
            }

            if (state.Signatures.Length == 0)
            {
                problems.Add(Signatures);
            }

            return problems.Count == 0 ? null : string.Join(", ", problems);
        }
    }

    private sealed record EffectiveLoadout(List<(string Slot, int ItemId)> Picks, Dictionary<string, int> Styles);

    /// <summary>
    /// The picks the player made, completed the way the app's frontend does it: an arcana is
    /// worn as its whole bundle, and a persona as its own default items. The player's own pick
    /// in a slot always wins; this only fills the slots it left empty. No path is ever touched.
    /// </summary>
    private static EffectiveLoadout BuildEffectiveLoadout(EconCatalog catalog, HeroPicks hero)
    {
        var picks = hero.Picks.Where(p => p.ItemId != 0 && p.Slot.Length > 0).ToList();
        var styles = hero.Styles is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(hero.Styles, StringComparer.OrdinalIgnoreCase);
        // The UI shows a persona in the hero's body slot, but Rust only turns a persona on
        // when it sees the selector slot. Normalise the pick before matching anything.
        var selector = picks
            .Select(p => (Pick: p, Item: catalog.ItemById(p.ItemId)))
            .FirstOrDefault(x => string.Equals(x.Item?.Slot, "persona_selector", StringComparison.OrdinalIgnoreCase));
        if (selector.Item is not null && !selector.Pick.Slot.Equals("persona_selector", StringComparison.OrdinalIgnoreCase))
        {
            picks.RemoveAll(p => p.Slot == selector.Pick.Slot && p.ItemId == selector.Pick.ItemId);
            picks.RemoveAll(p => p.Slot.Equals("persona_selector", StringComparison.OrdinalIgnoreCase));
            picks.Add(("persona_selector", selector.Pick.ItemId));
        }

        var bySlot = picks.ToDictionary(p => p.Slot, p => p.ItemId, StringComparer.OrdinalIgnoreCase);

        // a persona replaces the hero: classic items of his grown-up outfit would sit over it
        if (picks.Any(p => p.Slot.Equals("persona_selector", StringComparison.OrdinalIgnoreCase)))
        {
            picks.RemoveAll(p => !p.Slot.Equals("persona_selector", StringComparison.OrdinalIgnoreCase)
                                 && !p.Slot.Contains("_persona_", StringComparison.OrdinalIgnoreCase));
            bySlot = picks.ToDictionary(p => p.Slot, p => p.ItemId, StringComparer.OrdinalIgnoreCase);
            styles = styles
                .Where(s => bySlot.ContainsKey(s.Key))
                .ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);

            // every persona slot the player left empty wears the game's own default item
            foreach (var piece in PersonaDefaults(catalog, hero.Folder))
            {
                if (bySlot.ContainsKey(piece.Slot))
                {
                    continue;
                }

                bySlot[piece.Slot] = piece.Id;
                picks.Add((piece.Slot, piece.Id));
            }
        }

        foreach (var (slot, itemId) in picks.ToList())
        {
            var item = catalog.ItemById(itemId);
            var bundle = ArcanaBundleFor(catalog, hero.Folder, itemId);
            var arcana = item?.Rarity.Contains("arcana", StringComparison.OrdinalIgnoreCase) == true
                         || bundle?.Rarity.Contains("arcana", StringComparison.OrdinalIgnoreCase) == true;
            if (!arcana || bundle is null)
            {
                continue;
            }

            // выбран сам набор без слота — вместо него в пак пойдут его части
            var children = bundle.Items.Where(c => c.Id != itemId).ToList();
            if (itemId == bundle.Id && children.Count > 0)
            {
                picks.RemoveAll(p => p.Slot == slot && p.ItemId == itemId);
                bySlot.Remove(slot);
                styles.Remove(slot);
            }

            var sourceStyle = styles.TryGetValue(slot, out var wanted) ? wanted : 0;
            foreach (var child in children)
            {
                var childSlot = ItemSlotOf(child);
                if (bySlot.ContainsKey(childSlot))
                {
                    continue;
                }

                bySlot[childSlot] = child.Id;
                picks.Add((childSlot, child.Id));
                if (sourceStyle > 0)
                {
                    // Every piece of the bundle follows the pick: one with its own looked
                    // takes the style it has, one with none wears the bundle's skin (the
                    // arcana's pauldrons carry the second material set on their model).
                    var childItem = catalog.ItemById(child.Id);
                    styles[childSlot] = childItem is null || childItem.Styles == 0
                        ? sourceStyle
                        : Math.Min(sourceStyle, childItem.Styles - 1);
                }
            }
        }

        return new EffectiveLoadout(picks, styles);
    }

    /// <summary>The hero's own persona defaults, e.g. the Kid's hair, cape and sleeves.</summary>
    private static IEnumerable<EconItem> PersonaDefaults(EconCatalog catalog, string hero) =>
        catalog.ForHero(NpcHero(hero)).Where(i => i.IsDefault
            && i.Slot.Contains("_persona_", StringComparison.OrdinalIgnoreCase)
            && i.CompiledModel is not null);

    private static string NpcHero(string hero) =>
        hero.StartsWith("npc_dota_hero_", StringComparison.Ordinal) ? hero : "npc_dota_hero_" + hero;

    /// <summary>
    /// Some bundle pieces (a sword, a bow, a giant anchor) carry no item_slot in the table.
    /// The builder still needs the slot they dress, so give them the hero's weapon slot; it is
    /// the stock weapon the piece replaces, not a model path guessed by us.
    /// </summary>
    private static string ItemSlotOf(BundleItem item)
    {
        if (item.Slot.Length > 0)
        {
            return item.Slot;
        }

        var name = item.Name.ToLowerInvariant();
        if (name.Contains("offhand") || name.Contains("shield"))
        {
            return "offhand_weapon";
        }

        return "weapon";
    }

    private static BundleEntry? BundleFor(EconCatalog catalog, string hero, int itemId)
    {
        var names = new[] { hero, hero.StartsWith("npc_dota_hero_", StringComparison.Ordinal) ? hero : "npc_dota_hero_" + hero };
        foreach (var name in names)
        {
            var owning = catalog.BundleForItem(itemId, name);
            if (owning is not null)
            {
                return owning;
            }

            var direct = catalog.BundlesFor(name).FirstOrDefault(b => b.Id == itemId);
            if (direct is not null)
            {
                return direct;
            }
        }

        return null;
    }

    /// <summary>
    /// The arcana bundle a piece belongs to. An item can sit in two bundles (the WK crown is in
    /// The One True King and in King Kringle): the arcana's own bundle is the smaller one, while
    /// the larger one is a whole later set that merely reuses its pieces.
    /// </summary>
    private static BundleEntry? ArcanaBundleFor(EconCatalog catalog, string hero, int itemId)
    {
        var direct = catalog.BundlesFor(NpcHero(hero)).FirstOrDefault(b => b.Id == itemId);
        if (direct is not null)
        {
            return direct;
        }

        var owning = catalog.BundleForItem(itemId, NpcHero(hero));
        if (owning is null)
        {
            return null;
        }

        var all = catalog.BundlesFor(NpcHero(hero))
            .Where(b => b.Items.Any(i => i.Id == itemId))
            .ToList();
        if (all.Count > 1)
        {
            var arcanas = all.Where(b => b.Rarity.Contains("arcana", StringComparison.OrdinalIgnoreCase)).ToList();
            if (arcanas.Count > 0)
            {
                all = arcanas;
            }

            return all.OrderBy(b => b.Items.Count).First();
        }

        return owning;
    }

    /// <summary>Собирает pak06_dir.vpk из выбранных предметов; log получает строки ядра по мере сборки.</summary>
    public static Result Build(
        DotaInstallation install,
        EconCatalog catalog,
        IReadOnlyList<HeroPicks> loadout,
        Action<string>? log = null,
        WorldSelection? world = null,
        string? outputOverride = null)
    {
        var exe = Ensure();
        if (exe is null)
        {
            return new Result(false, "Rust-ядро не распаковалось: " + (LastError ?? "неизвестная причина"), 0, 0, null, Array.Empty<string>());
        }

        var configured = loadout.Where(h => h.Picks.Count > 0).ToList();
        var worldEmpty = world is null || world.IsEmpty;
        if (configured.Count == 0 && worldEmpty)
        {
            return new Result(false, "примерка пуста: сначала выбери скины героям или косметику мира", 0, 0, null, Array.Empty<string>());
        }

        if (DotaInstallation.IsGameRunning())
        {
            return new Result(false, "Dota 2 запущена: закрой игру и повтори", 0, 0, null, Array.Empty<string>());
        }

        var before = ClientFiles.Snapshot(install.GameDir);
        var pinned = ClientFiles.PinnedMismatch(before);
        if (pinned is not null)
        {
            Log("GUARD: signed Valve files are not clean: " + pinned);
            return new Result(false, "клиент Dota 2 изменён извне (" + pinned + "). Восстанови файлы и повтори.", 0, 0, null, Array.Empty<string>());
        }

        if (before.Pak64.Length > 0)
        {
            Log("GUARD: another tool's pak64 is present: " + before.Pak64);
            return new Result(false, "в игре лежит чужой pak64 (" + before.Pak64 + "). Удали его и повтори.", 0, 0, null, Array.Empty<string>());
        }

        const string pakName = "pak06_dir.vpk";
        var outPath = outputOverride ?? Path.Combine(install.ModFolderPath, pakName);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        if (outputOverride is null)
        {
            Directory.CreateDirectory(install.ModFolderPath);
        }
        Directory.CreateDirectory(BasesDirectory);

        // the player's picks, completed exactly as the frontend completes them: an arcana is
        // sent as its whole bundle and a persona as its own default items. Rust still decides
        // every path, every stock model and every file in the pak.
        var rows = new List<PickRow>();
        var heroPedestals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hero in configured)
        {
            var effective = BuildEffectiveLoadout(catalog, hero);
            foreach (var (slot, itemId) in effective.Picks)
            {
                if (itemId == 0 || slot.Length == 0)
                {
                    continue;
                }

                var item = catalog.ItemById(itemId);
                var style = effective.Styles.TryGetValue(slot, out var wanted) ? wanted : 0;
                if (Wibe.Core.World.PedestalCatalog.ThroneFor(hero.Folder, itemId) is { } throne)
                {
                    heroPedestals["skeleton_king"] = throne;
                }

                rows.Add(new PickRow(
                    hero.Folder,
                    slot,
                    itemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    item?.Name,
                    style > 0 ? "style-" + style.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    style,
                    hero.RareTaunt && string.Equals(slot, "taunt", StringComparison.OrdinalIgnoreCase)));
            }
        }

        var manifest = new JsonObject
        {
            ["game"] = install.GameDir,
            ["out"] = outPath,
            ["bases_dir"] = BasesDirectory,
            ["picks"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["hero"] = r.Hero,
                ["slot"] = r.Slot,
                ["item_id"] = r.ItemId,
                ["name"] = r.Name,
                ["effect_id"] = r.EffectId,
                ["style"] = r.Style,
                ["rare_taunt"] = r.RareTaunt,
            }).ToArray()),
        };

        if (heroPedestals.Count > 0)
        {
            var pedestals = new JsonObject();
            foreach (var (hero, model) in heroPedestals)
            {
                pedestals[hero] = model;
            }

            manifest["hero_pedestals"] = pedestals;
        }

        if (world is { IsEmpty: false })
        {
            manifest["world"] = new JsonObject
            {
                ["radiant_tower"] = world.RadiantTower?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["dire_tower"] = world.DireTower?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["emblem"] = world.Emblem?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["radiant_creep"] = world.RadiantCreep?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["dire_creep"] = world.DireCreep?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        var manifestPath = Path.Combine(InstallDirectory, "build_manifest.json");
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        if (File.Exists(outPath))
        {
            try
            {
                File.Delete(outPath);
            }
            catch (IOException ex)
            {
                return new Result(false, "старый пак занят: " + ex.Message, 0, 0, pakName, Array.Empty<string>());
            }
        }

        var output = new List<string>();
        var error = new List<string>();
        var left = new List<string>();
        JsonObject? final = null;
        try
        {
            var info = new ProcessStartInfo(exe, "\"" + manifestPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = InstallDirectory,
            };
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                lock (output)
                {
                    output.Add(e.Data);
                }

                try
                {
                    var node = JsonNode.Parse(e.Data) as JsonObject;
                    if (node is null)
                    {
                        log?.Invoke(e.Data);
                        return;
                    }

                    if (node["log"] is JsonNode line)
                    {
                        var text = line.GetValue<string>();
                        Log(text);
                        log?.Invoke(text);
                    }
                    else if (node["stage"] is JsonNode stage)
                    {
                        var done = node["done"]?.GetValue<int>() ?? 0;
                        var total = node["total"]?.GetValue<int>() ?? 0;
                        log?.Invoke($"{stage.GetValue<string>()}: {done}/{total}");
                    }
                    else if (node["ok"] is JsonNode ok)
                    {
                        final = node;
                        if (ok.GetValue<bool>())
                        {
                            if (node["left"] is JsonArray arr)
                            {
                                foreach (var item in arr)
                                {
                                    var source = item?["source"]?.GetValue<string>() ?? "?";
                                    var what = item?["what"] is JsonArray w
                                        ? string.Join(", ", w.Select(x => x?.GetValue<string>() ?? ""))
                                        : "";
                                    left.Add($"{source}: {what}");
                                    log?.Invoke($"без таблицы осталось: {source} — {what}");
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    Log(e.Data);
                    log?.Invoke(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                lock (error)
                {
                    error.Add(e.Data);
                }

                Log(e.Data);
                log?.Invoke(e.Data);
            };

            if (!process.Start())
            {
                return new Result(false, "Rust-ядро не запустилось", 0, 0, pakName, Array.Empty<string>());
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode != 0 || final?["ok"]?.GetValue<bool>() != true)
            {
                var detail = final?["error"]?.GetValue<string>()
                             ?? (error.Count > 0 ? error[^1] : "ядро завершилось с кодом " + process.ExitCode);
                Log("FAILED: " + detail);
                return new Result(false, "сборка Rust-ядром не удалась: " + detail, 0, 0, pakName, left);
            }

            if (!File.Exists(outPath))
            {
                return new Result(false, "Rust-ядро не создало " + outPath, 0, 0, pakName, left);
            }

            var after = ClientFiles.Snapshot(install.GameDir);
            var changed = ClientFiles.Diff(before, after);
            if (changed is not null)
            {
                Log("GUARD: the client changed during the build: " + changed);
                try
                {
                    File.Delete(outPath);
                }
                catch (IOException)
                {
                }

                return new Result(false, "ядро тронуло файлы клиента (" + changed + "); пак удалён, игра не запускалась.", 0, 0, pakName, left);
            }

            var size = new FileInfo(outPath).Length;
            var files = final?["files"]?.GetValue<int>() ?? 0;
            var heroes = configured.Count;
            Log($"OK: {outPath}, {files} файлов, {size} байт, героев {heroes}");
            return new Result(
                true,
                $"готово: {heroes} гер., {files} файлов, {size / 1024 / 1024.0:0.0} МБ — Rust-ядро",
                files,
                size,
                pakName,
                left);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log("EXCEPTION: " + ex);
            return new Result(false, "Rust-ядро: " + ex.Message, 0, 0, pakName, left);
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
