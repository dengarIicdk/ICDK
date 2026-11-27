using Wibe.Core.Dota;
using Wibe.Core.Policy;
using Wibe.Core.Tools;

namespace Wibe.Core.Diagnostics;

public enum DiagLevel
{
    Ok,
    Info,
    Warn,
    Error,
}

public sealed record DiagLine(DiagLevel Level, string Section, string Text);

public sealed record ModPakInfo(
    int Number,
    string FilePath,
    long SizeBytes,
    int? EntryCount,
    int BlockedCount,
    int WarnCount,
    IReadOnlyList<string> SamplePaths,
    IReadOnlyList<string> BlockedSamples,
    IReadOnlyList<string> Paths)
{
    public string FileName => Path.GetFileName(FilePath);
}

public sealed record ConflictInfo(string Path, IReadOnlyList<string> Paks);

public sealed class DoctorReport
{
    public DotaInstallation? Installation { get; set; }

    public string? FatalError { get; set; }

    public bool GameRunning { get; set; }

    public List<DiagLine> Lines { get; } = new();

    public List<ModPakInfo> Paks { get; } = new();

    public List<ConflictInfo> Conflicts { get; } = new();

    public int SignatureTotal { get; set; }

    public int SignatureMatched { get; set; }

    public int SignatureMissing { get; set; }

    public List<string> SignatureMismatched { get; } = new();

    public bool HasErrors => Lines.Any(l => l.Level == DiagLevel.Error) || FatalError is not null;

    public bool HasWarnings => Lines.Any(l => l.Level == DiagLevel.Warn);

    /// <summary>Можно ли безопасно ставить моды прямо сейчас (без учёта «нравится/не нравится»).</summary>
    public bool ReadyToInstall => FatalError is null && !HasErrors && !GameRunning && SignatureMismatched.Count == 0;
}

/// <summary>
/// `wibe doctor`: полная проверка окружения. Всё только читает.
/// Отвечает на вопрос «сработает ли мод в обычной и рейтинговой игре на этой машине».
/// </summary>
public static class Doctor
{
    public static DoctorReport Run()
    {
        var report = new DoctorReport();
        var installation = DotaInstallation.Detect(out var error);
        if (installation is null)
        {
            report.FatalError = error;
            return report;
        }

        report.Installation = installation;
        report.GameRunning = DotaInstallation.IsGameRunning();

        CheckSteam(report, installation);
        CheckGame(report, installation);
        CheckModFolder(report, installation);
        CheckSignatures(report, installation);
        CheckPaks(report, installation);
        CheckConflicts(report);
        CheckMount(report, installation);
        CheckRankedReadiness(report, installation);
        return report;
    }

    /// <summary>
    /// Реальная проверка по логу игры: смонтировал ли Source 2 наш пак из языковой папки.
    /// Лог появляется при запуске с -condebug (game/dota/console.log).
    /// </summary>
    private static void CheckMount(DoctorReport report, DotaInstallation install)
    {
        const string section = "Монтирование";
        if (!install.ModFolderExists)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Warn, section, "папки языка нет — мод некуда положить"));
            return;
        }

        var paks = new List<(int Number, string Path)>();
        foreach (var file in Directory.EnumerateFiles(install.ModFolderPath, "pak*_dir.vpk", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var digits = new string(name.Skip(3).TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var number) || number <= 1 || number == 64)
            {
                continue;
            }

            paks.Add((number, file));
        }

        if (paks.Count == 0)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Info, section, $"в {install.ModFolderName} пока нет пака мода — собери сет"));
            return;
        }

        var newest = paks.OrderByDescending(p => File.GetLastWriteTimeUtc(p.Path)).First();
        var pakName = $"pak{newest.Number:00}.vpk";
        var logPath = Path.Combine(install.GameDir, "game", "dota", "console.log");
        if (!File.Exists(logPath))
        {
            report.Lines.Add(new DiagLine(DiagLevel.Warn, section,
                $"console.log нет — запусти игру с -condebug, тогда видно, примонтирован ли {install.ModFolderName}/{pakName}"));
            return;
        }

        string log;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            log = reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Warn, section, "console.log занят: " + ex.Message));
            return;
        }

        var mounted = log.Contains(install.ModFolderName + "\\", StringComparison.OrdinalIgnoreCase) &&
                      log.Contains(pakName, StringComparison.OrdinalIgnoreCase);
        if (mounted)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Ok, section,
                $"Source 2 смонтировал {install.ModFolderName}/{pakName} (по console.log)"));
        }
        else
        {
            report.Lines.Add(new DiagLine(DiagLevel.Warn, section,
                $"в console.log нет {install.ModFolderName}/{pakName}: мод не примонтирован — запусти с -condebug"));
        }
    }

    private static void CheckSteam(DoctorReport report, DotaInstallation install)
    {
        const string section = "Steam";
        report.Lines.Add(new DiagLine(DiagLevel.Ok, section, $"библиотека: {install.SteamLibrary}"));
        report.Lines.Add(new DiagLine(DiagLevel.Info, section, $"appid {install.App.AppId} · {install.App.Name}"));
        if (install.App.SizeOnDisk is { } size)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Info, section, $"размер установки: {FormatSize(size)}"));
        }
    }

    private static void CheckGame(DoctorReport report, DotaInstallation install)
    {
        const string section = "Игра";
        if (!install.IsValid)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Error, section, $"каталог игры не найден: {install.GameDir}"));
            return;
        }

        report.Lines.Add(new DiagLine(DiagLevel.Ok, section, install.GameDir));
        report.Lines.Add(new DiagLine(DiagLevel.Ok, section, $"buildid {install.BuildId ?? "неизвестен"}"));
        report.Lines.Add(new DiagLine(
            DiagLevel.Info,
            section,
            $"AudioLanguage «{install.AudioLanguage}» → папка модов «{install.ModFolderName}»"));
        report.Lines.Add(new DiagLine(
            DiagLevel.Info,
            section,
            $"основной пак: {Path.GetFileName(install.MainPakDir)} ({FormatSize(new FileInfo(install.MainPakDir).Length)})"));

        if (report.GameRunning)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Warn, section, "Dota 2 запущена — установка модов будет недоступна до выхода из игры"));
        }
    }

    private static void CheckModFolder(DoctorReport report, DotaInstallation install)
    {
        const string section = "Папка модов";
        if (!install.ModFolderExists)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Error, section, $"нет папки {install.ModFolderPath}"));
            return;
        }

        report.Lines.Add(new DiagLine(DiagLevel.Ok, section, install.ModFolderPath));

        if (install.ModFolderLayeredOnDota)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Ok, section, "gameinfo.gi: LayeredOnMod dota — папка монтируется поверх игры (штатный механизм Valve)"));
        }
        else
        {
            report.Lines.Add(new DiagLine(DiagLevel.Error, section, "в gameinfo.gi папки нет LayeredOnMod dota — моды не подхватятся"));
        }

        if (install.ModFolderHasSelfSearchPath)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Ok, section, $"SearchPaths: {install.ModFolderName} первой в списке Game"));
        }
        else
        {
            report.Lines.Add(new DiagLine(DiagLevel.Warn, section, "SearchPaths: папка модов не первая — приоритет над dota не гарантирован"));
        }

        var slots = install.OccupiedSlots;
        if (slots.Count == 0)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Info, section, "свободны все слоты pak02…pak99"));
            return;
        }

        var occupied = string.Join(", ", slots.Select(s => $"pak{s.Number:00} ({FormatSize(s.SizeBytes)})"));
        report.Lines.Add(new DiagLine(DiagLevel.Info, section, $"занято: {occupied}"));

        var free = install.FreeSlots.Take(5).Select(n => $"pak{n:00}");
        report.Lines.Add(new DiagLine(DiagLevel.Ok, section, $"свободные слоты: {string.Join(", ", free)} …"));
        report.Lines.Add(new DiagLine(DiagLevel.Ok, section, $"рекомендуемый слот: pak{install.RecommendedFreeSlot:00}"));
    }

    private static void CheckSignatures(DoctorReport report, DotaInstallation install)
    {
        const string section = "Целостность клиента";
        var set = SignatureSet.TryLoad(install.SignaturesPath);
        if (set is null)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Warn, section, $"нет {Path.GetFileName(install.SignaturesPath)} — пропускаю проверку подписей"));
            return;
        }

        var results = set.VerifyAll();
        report.SignatureTotal = results.Count;
        report.SignatureMatched = results.Count(r => r.Exists && r.Matches);
        report.SignatureMissing = results.Count(r => !r.Exists);

        foreach (var bad in results.Where(r => r.Exists && !r.Matches))
        {
            report.SignatureMismatched.Add($"{bad.Path} (ждали {bad.ExpectedSha1}, на диске {bad.ActualSha1})");
        }

        if (report.SignatureMatched == report.SignatureTotal)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Ok, section, $"подписанные файлы Valve совпадают с диском: {report.SignatureMatched}/{report.SignatureTotal}"));
            report.Lines.Add(new DiagLine(DiagLevel.Info, section, "gameinfo.gi и остальное, что Valve хеширует, — нетронуты"));
        }
        else
        {
            report.Lines.Add(new DiagLine(
                DiagLevel.Error,
                section,
                $"не совпадает {report.SignatureMismatched.Count} из {report.SignatureTotal} — игру правили вручную, это риск"));
            foreach (var line in report.SignatureMismatched.Take(5))
            {
                report.Lines.Add(new DiagLine(DiagLevel.Error, section, line));
            }
        }

        if (report.SignatureMissing > 0)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Info, section, $"ещё {report.SignatureMissing} файлов из списка отсутствуют (обычно ок для необязательных подсистем)"));
        }
    }

    private static void CheckPaks(DoctorReport report, DotaInstallation install)
    {
        const string section = "Моды в папке";
        var exe = Source2ViewerCli.Locate();
        var slots = install.OccupiedSlots.Where(s => !s.IsValveLocalization).ToList();
        if (slots.Count == 0)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Info, section, "посторонних паков нет — папка модов чистая"));
            return;
        }

        if (exe is null)
        {
            report.Lines.Add(new DiagLine(
                DiagLevel.Warn,
                section,
                $"{Source2ViewerCli.ExeName} не найден — индексы паков не читаю (положи его в bin/ или задай WIBE_S2V)"));
        }

        foreach (var slot in slots)
        {
            int? entries = null;
            var blocked = new List<string>();
            var warned = new List<string>();
            var samples = new List<string>();
            var paths = new List<string>();

            if (exe is not null)
            {
                try
                {
                    var files = Source2ViewerCli.ListVpk(slot.DirVpkPath, exe, TimeSpan.FromSeconds(90));
                    entries = files.Count;
                    foreach (var file in files)
                    {
                        paths.Add(file.Path);
                        if (samples.Count < 3)
                        {
                            samples.Add(file.Path);
                        }

                        if (SafetyPolicy.Evaluate(file.Path) == SafetyVerdict.Blocked)
                        {
                            if (blocked.Count < 20)
                            {
                                blocked.Add(file.Path);
                            }
                        }
                        else if (SafetyPolicy.Evaluate(file.Path) == SafetyVerdict.Warn && warned.Count < 200)
                        {
                            warned.Add(file.Path);
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
                {
                    report.Lines.Add(new DiagLine(DiagLevel.Warn, section, $"pak{slot.Number:00}: индекс не прочитан ({ex.Message})"));
                }
            }

            report.Paks.Add(new ModPakInfo(slot.Number, slot.DirVpkPath, slot.SizeBytes, entries, blocked.Count, warned.Count, samples, blocked, paths));

            var state = entries is null ? "индекс не прочитан" : $"{entries} файлов, {FormatSize(slot.SizeBytes)}";
            report.Lines.Add(new DiagLine(DiagLevel.Info, section, $"pak{slot.Number:00}: {state}"));

            if (blocked.Count > 0)
            {
                report.Lines.Add(new DiagLine(
                    DiagLevel.Warn,
                    section,
                    $"pak{slot.Number:00}: {blocked.Count} файлов категории C (например {blocked[0]}) — этот пак ставили не мы, и он трогает видимость"));
            }

            if (warned.Count > 0)
            {
                report.Lines.Add(new DiagLine(
                    DiagLevel.Info,
                    section,
                    $"pak{slot.Number:00}: {warned.Count} файлов категории B (строения/крипы/террейн/частицы) — косметика с предупреждением"));
            }
        }
    }

    private static void CheckConflicts(DoctorReport report)
    {
        const string section = "Конфликты";
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pak in report.Paks.Where(p => p.EntryCount is not null))
        {
            foreach (var path in pak.Paths)
            {
                if (!map.TryGetValue(path, out var list))
                {
                    list = new List<string>(2);
                    map[path] = list;
                }

                list.Add(pak.FileName);
            }
        }

        foreach (var pair in map.Where(p => p.Value.Count > 1))
        {
            report.Conflicts.Add(new ConflictInfo(pair.Key, pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        if (report.Conflicts.Count == 0)
        {
            report.Lines.Add(new DiagLine(DiagLevel.Ok, section, "пересекающихся путей между паками нет"));
            return;
        }

        report.Lines.Add(new DiagLine(
            DiagLevel.Warn,
            section,
            $"один и тот же путь лежит в нескольких паках: {report.Conflicts.Count} шт (предполагается, что выигрывает пак с меньшим номером — это ещё проверяется в игре, риск R1)"));
        foreach (var conflict in report.Conflicts.Take(3))
        {
            report.Lines.Add(new DiagLine(DiagLevel.Warn, section, $"{conflict.Path} → {string.Join(" + ", conflict.Paks)}"));
        }
    }

    private static void CheckRankedReadiness(DoctorReport report, DotaInstallation install)
    {
        const string section = "Готовность";
        var checks = new List<(bool Ok, string Text)>
        {
            (install.IsValid, "установка найдена"),
            (install.ModFolderLayeredOnDota, "папка модов подключена штатно (LayeredOnMod)"),
            (!report.GameRunning, "игра закрыта"),
            (report.SignatureMismatched.Count == 0, "подписанные файлы Valve не изменены"),
            (report.Paks.All(p => p.BlockedCount == 0), "в существующих паках нет файлов категории C"),
            (install.RecommendedFreeSlot is not null, "есть свободный слот для мода"),
            (File.Exists(install.MainPakDir), "основной пак игры на месте"),
        };

        foreach (var (ok, text) in checks)
        {
            report.Lines.Add(new DiagLine(ok ? DiagLevel.Ok : DiagLevel.Error, section, text));
        }

        report.Lines.Add(new DiagLine(
            DiagLevel.Info,
            section,
            "моды — локальные файлы: сервер присылает только путь к модели, поэтому подмена видна в обычных и рейтинговых матчах только тебе"));
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} МБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} КБ",
        _ => $"{bytes} Б",
    };
}
