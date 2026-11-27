using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Wibe.Installer;

public static class Program
{
    internal const string ProductName = "ICDK";
    internal const string Title = "Установка ICDK";
    private const string TrailerMagic = "ICDKINST";
        private const string RawMagic = "ICDKRAW1";
    private const int TrailerSize = 16;

    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.Uninstall)
        {
            return Uninstaller.Run(options);
        }

        try
        {
            if (options.Silent)
            {
                Installer.Install(options, null);
                return 0;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new InstallerForm(options));
            return 0;
        }
        catch (Exception ex)
        {
            // молчаливая установка без лога неотлаживаема: пишем причину в %TEMP%
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "ICDK_setup_error.log"),
                    DateTime.Now.ToString("u") + " " + ex + Environment.NewLine);
            }
            catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
            {
                // лог — удобство, а не условие
            }

            if (!options.Silent)
            {
                MessageBox.Show(ex.Message, "ICDK", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    public sealed class Options
    {
        public string InstallDir { get; private set; } = DefaultInstallDir();
        public string CachePath { get; private set; } = DefaultCachePath();
        public string? DotaPath { get; private set; }
        public bool DesktopShortcut { get; private set; } = true;
        public bool StartMenuShortcut { get; private set; } = true;
        public bool RunAfter { get; private set; } = true;
        public bool Silent { get; private set; }
        public bool Uninstall { get; private set; }
        public bool DirExplicit { get; private set; }

        public static string DefaultInstallDir() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

        public static string DefaultCachePath() => ReadyVault() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName, "AssetVault");

        /// <summary>
        /// Готовый AssetVault на любом диске: <c>X:\ICDK_AssetVault</c>. Если таких несколько —
        /// побеждает самый наполненный (модели в meshes / temp/meshes), а не первый по алфавиту.
        /// </summary>
        public static string? ReadyVault()
        {
            string? best = null;
            long bestScore = -1;
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (IOException)
            {
                return null;
            }

            foreach (var drive in drives)
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    {
                        continue;
                    }

                    var candidate = Path.Combine(drive.RootDirectory.FullName, "ICDK_AssetVault");
                    if (!Directory.Exists(candidate))
                    {
                        continue;
                    }

                    long score = 0;
                    var meshes = Path.Combine(candidate, "meshes");
                    if (Directory.Exists(meshes))
                    {
                        score += Directory.EnumerateFiles(meshes, "*.glb", SearchOption.TopDirectoryOnly).Take(5_000).Count();
                    }

                    var ready = Path.Combine(candidate, "temp", "meshes");
                    if (Directory.Exists(ready))
                    {
                        score += Directory.EnumerateDirectories(ready, "*", SearchOption.TopDirectoryOnly).Take(20_000).Count();
                    }

                    if (score > bestScore)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // нечитаемый диск пропускаем
                }
            }

            return best;
        }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;
                switch (arg.ToLowerInvariant())
                {
                    case "--silent":
                    case "/s":
                        options.Silent = true;
                        break;
                    case "--uninstall":
                        options.Uninstall = true;
                        break;
                    case "--dir":
                        options.DirExplicit = true;
                        options.InstallDir = Next() ?? options.InstallDir;
                        break;
                    case "--cache":
                        options.CachePath = Next() ?? options.CachePath;
                        break;
                    case "--dota":
                        options.DotaPath = Next();
                        break;
                    case "--no-desktop":
                        options.DesktopShortcut = false;
                        break;
                    case "--no-startmenu":
                        options.StartMenuShortcut = false;
                        break;
                    case "--no-run":
                        options.RunAfter = false;
                        break;
                }
            }

            return options;
        }

        public void Remember(string installDir, string cachePath, string? dota, bool desktop, bool startMenu, bool run)
        {
            InstallDir = installDir;
            CachePath = cachePath;
            DotaPath = dota;
            DesktopShortcut = desktop;
            StartMenuShortcut = startMenu;
            RunAfter = run;
        }
    }

    public static class Payload
    {
        /// <summary>Длина сжатого payload и его смещение относительно конца файла.</summary>
        public static long Length(string setupPath)
        {
            using var stream = OpenShared(setupPath);
            if (stream.Length < TrailerSize)
            {
                return 0;
            }

            stream.Seek(-TrailerSize, SeekOrigin.End);
            Span<byte> trailer = stackalloc byte[TrailerSize];
            stream.ReadExactly(trailer);
            var magic = Encoding.ASCII.GetString(trailer[8..16]);
            if (magic != TrailerMagic && magic != RawMagic)
            {
                return 0;
            }

            return BitConverter.ToInt64(trailer[..8]);
        }

        /// <summary>Payload записан без сжатия (magic ICDKRAW1).</summary>
        private static bool IsRaw(string setupPath)
        {
            try
            {
                using var stream = OpenShared(setupPath);
                if (stream.Length < TrailerSize)
                {
                    return false;
                }

                stream.Seek(-8, SeekOrigin.End);
                var magic = new byte[8];
                stream.ReadExactly(magic);
                return System.Text.Encoding.ASCII.GetString(magic) == RawMagic;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static bool HasPayload => Length(Environment.ProcessPath ?? string.Empty) > 0;

        /// <summary>
        /// Открыть свой же exe, не мешая антивирусу и проводнику читать его: File.OpenRead
        /// берёт монопольную блокировку и даёт «The process cannot access the file».
        /// </summary>
        private static FileStream OpenShared(string path) => new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        /// <summary>Потоковая распаковка payload в целевой файл с отчётом о прогрессе (0..1).</summary>
        public static void Extract(string targetFile, Action<double>? progress = null, CancellationToken token = default, string? setupPath = null)
        {
            var self = setupPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("не найден собственный файл установщика");
            var length = Length(self);
            if (length <= 0)
            {
                throw new InvalidOperationException("в установщике нет данных приложения");
            }

            using var source = OpenShared(self);
            var start = source.Length - TrailerSize - length;
            source.Seek(start, SeekOrigin.Begin);

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            var temp = targetFile + ".part";
            try
            {
                // хвост от прошлой неудачной попытки — наш, его можно смело убрать
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // занят — напишем в новое имя ниже
            }

            var raw = IsRaw(self);
            using (var compressed = new SubStream(source, length))
            using (var output = File.Create(temp))
            {
                if (raw)
                {
                    var buffer0 = new byte[1 << 20];
                    long done0 = 0;
                    int read0;
                    while ((read0 = compressed.Read(buffer0, 0, buffer0.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer0, 0, read0);
                        done0 += read0;
                        progress?.Invoke(Math.Clamp((double)done0 / length, 0, 0.999));
                    }
                }
                else
                {
                    using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                    var buffer = new byte[1 << 16];
                    int read;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        // исходный размер неизвестен заранее: оцениваем по сжатому потоку
                        progress?.Invoke(Math.Clamp((double)compressed.Position / length, 0, 0.995));
                    }
                }
            }

            // Переименование возможно только после закрытия потока: иначе Windows
            // держит файл .part и File.Move падает с IOException (это и ломало установку).
            // Антивирус/проводник/Defender могут держать целевой ICDK.exe десятки секунд
            // после закрытия — повторяем до 30 с, а не падаем сразу.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, targetFile, true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 60)
                {
                    token.ThrowIfCancellationRequested();
                    Thread.Sleep(500);
                }
            }

            progress?.Invoke(1);
        }

        private sealed class SubStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _length;
            private long _read;

            public SubStream(Stream inner, long length)
            {
                _inner = inner;
                _length = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _read; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var left = _length - _read;
                if (left <= 0)
                {
                    return 0;
                }

                var wanted = (int)Math.Min(count, left);
                var read = _inner.Read(buffer, offset, wanted);
                _read += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    public static class Installer
    {
        public static void Install(Options options, Action<double, string>? report)
        {
            report?.Invoke(0.02, "закрываю запущенный ICDK…");
            KillRunning();

            var target = Path.Combine(options.InstallDir, "ICDK.exe");
            Directory.CreateDirectory(options.InstallDir);

            report?.Invoke(0.08, "распаковываю приложение…");
            Payload.Extract(target, value => report?.Invoke(0.08 + value * 0.74, "распаковываю приложение…"));

            report?.Invoke(0.86, "записываю настройки…");
            WriteSettings(options);

            report?.Invoke(0.92, "создаю ярлыки…");
            if (options.DesktopShortcut)
            {
                Shortcuts.Create(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), target);
            }

            if (options.StartMenuShortcut)
            {
                Shortcuts.Create(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName), target);
            }

            report?.Invoke(0.96, "регистрирую в Windows…");
            Uninstaller.Register(target, options.InstallDir, Environment.ProcessPath);
            File.WriteAllText(Path.Combine(options.InstallDir, "install.json"), JsonSerializer.Serialize(new
            {
                dir = options.InstallDir,
                cache = options.CachePath,
                dota = options.DotaPath,
                desktop = options.DesktopShortcut,
                startMenu = options.StartMenuShortcut,
            }));

            if (options.RunAfter)
            {
                report?.Invoke(0.99, "запускаю ICDK…");
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = options.InstallDir });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // не запустилось — не повод валить установку
                }
            }

            report?.Invoke(1, "готово");
        }

        public static void KillRunning()
        {
            foreach (var process in Process.GetProcessesByName("ICDK"))
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // процесс уже закрылся
                }
            }
        }

        private static void WriteSettings(Options options)
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName, "UserData");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "settings.json");
            JsonObject settings;
            try
            {
                settings = File.Exists(file)
                    ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
            }
            catch (JsonException)
            {
                settings = new JsonObject();
            }

            settings["cachePath"] = options.CachePath;
            if (!string.IsNullOrWhiteSpace(options.DotaPath))
            {
                settings["dotaPath"] = options.DotaPath;
            }

            File.WriteAllText(file, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            try
            {
                Directory.CreateDirectory(options.CachePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // выбранный диск только для чтения — приложение возьмёт готовый кэш оттуда же
            }
        }
    }

    public static class Uninstaller
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ICDK";

        public static void Register(string exePath, string installDir, string? setupPath = null)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Key);
                if (key is null)
                {
                    return;
                }

                // Удалять умеет именно установщик (ICDK_Setup_vX.exe), а не exe приложения:
                // путь берём у запущенного файла, а не собираем по шаблону.
                var setup = string.IsNullOrWhiteSpace(setupPath) ? null : setupPath;
                key.SetValue("DisplayName", "ICDK — fitting room");
                key.SetValue("DisplayVersion", Version());
                key.SetValue("Publisher", "ICDK");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", exePath);
                if (setup is not null)
                {
                    key.SetValue("UninstallString", "\"" + setup + "\" --uninstall");
                    key.SetValue("QuietUninstallString", "\"" + setup + "\" --uninstall --silent");
                }

                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // без реестра установка всё равно рабочая
            }
        }

        /// <summary>Папка установки из записи «Программы и компоненты».</summary>
        private static string? InstallLocation()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key);
                return key?.GetValue("InstallLocation") as string;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        public static string Version() =>
            typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.4.2";

        public static int Run(Options options)
        {
            var silent = options.Silent;
            // --dir важнее всего; иначе — запись в реестре, иначе папка по умолчанию.
            var installDir = options.DirExplicit
                ? options.InstallDir
                : InstallLocation() ?? options.InstallDir;
            if (!options.DirExplicit)
            {
                foreach (var candidate in new[]
                         {
                             installDir,
                             options.InstallDir,
                             Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty,
                         })
                {
                    var info = string.IsNullOrWhiteSpace(candidate) ? null : Path.Combine(candidate, "install.json");
                    if (info is null || !File.Exists(info))
                    {
                        continue;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(info));
                        if (doc.RootElement.TryGetProperty("dir", out var dir) && dir.GetString() is { Length: > 0 } value)
                        {
                            installDir = value;
                            break;
                        }
                    }
                    catch (JsonException)
                    {
                        // битый install.json — пробуем следующий кандидат
                    }
                }
            }

            Installer.KillRunning();
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(Key, false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // ключа нет — и хорошо
            }

            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName),
                     })
            {
                var link = Path.Combine(folder, "ICDK.lnk");
                if (File.Exists(link))
                {
                    File.Delete(link);
                }
            }

            try
            {
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, true);
                }
            }
            catch (IOException)
            {
                return 1;
            }

            if (!silent)
            {
                MessageBox.Show("ICDK удалён. Кэш моделей остался на диске — его можно удалить вручную.",
                    "ICDK", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return 0;
        }
    }

    public static class Shortcuts
    {
        public static void Create(string folder, string target)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var link = Path.Combine(folder, "ICDK.lnk");
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type is null)
                {
                    return;
                }

                dynamic shell = Activator.CreateInstance(type)!;
                dynamic shortcut = shell.CreateShortcut(link);
                shortcut.TargetPath = target;
                shortcut.WorkingDirectory = Path.GetDirectoryName(target);
                shortcut.IconLocation = target;
                shortcut.Description = "ICDK fitting room";
                shortcut.Save();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // ярлык не критичен
            }
        }
    }

    public static class Steam
    {
        /// <summary>Ищет Dota 2 по libraryfolders.vdf всех библиотек Steam.</summary>
        public static string? FindDota()
        {
            foreach (var root in SteamRoots())
            {
                var libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                foreach (var library in Libraries(libraries).Prepend(Path.Combine(root, "steamapps")))
                {
                    var candidate = Path.Combine(library, "common", "dota 2 beta");
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> SteamRoots()
        {
            var roots = new List<string>();
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
                {
                    roots.Add(path.Replace('/', '\\'));
                }
            }

            foreach (var guess in new[] { @"C:\Program Files (x86)\Steam", @"Z:\Steam", @"D:\Steam" })
            {
                if (Directory.Exists(guess) && !roots.Contains(guess, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(guess);
                }
            }

            return roots;
        }

        private static IEnumerable<string> Libraries(string vdf)
        {
            var found = new List<string>();
            if (!File.Exists(vdf))
            {
                return found;
            }

            try
            {
                foreach (var line in File.ReadLines(vdf))
                {
                    var trimmed = line.Trim().Trim('"');
                    var value = trimmed.Split('"').LastOrDefault()?.TrimEnd('"');
                    if (value is { Length: > 2 } && value.Contains(":\\", StringComparison.Ordinal) || value is { Length: > 2 } && value[1] == ':')
                    {
                        var library = value.Replace("\\\\", "\\");
                        if (Directory.Exists(library))
                        {
                            found.Add(Path.Combine(library, "steamapps"));
                        }
                    }
                }
            }
            catch (IOException)
            {
                // библиотека не читается — пропускаем
            }

            return found;
        }
    }
}
