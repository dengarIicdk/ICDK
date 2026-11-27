using System.Text;
using Wibe.Core.Diagnostics;
using Wibe.Core.Dota;
using Wibe.Core.Index;
using Wibe.Core.Mods;

namespace Wibe.Cli;

internal static class Program
{
    private const string Version = "0.2.0";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var options = ParseOptions(args.Skip(1));

        return command switch
        {
            "doctor" => RunDoctor(options),
            "find" => RunFind(options),
            "install" => RunInstall(options),
            "uninstall" or "remove" => RunUninstall(options),
            "mods" or "list" => RunMods(),

            "econ" => RunEcon(options),
            "overlay" => RunOverlay(options),
            "help" or "--help" or "-h" or "/?" => Help(),
            "version" or "--version" => PrintVersion(),
            _ => Unknown(command),
        };
    }

    private static int RunDoctor(Dictionary<string, string?> options)
    {
        WriteHeader("doctor");
        var report = Doctor.Run();

        if (report.FatalError is not null)
        {
            WriteLine(ConsoleColor.Red, "  ✗ " + report.FatalError);
            Console.WriteLine();
            return 2;
        }

        PrintSectioned(report);

        var install = report.Installation!;
        WriteLine(
            report.ReadyToInstall ? ConsoleColor.Green : ConsoleColor.Red,
            report.ReadyToInstall
                ? $"  ГОТОВО к установке модов · build {install.BuildId} · папка {install.ModFolderName} · слот pak{install.RecommendedFreeSlot:00}"
                : "  ЕСТЬ БЛОКЕРЫ — смотри ✗ выше");

        Console.WriteLine();
        WriteLine(ConsoleColor.DarkGray, "  Моды — локальные файлы. Сервер присылает только путь к модели, поэтому подмена видна в обычных");
        WriteLine(ConsoleColor.DarkGray, "  и рейтинговых матчах только тебе и не даёт игрового преимущества. Официальных гарантий Valve нет.");
        Console.WriteLine();
        return report.ReadyToInstall ? 0 : 1;
    }

    private static int RunFind(Dictionary<string, string?> options)
    {
        var query = Positional(options);
        if (string.IsNullOrWhiteSpace(query))
        {
            WriteLine(ConsoleColor.Red, "  укажи подстроку: wibe find axe_carnival");
            return 2;
        }

        var install = RequireInstall();
        if (install is null)
        {
            return 2;
        }

        WriteHeader($"find «{query}»");
        var index = PakIndex.Load(install, forceRefresh: options.ContainsKey("--refresh"));
        var limit = ParseInt(options, "--limit") ?? 40;

        var matches = index.Find(query, limit).ToList();
        foreach (var match in matches)
        {
            Console.WriteLine($"  {match.Path}");
        }

        Console.WriteLine();
        WriteLine(ConsoleColor.DarkGray, $"  найдено {matches.Count} из {index.Entries.Count} файлов · индекс {(index.FromCache ? "из кеша" : "свежий")} · build {index.BuildId}");
        return matches.Count > 0 ? 0 : 1;
    }

    private static int RunInstall(Dictionary<string, string?> options)
    {
        var install = RequireInstall();
        if (install is null)
        {
            return 2;
        }

        var source = CleanPath(options.GetValueOrDefault("--source"));
        var target = CleanPath(options.GetValueOrDefault("--target"));
        var hero = options.GetValueOrDefault("--hero")?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(source))
        {
            WriteLine(ConsoleColor.Red, "  нужен --source <путь модели в игре> (см. wibe find)");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            if (string.IsNullOrWhiteSpace(hero))
            {
                WriteLine(ConsoleColor.Red, "  нужен --hero axe (тело героя) или --target <точный путь в паке>");
                return 2;
            }

            hero = hero.Replace("npc_dota_hero_", string.Empty);
            target = $"models/heroes/{hero}/{hero}.vmdl_c";
        }

        var install2 = (hero is null ? target! : target!);
        var installer = new ModInstaller(install);
        var title = options.GetValueOrDefault("--title") ?? $"{(hero ?? target)} ← {Path.GetFileName(source)}";

        WriteHeader("install");
        Console.WriteLine($"  цель:   {install2}");
        Console.WriteLine($"  источник: {source}");
        Console.WriteLine();

        var result = installer.InstallModel(
            install2,
            source!,
            title,
            kind: options.GetValueOrDefault("--kind") ?? "hero.body",
            heroId: hero,
            slot: ParseInt(options, "--slot"));

        WriteLine(result.Success ? ConsoleColor.Green : ConsoleColor.Red, (result.Success ? "  ✓ " : "  ✗ ") + result.Message);
        if (result.Mod is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  пак: {result.Mod.VpkPath}");
            Console.WriteLine($"  бэкап: {result.Mod.BackupPath}");
            Console.WriteLine($"  crc: 0x{result.Mod.SourceCrc:x8} · {DiagnosticsFormat(result.Mod.Size)}");
            Console.WriteLine();
            WriteLine(ConsoleColor.Yellow, "  Дальше: запусти Dota 2 → «Создать лобби» с ботами или «Демонстрация героя» и проверь визуал.");
        }

        return result.Success ? 0 : 1;
    }

    private static int RunUninstall(Dictionary<string, string?> options)
    {
        var install = RequireInstall();
        if (install is null)
        {
            return 2;
        }

        var slot = ParseInt(options, "--slot");
        if (slot is null)
        {
            WriteLine(ConsoleColor.Red, "  нужен --slot 06");
            return 2;
        }

        WriteHeader("uninstall");
        var result = new ModInstaller(install).Uninstall(slot.Value);
        WriteLine(result.Success ? ConsoleColor.Green : ConsoleColor.Red, (result.Success ? "  ✓ " : "  ✗ ") + result.Message);
        return result.Success ? 0 : 1;
    }

    private static int RunOverlay(Dictionary<string, string?> options)
    {
        var install = RequireInstall();
        if (install is null)
        {
            return 1;
        }

        WriteHeader("overlay");
        var index = Wibe.Core.Index.PakIndex.Load(install);
        var catalog = Wibe.Core.Econ.EconCatalog.Load(install);
        var sets = new Wibe.Core.Sets.HeroSetService(install, index, catalog);
        var session = new Wibe.Core.Mods.ModSession(install, index, catalog, sets);

        if (options.ContainsKey("--status") || options.Keys.All(k => k is "--status"))
        {
            var status = session.GameInfo.Status();
            WriteLine(status.Patched ? ConsoleColor.Red : ConsoleColor.Gray, $"  gameinfo.gi игры: {(status.Patched ? "ПРОПАТЧЕН (координатор заблокирует поиск!)" : "чистый — проверка матча проходит")}");
            WriteLine(File.Exists(session.ModVpkPath) ? ConsoleColor.Green : ConsoleColor.Gray, $"  мод-пак: {session.ModPakName} · {(File.Exists(session.ModVpkPath) ? new FileInfo(session.ModVpkPath).Length / 1024 / 1024 + " МБ" : "нет")} · папка: {session.ModFolderPath}");
            var mount = session.CheckMount();
            WriteLine(mount.Ok ? ConsoleColor.Green : ConsoleColor.Yellow, "  mount: " + mount.Message);
            Console.WriteLine();
            return 0;
        }

        if (options.ContainsKey("--revert"))
        {
            var revert = session.Revert();
            WriteLine(revert.Success ? ConsoleColor.Green : ConsoleColor.Red, "  " + revert.Message);
            Console.WriteLine();
            return revert.Success ? 0 : 1;
        }

        if (options.ContainsKey("--launch"))
        {
            var launch = session.Launch(conDebug: options.ContainsKey("--condebug"));
            WriteLine(launch.Success ? ConsoleColor.Green : ConsoleColor.Red, "  " + launch.Message);
            Console.WriteLine();
            return launch.Success ? 0 : 1;
        }

        var hero = options.GetValueOrDefault("--hero");
        if (hero is null)
        {
            WriteLine(ConsoleColor.Red, "  нужен --hero <имя> или --revert / --status / --launch");
            Console.WriteLine();
            return 1;
        }

        var picks = new List<(string Slot, int ItemId)>();
        foreach (var pair in (options.GetValueOrDefault("--pick") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=');
            if (parts.Length == 2 && int.TryParse(parts[1], out var id))
            {
                picks.Add((parts[0], id));
            }
        }

        if (picks.Count == 0)
        {
            var dto = sets.Get(hero);
            if (dto is null)
            {
                WriteLine(ConsoleColor.Red, "  герой не найден");
                Console.WriteLine();
                return 1;
            }

            picks.Add(("weapon", dto.Slots.FirstOrDefault(s => s.Slot == "weapon")?.Items.FirstOrDefault()?.Id ?? 0));
            picks = picks.Where(p => p.ItemId != 0).ToList();
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = session.Build(hero, picks);
        watch.Stop();
        WriteLine(outcome.Success ? ConsoleColor.Green : ConsoleColor.Red, $"  {outcome.Message} · {watch.ElapsedMilliseconds} мс");
        foreach (var remap in outcome.Remaps)
        {
            WriteLine(remap.Applied ? ConsoleColor.Gray : ConsoleColor.DarkYellow, $"    {remap.Slot}: {(remap.Applied ? remap.Detail : "не применено — " + remap.Detail)}");
        }

        Console.WriteLine();
        return outcome.Success ? 0 : 1;
    }

    private static int RunEcon(Dictionary<string, string?> options)
    {
        var install = RequireInstall();
        if (install is null)
        {
            return 1;
        }

        WriteHeader("econ");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var catalog = Wibe.Core.Econ.EconCatalog.Load(install, refresh: options.ContainsKey("--refresh"));
        watch.Stop();

        WriteLine(ConsoleColor.Green, $"  ✓ предметов: {catalog.Items.Count:N0} · героев: {catalog.HeroKeys.Count} · {watch.ElapsedMilliseconds} мс" + (catalog.FromCache ? " (кеш)" : " (разбор)"));
        WriteLine(ConsoleColor.DarkGray, "  предметы с моделью: " + catalog.Items.Count(i => i.CompiledModel is not null).ToString("N0"));

        var hero = options.GetValueOrDefault("--hero") ?? "axe";
        var key = hero.StartsWith("npc_dota_hero", StringComparison.OrdinalIgnoreCase) ? hero : "npc_dota_hero_" + hero;
        var items = catalog.ForHero(key);
        WriteLine(ConsoleColor.White, $"  {key}: {items.Count} предметов");
        foreach (var item in items.GroupBy(i => i.Slot).OrderByDescending(g => g.Count()).Take(12))
        {
            WriteLine(ConsoleColor.Gray, $"    {item.Key,-12} {item.Count(),5}   например: {item.First().Name}");
        }

        WriteLine(ConsoleColor.White, "  стоковые (цели подмены):");
        foreach (var item in catalog.DefaultsFor(key))
        {
            WriteLine(ConsoleColor.Gray, $"    {item.Slot,-12} {item.CompiledModel}");
        }

        var index = Wibe.Core.Index.PakIndex.Load(install);
        var service = new Wibe.Core.Sets.HeroSetService(install, index, catalog);
        var set = service.Get(key["npc_dota_hero_".Length..]);
        if (set is null)
        {
            WriteLine(ConsoleColor.Red, "  герой не найден");
            Console.WriteLine();
            return 1;
        }

        WriteLine(ConsoleColor.White, $"  слоты сета: {set.Slots.Count}");
        foreach (var slot in set.Slots)
        {
            WriteLine(ConsoleColor.Gray, $"    {slot.Label,-10} {slot.Items.Count,4} вариантов → {slot.Target}");
            foreach (var it in slot.Items.Take(2))
            {
                Console.WriteLine($"        · {it.Name} [{it.Id}] {it.Model}");
            }
        }

        Console.WriteLine();
        return 0;
    }

    private static int RunMods()
    {
        var install = RequireInstall();
        if (install is null)
        {
            return 2;
        }

        WriteHeader("mods");
        var journal = ModJournal.Load();
        if (journal.Mods.Count == 0)
        {
            WriteLine(ConsoleColor.DarkGray, "  журнал пуст — ставили моды другим способом или ещё ничего не ставили");
        }
        else
        {
            foreach (var mod in journal.Mods)
            {
                var exists = File.Exists(mod.VpkPath);
                WriteLine(
                    exists ? ConsoleColor.Green : ConsoleColor.Yellow,
                    $"  pak{mod.Slot:00}  {mod.Title}");
                Console.WriteLine($"        {mod.TargetPath} ← {mod.SourcePath}");
                Console.WriteLine($"        {DiagnosticsFormat(mod.Size)} · crc 0x{mod.SourceCrc:x8} · {mod.InstalledAt:yyyy-MM-dd HH:mm}");
            }
        }

        Console.WriteLine();
        var occupied = string.Join(", ", install.OccupiedSlots.Where(s => !s.IsValveLocalization)
            .Select(s => $"pak{s.Number:00} ({DiagnosticsFormat(s.SizeBytes)})"));
        WriteLine(ConsoleColor.DarkGray, $"  в папке модов: {occupied}");
        return 0;
    }

    private static void PrintSectioned(DoctorReport report)
    {
        var currentSection = string.Empty;
        foreach (var line in report.Lines)
        {
            if (!string.Equals(line.Section, currentSection, StringComparison.Ordinal))
            {
                currentSection = line.Section;
                Console.WriteLine();
                WriteLine(ConsoleColor.DarkGray, "  " + currentSection.ToUpperInvariant());
            }

            var (color, glyph) = line.Level switch
            {
                DiagLevel.Ok => (ConsoleColor.Green, "✓"),
                DiagLevel.Info => (ConsoleColor.Gray, "·"),
                DiagLevel.Warn => (ConsoleColor.Yellow, "!"),
                _ => (ConsoleColor.Red, "✗"),
            };

            WriteLine(color, $"  {glyph} {line.Text}");
        }
    }

    private static DotaInstallation? RequireInstall()
    {
        var install = DotaInstallation.Detect(out var error);
        if (install is null || !install.IsValid)
        {
            WriteLine(ConsoleColor.Red, "  ✗ " + (error ?? "Dota 2 не найдена"));
            return null;
        }

        return install;
    }

    private static Dictionary<string, string?> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;
        var positionals = new List<string>();
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                {
                    result[pending] = "true";
                }

                var eq = arg.IndexOf('=');
                if (eq > 0)
                {
                    result[arg[..eq]] = arg[(eq + 1)..];
                    pending = null;
                }
                else
                {
                    pending = arg;
                }

                continue;
            }

            if (pending is not null)
            {
                result[pending] = arg;
                pending = null;
                continue;
            }

            positionals.Add(arg);
        }

        if (pending is not null)
        {
            result[pending] = "true";
        }

        if (positionals.Count > 0)
        {
            result["__positional"] = string.Join(' ', positionals);
        }

        return result;
    }

    private static string? Positional(Dictionary<string, string?> options) =>
        options.TryGetValue("__positional", out var value) ? value : null;

    private static int? ParseInt(Dictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;

    private static string? CleanPath(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DiagnosticsFormat(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} МБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} КБ",
        _ => $"{bytes} Б",
    };

    private static int Help()
    {
        WriteHeader("help");
        Console.WriteLine("  Команды:");
        Console.WriteLine("    wibe doctor [--json]                 проверить установку и готовность");
        Console.WriteLine("    wibe find <подстрока> [--limit 40]   найти модели в паке игры");
        Console.WriteLine("    wibe install --hero axe --source <путь> [--slot 06]");
        Console.WriteLine("    wibe mods                            что установлено");
        Console.WriteLine("    wibe uninstall --slot 06             снять мод");
        Console.WriteLine();
        Console.WriteLine("  Пример:");
        Console.WriteLine("    wibe find axe_carnival");
        Console.WriteLine("    wibe install --hero axe --source models/items/axe/axe_carnival/axe_carnival_base.vmdl_c");
        Console.WriteLine();
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"wibe {Version}");
        return 0;
    }

    private static int Unknown(string command)
    {
        WriteLine(ConsoleColor.Red, $"неизвестная команда: {command}");
        return Help();
    }

    private static void WriteHeader(string command)
    {
        Console.WriteLine();
        WriteLine(ConsoleColor.DarkGray, $"  WIBE FITTING ROOM {Version} · {command}");
        WriteLine(ConsoleColor.DarkGray, "  " + new string('─', 72));
    }

    private static void WriteLine(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
