namespace Wibe.Server.Launcher;

/// <summary>
/// Изолированный языковой контейнер мода: <c>game/dota_icdk/</c>. Русский текст и озвучка
/// остаются родными — официальные паки Valve и папка <c>resource/</c> приходят хардлинками
/// из <c>game/dota_russian/</c> (0 МБ копий), наш мод лежит поверх как <c>pak02_dir.vpk</c>,
/// карта — в <c>maps/dota.vpk</c>. Снятие мода — удаление одной папки.
/// </summary>
public static class LanguageMount
{
    /// <summary>
    /// Официальный резервный язык Steam. Проверено живым запуском: не-Steam имя (icdk)
    /// движок молча игнорирует, а <c>danish</c> монтирует <c>game/dota_danish/pak01.vpk</c>
    /// и наш <c>pak02.vpk</c> в список GAME search paths.
    /// </summary>
    public const string Language = "danish";

    public const string FolderName = "dota_" + Language;

    public static string Folder(string gameRoot) => Path.Combine(gameRoot, FolderName);

    /// <summary>
    /// Наш собранный мод. Официальные паки Valve остаются в контейнере под родными именами
    /// <c>pak01_*</c> (их оглавление привязано к базовому имени), поэтому мод идёт следующим
    /// номером: <c>pak02_dir.vpk</c> монтируется поверх <c>pak01_dir.vpk</c>. Наш билдер пишет
    /// один dir-файл со встроенными данными — внешних томов у него нет.
    /// </summary>
    public static string ModPakPath(string gameRoot) => Path.Combine(Folder(gameRoot), "pak02_dir.vpk");

    public static string MapPath(string gameRoot) => Path.Combine(Folder(gameRoot), "maps", "dota.vpk");

    /// <summary>Аргумент запуска игры.</summary>
    public static string LaunchArgument => "-language " + Language;

    /// <summary>
    /// Собирает чистый контейнер: хардлинки официальной русской локализации, наш мод и карта.
    /// Возвращает список того, что реально связали — для лога и теста.
    /// </summary>
    /// <summary>
    /// Папка языкового контейнера Valve (dota_russian, dota_danish…). Базовая <c>game/dota</c>
    /// таковой не является: её паки трогать нельзя никогда.
    /// </summary>
    public static bool IsLanguageFolder(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.StartsWith("dota_", StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> Mount(string gameRoot, string russianDir, string? modPak, string? mapPak)
    {
        var target = Folder(gameRoot);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(target, "maps"));
        var linked = new List<string>();

        // Только языковая папка (dota_russian…). game/dota — базовая игра, её паки
        // не копируются и не удаляются ни при каких условиях.
        if (!IsLanguageFolder(russianDir))
        {
            russianDir = string.Empty;
        }

        // Какие официальные паки должны лежать в контейнере СЕЙЧАС.
        var official = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(russianDir))
        {
            foreach (var file in Directory.EnumerateFiles(russianDir, "pak01*.vpk"))
            {
                official.Add(Path.GetFileName(file));
            }
        }

        // Контейнер полностью наш: всё лишнее (старые тома, случайные линки на базовые паки
        // игры) удаляется — это только ссылки, сами файлы Valve не трогаются. Именно такие
        // лишние pak02_135.vpk / pak01_6xx.vpk и валили движок.
        foreach (var stale in Directory.EnumerateFiles(target, "pak*.vpk"))
        {
            var name = Path.GetFileName(stale);
            if (string.Equals(name, "pak02_dir.vpk", StringComparison.OrdinalIgnoreCase) || official.Contains(name))
            {
                continue;
            }

            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // занятый файл удалится при следующем монтировании
            }
        }

        if (Directory.Exists(russianDir))
        {
            // Официальные паки Valve линкуются под РОДНЫМИ именами: pak01_dir.vpk и все тома
            // pak01_*.vpk. Переименование в pak02* ломало внутренний индекс томов и давало
            // FATAL ERROR: Error reading from loaded packed store "...\pak02_135.vpk".
            foreach (var name in official.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var from = Path.Combine(russianDir, name);
                var to = Path.Combine(target, name);
                if (HardLink(from, to))
                {
                    linked.Add(name);
                }
            }

            var resource = Path.Combine(russianDir, "resource");
            if (Directory.Exists(resource))
            {
                foreach (var file in Directory.EnumerateFiles(resource, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(target, "resource", Path.GetRelativePath(resource, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (HardLink(file, to))
                    {
                        linked.Add("resource/" + Path.GetFileName(file));
                    }
                }
            }
        }

        if (modPak is not null && File.Exists(modPak))
        {
            // File.Copy(overwrite) пишет СКВОЗЬ уже существующий хардлинк и портит чужой файл.
            // Поэтому сначала снимаем запись каталога, потом копируем в новую.
            var destination = ModPakPath(gameRoot);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Copy(modPak, destination, true);
            linked.Add("pak02_dir.vpk");
        }

        if (mapPak is not null && File.Exists(mapPak))
        {
            var destination = MapPath(gameRoot);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Copy(mapPak, destination, true);
            linked.Add("maps/dota.vpk");
        }

        return linked;
    }

    // ── локализация: русский интерфейс при -language danish ─────────────────────

    /// <summary>
    /// Файлы локализации .txt с диска лежат НИЖЕ .vpk в таблице [Filesystem] и движком
    /// игнорируются. Поэтому русские строки переименовываются в <c>*_danish.txt</c>,
    /// заголовок <c>"Language"</c> меняется на danish и всё упаковывается внутрь нашего
    /// pak02_dir.vpk — он монтируется поверх официального датского перевода.
    /// </summary>
    public static Dictionary<string, byte[]> CollectLocalization(string gameRoot)
    {
        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ICDK", "LocalizationCache", "v3-" + Language);
        var stamp = LocalizationStamp(gameRoot);

        var cached = LoadCached(cache, stamp);
        if (cached is not null)
        {
            return cached;
        }

        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pak in LocalizationPaks(gameRoot))
        {
            var files = Wibe.Core.Vpk.VpkReader.ReadMatching(pak,
                p => p.EndsWith("_russian.txt", StringComparison.OrdinalIgnoreCase));
            foreach (var (path, bytes) in files)
            {
                var alias = path.Replace("_russian.", "_" + Language + ".", StringComparison.OrdinalIgnoreCase);
                map[alias] = RetargetLanguage(bytes);
            }
        }

        SaveCache(cache, stamp, map);
        return map;
    }

    /// <summary>
    /// Вливает локализацию в наш пак (по умолчанию pak02_dir.vpk). Возвращает число файлов,
    /// 0 — уже влито (sidecar-метка), -1 — пак не нашего формата (тогда нужен отдельный pak03).
    /// </summary>
    public static int EmbedLocalization(string pakPath, IReadOnlyDictionary<string, byte[]> localization)
    {
        if (localization.Count == 0)
        {
            return 0;
        }

        if (!File.Exists(pakPath))
        {
            // мода ещё нет (чистая установка) — локализация пойдёт отдельным паком
            return -1;
        }

        var sidecar = pakPath + ".loc";
        if (File.Exists(sidecar))
        {
            try
            {
                if (File.ReadAllText(sidecar).Trim() == EmbedStamp(pakPath, localization))
                {
                    return 0;
                }
            }
            catch (IOException)
            {
                // перезапишем метку после вливания
            }
        }

        var entries = Wibe.Core.Vpk.VpkReader.ReadAll(pakPath);
        if (entries.Count == 0)
        {
            return -1;
        }

        foreach (var (path, bytes) in localization)
        {
            entries[path] = bytes;
        }

        var temp = pakPath + ".merge";
        try
        {
            Wibe.Core.Vpk.VpkWriter.Write(temp, entries);
            var check = Wibe.Core.Vpk.VpkReader.ReadAll(temp);
            var key = localization.Keys.First();
            if (check.Count == 0 || !check.ContainsKey(key) || check[key].Length == 0)
            {
                File.Delete(temp);
                return -1;
            }

            File.Move(temp, pakPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "icdk_loc.log"),
                    DateTime.Now.ToString("u") + " embed failed: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine);
            }
            catch (IOException)
            {
            }

            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }

            return -1;
        }

        try
        {
            File.WriteAllText(sidecar, EmbedStamp(pakPath, localization));
        }
        catch (IOException)
        {
            // метка — только ускорение для следующего старта
        }

        return localization.Count;
    }

    /// <summary>
    /// Отдельный пак только с локализацией — запасной путь, если мод-пак занят или чужого формата.
    /// Первый свободный номер, не занятый модом.
    /// </summary>
    public static string WriteStandaloneLocalization(
        string gameRoot,
        IReadOnlyDictionary<string, byte[]> localization,
        IEnumerable<string>? reserved = null)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (reserved is not null)
        {
            foreach (var path in reserved)
            {
                taken.Add(Path.GetFileName(path));
            }
        }

        var folder = Folder(gameRoot);
        Directory.CreateDirectory(folder);
        for (var number = 3; number <= 9; number++)
        {
            var name = $"pak{number:00}_dir.vpk";
            if (taken.Contains(name))
            {
                continue;
            }

            var pak = Path.Combine(folder, name);
            Wibe.Core.Vpk.VpkWriter.Write(pak, localization);
            return pak;
        }

        throw new IOException("нет свободного слота пака для локализации");
    }

    private static string EmbedStamp(string pakPath, IReadOnlyDictionary<string, byte[]> localization)
    {
        var info = new FileInfo(pakPath);
        long bytes = 0;
        foreach (var value in localization.Values)
        {
            bytes += value.Length;
        }

        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{localization.Count}:{bytes}";
    }

    private static IEnumerable<string> LocalizationPaks(string gameRoot)
    {
        // базовый пак игры: там лежат все языки, включая русский
        var basePak = Path.Combine(gameRoot, "dota", "pak01_dir.vpk");
        if (File.Exists(basePak))
        {
            yield return basePak;
        }

        // языковые паки Valve (dota_russian…), но не наш собственный контейнер
        var own = Path.GetFullPath(Folder(gameRoot));
        foreach (var folder in Directory.EnumerateDirectories(gameRoot, "dota_*"))
        {
            if (string.Equals(Path.GetFullPath(folder), own, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pak = Path.Combine(folder, "pak01_dir.vpk");
            if (File.Exists(pak))
            {
                yield return pak;
            }
        }
    }

    private static string LocalizationStamp(string gameRoot)
    {
        var parts = new List<string>();
        foreach (var pak in LocalizationPaks(gameRoot))
        {
            var info = new FileInfo(pak);
            parts.Add(info.Length + ":" + info.LastWriteTimeUtc.Ticks);
        }

        return string.Join('|', parts);
    }

    private static Dictionary<string, byte[]>? LoadCached(string cache, string stamp)
    {
        try
        {
            var marker = Path.Combine(cache, "stamp.txt");
            if (!File.Exists(marker) || File.ReadAllText(marker) != stamp)
            {
                return null;
            }

            var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(cache, "*.txt", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), "stamp.txt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                map[Path.GetRelativePath(cache, file).Replace('\\', '/')] = File.ReadAllBytes(file);
            }

            return map.Count > 0 ? map : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveCache(string cache, string stamp, Dictionary<string, byte[]> map)
    {
        try
        {
            Directory.CreateDirectory(cache);
            foreach (var (path, bytes) in map)
            {
                var file = Path.Combine(cache, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
            }

            File.WriteAllText(Path.Combine(cache, "stamp.txt"), stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // кэш — ускорение, не условие
        }
    }



    /// <summary>
    /// Русские тексты интерфейса лежат с суффиксом <c>_russian</c>. Движок под нашим языком
    /// ищет <c>_&lt;lang&gt;</c>, поэтому файлы один раз извлекаются из пака игры,
    /// переименовываются и кэшируются, а в контейнер попадают копией за миллисекунды.
    /// </summary>
    public static List<string> EnsureLocalization(string gameRoot)
    {
        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ICDK", "LocalizationCache", "v1-" + Language);
        var made = new List<string>();
        Directory.CreateDirectory(cache);

        if (Directory.GetFiles(cache, "*", SearchOption.AllDirectories).Length == 0)
        {
            ExtractRussianTexts(cache);
        }

        var target = Path.Combine(Folder(gameRoot), "resource");
        foreach (var file in Directory.GetFiles(cache, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(cache, file);
            var to = Path.Combine(Folder(gameRoot), rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try
            {
                File.Copy(file, to, true);
                made.Add(rel);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // тексты не критичны для монтажа моделей
            }
        }

        _ = target;
        return made;
    }

    private static void ExtractRussianTexts(string cache)
    {
        var install = Wibe.Core.Dota.DotaInstallation.Detect(out _);
        var exe = Wibe.Core.Tools.Source2ViewerCli.Locate();
        if (install is null || exe is null)
        {
            return;
        }

        // тексты лежат и в базовом паке игры, и в русском языковом паке — проверяем оба
        var paks = new List<string> { install.MainPakDir };
        var russianPak = Path.Combine(Path.GetDirectoryName(install.MainPakDir)!, "..", "dota_russian", "pak01_dir.vpk");
        if (File.Exists(russianPak))
        {
            paks.Add(Path.GetFullPath(russianPak));
        }

        foreach (var pak in paks)
        foreach (var folder in new[] { "resource", "panorama/localization" })
        {
            var sources = paks;
            List<string> entries;
            try
            {
                entries = VpkFiles(pak, folder);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                continue;
            }

            foreach (var entry in entries.Where(e => e.Contains("_russian.", StringComparison.OrdinalIgnoreCase)).Take(400))
            {
                var temp = Path.Combine(cache, "_tmp");
                Directory.CreateDirectory(temp);
                try
                {
                    var start = new System.Diagnostics.ProcessStartInfo(exe)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    foreach (var arg in new[] { "-i", pak, "-f", entry, "-o", temp, "-q" })
                    {
                        start.ArgumentList.Add(arg);
                    }

                    using var process = System.Diagnostics.Process.Start(start);
                    if (process is null)
                    {
                        continue;
                    }

                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(30_000);
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                {
                    continue;
                }
            }
        }

        var tempDir = Path.Combine(cache, "_tmp");
        if (!Directory.Exists(tempDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(tempDir, file)
                .Replace("_russian.", "_" + Language + ".", StringComparison.OrdinalIgnoreCase);
            var to = Path.Combine(cache, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try
            {
                File.WriteAllBytes(to, RetargetLanguage(File.ReadAllBytes(file)));
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // пропускаем отдельный файл
            }
        }

        try
        {
            Directory.Delete(tempDir, true);
        }
        catch (IOException)
        {
            // временная папка уберётся позже
        }

    }

    private static List<string> VpkFiles(string pak, string folder)
    {
        var exe = Wibe.Core.Tools.Source2ViewerCli.Locate()!;
        var start = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--vpk_list");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(pak);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            return new List<string>();
        }

        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        return text.Split((char)10)
            .Select(line => line.Split(" CRC:")[0].Trim())
            .Where(line => line.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
    /// <summary>Ссылка запуска Steam с нашим языком и пользовательскими параметрами.</summary>
    public static string SteamUri(string customArgs = "")
    {
        var extra = (customArgs ?? string.Empty).Trim();
        var tail = extra.Length == 0 ? LaunchArgument : LaunchArgument + " " + extra;
        return "steam://rungameid/570//" + tail;
    }

    /// <summary>
    /// Боевой перенос собранного мода в чистый контейнер: pak06 из языковой папки Valve
    /// становится <c>dota_icdk/pak02_dir.vpk</c> (поверх официального pak01_dir.vpk), карта —
    /// <c>dota_icdk/maps/dota.vpk</c>, русская локализация линкуется дважды: как есть и под именем
    /// нашего языка (иначе в меню появятся сырые токены вида #DOTA_Play).
    /// </summary>
    public static List<string> Deploy(string gameRoot, string russianDir, string? customArgs = "", bool embedLocalization = true)
    {
        var deployed = new List<string>();
        var target = Folder(gameRoot);
        Directory.CreateDirectory(Path.Combine(target, "maps"));

        // официальные паки и resource/ русской локализации — хардлинками
        deployed.AddRange(Mount(gameRoot, russianDir, null, null));
        DuplicateLocalization(target, Language);

        var pak06 = Path.Combine(russianDir, "pak06_dir.vpk");
        var modTarget = ModPakPath(gameRoot);
        var modCopied = false;
        if (File.Exists(pak06))
        {
            Directory.CreateDirectory(target);
            modCopied = TryReplace(pak06, modTarget);
            if (modCopied)
            {
                deployed.Add("pak02_dir.vpk");
            }
            else
            {
                // pak02 занят (Steam/антивирус): мод кладём следующим паком, он выше по порядку
                var fallback = Path.Combine(target, "pak03_dir.vpk");
                if (TryReplace(pak06, fallback))
                {
                    modTarget = fallback;
                    modCopied = true;
                    deployed.Add("pak03_dir.vpk (мод)");
                }
            }
        }

        var map = Path.Combine(russianDir, "maps", "dota.vpk");
        if (File.Exists(map))
        {
            Directory.CreateDirectory(Path.Combine(target, "maps"));
            var destination = MapPath(gameRoot);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Copy(map, destination, true);
            deployed.Add("maps/dota.vpk");
        }

        if (embedLocalization)
        {
            deployed.AddRange(EmbedInto(gameRoot, modTarget, modCopied));
        }

        // старые хвосты в языковой папке Valve больше не нужны
        deployed.AddRange(CleanLegacy(russianDir).Select(r => "removed:" + r));
        _ = customArgs;
        return deployed;
    }

    /// <summary>Вливает локализацию в мод-пак; если он занят или чужого формата — отдельным паком.</summary>
    public static List<string> EmbedInto(string gameRoot, string modPak, bool modCopied)
    {
        var deployed = new List<string>();
        try
        {
            var localization = CollectLocalization(gameRoot);
            if (localization.Count == 0)
            {
                return deployed;
            }

            var target = modCopied ? modPak : ModPakPath(gameRoot);
            var merged = EmbedLocalization(target, localization);
            if (merged > 0)
            {
                deployed.Add($"loc:{merged}");
                // отдельный пак локализации больше не нужен
                var extra = Path.Combine(Folder(gameRoot), "pak03_dir.vpk");
                if (File.Exists(extra) && !string.Equals(extra, target, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(extra);
                    }
                    catch (IOException)
                    {
                    }
                }

                return deployed;
            }

            if (merged < 0)
            {
                deployed.Add("loc:" + Path.GetFileName(WriteStandaloneLocalization(
                    gameRoot, localization, new[] { target })));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            deployed.Add("loc:ошибка " + ex.Message);
        }

        return deployed;
    }

    /// <summary>Заменить файл с повтором: игру/антивирус может держать приёмник пару секунд.</summary>
    public static bool TryReplace(string source, string destination)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Copy(source, destination, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }

        return false;
    }


    /// <summary>
    /// Внутри текстов локализации Valve пишет <c>"Language" "russian"</c>. При нашем языке
    /// этот заголовок обязан совпадать с <c>-language</c>, иначе парсер отклонит блок.
    /// Кодировка определяется по BOM: UTF-16LE или UTF-8.
    /// </summary>
    public static byte[] RetargetLanguage(byte[] bytes)
    {
        if (bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            var text16 = System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            var fixed16 = StripGenders(Swap(text16));
            return new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes(fixed16)).ToArray();
        }

        var offset = bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var text = System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        var fixedText = StripGenders(Swap(text));
        var body = System.Text.Encoding.UTF8.GetBytes(fixedText);
        return offset == 3 ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }

    private static readonly System.Text.RegularExpressions.Regex GenderKey = new(
        "^(\\s*)\"([^\"]+):[nmf]\"",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex GenderValueHead = new(
        "\"#\\|[nmf]\\|#",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex GenderValueAny = new(
        "#\\|[nmf]\\|#",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Русские строки используют родовые варианты Valve: ключ <c>"npc_dota_hero_antimage:n"</c>
    /// и значение <c>"#|m|#Anti-Mage"</c>. Язык danish родов не знает, и валидатор движка
    /// выбрасывает такие токены целиком (<c>LOCFAIL … uses a gender not valid</c>). Поэтому суффикс
    /// ключа и маркер значения снимаются, а из одинаковых после снятия ключей остаётся первый.
    /// </summary>
    private static string StripGenders(string text)
    {
        if (text.IndexOf("#|", StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var end = text.IndexOf('\n', index);
            if (end < 0)
            {
                end = text.Length;
            }

            var line = text.Substring(index, end - index);
            index = end + 1;

            var match = GenderKey.Match(line);
            if (match.Success)
            {
                if (!seen.Add(match.Groups[2].Value))
                {
                    continue;
                }

                var rest = GenderValueHead.Replace(line.Substring(match.Length).TrimStart(), "\"", 1);
                rest = GenderValueAny.Replace(rest, string.Empty);
                line = match.Groups[1].Value + "\"" + match.Groups[2].Value + "\"" +
                       (rest.Length > 0 ? " " + rest : string.Empty);
            }
            else if (line.IndexOf("#|", StringComparison.Ordinal) >= 0)
            {
                line = GenderValueAny.Replace(line, string.Empty);
            }

            output.Append(line);
            if (end < text.Length)
            {
                output.Append('\n');
            }
        }

        return output.ToString();
    }

    private static string Swap(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text,
            "([\"']Language[\"']\\s*[\"'])([A-Za-z_]+)([\"'])",
            m => m.Groups[1].Value + Language + m.Groups[3].Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Копия русских файлов локализации под именем нашего языка.</summary>
    public static List<string> DuplicateLocalization(string containerDir, string language)
    {
        var made = new List<string>();
        var resource = Path.Combine(containerDir, "resource");
        if (!Directory.Exists(resource))
        {
            return made;
        }

        foreach (var file in Directory.EnumerateFiles(resource, "*_russian.*"))
        {
            var name = Path.GetFileName(file);
            var alias = Path.Combine(resource, name.Replace("_russian", "_" + language, StringComparison.OrdinalIgnoreCase));
            if (File.Exists(alias))
            {
                continue;
            }

            try
            {
                File.WriteAllBytes(alias, RetargetLanguage(File.ReadAllBytes(file)));
                made.Add(Path.GetFileName(alias));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // локализация не критична для монтажа мода
            }
        }

        return made;
    }
    /// <summary>Снять мод: удаляется только наша папка, `dota_russian` не трогается.</summary>
    public static bool Unmount(string gameRoot)
    {
        var target = Folder(gameRoot);
        if (!Directory.Exists(target))
        {
            return false;
        }

        Directory.Delete(target, true);
        return true;
    }

    /// <summary>
    /// Убирает наши старые хвосты из языковой папки Valve: <c>pak06*</c> и подменённую карту.
    /// Родной <c>pak01*</c> от Valve не трогаем никогда.
    /// </summary>
    public static List<string> CleanLegacy(string russianDir)
    {
        var removed = new List<string>();
        if (!Directory.Exists(russianDir) || !IsLanguageFolder(russianDir))
        {
            return removed;
        }

        foreach (var file in Directory.EnumerateFiles(russianDir, "pak06*.vpk"))
        {
            File.Delete(file);
            removed.Add(Path.GetFileName(file));
        }

        var map = Path.Combine(russianDir, "maps", "dota.vpk");
        if (File.Exists(map))
        {
            File.Delete(map);
            removed.Add("maps/dota.vpk");
        }

        return removed;
    }

    /// <summary>Хардлинк, а если файловая система не даёт — обычная копия.</summary>
    private static bool HardLink(string from, string to)
    {
        try
        {
            if (File.Exists(to))
            {
                File.Delete(to);
            }

            if (NativeHardLink.Create(to, from))
            {
                return true;
            }

            File.Copy(from, to, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
