using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Uninstaller = Wibe.Installer.Program.Uninstaller;
using SteamProbe = Wibe.Installer.Program.Steam;
using InstallerCore = Wibe.Installer.Program.Installer;

namespace Wibe.Installer;

/// <summary>
/// Окно установщика: WebView2 с нашей тёмной разметкой. Без системных TextBox/ComboBox/CheckBox —
/// интерфейс целиком HTML/CSS, футер прибит к низу и не обрезается ни на одном DPI.
/// Если WebView2 нет на машине, показывается аккуратный резервный OwnerDraw-экран.
/// </summary>
public sealed class InstallerForm : Form
{
    private readonly Program.Options _options;
    private WebView2? _web;
    private bool _ready;
    private Point _dragFrom;
    private bool _dragging;
    private volatile bool _busy;

    public InstallerForm(Program.Options options)
    {
        _options = options;
        Text = Program.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 580);
        MinimumSize = new Size(720, 540);
        BackColor = Color.FromArgb(18, 20, 24);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;

        Load += async (_, _) => await StartWebUI();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // старый Windows — углы останутся прямыми
        }
    }

    private async Task StartWebUI()
    {
        try
        {
            var data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Program.ProductName, "WebView2Installer");
            Directory.CreateDirectory(data);
            var environment = await CoreWebView2Environment.CreateAsync(null, data);
            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(18, 20, 24) };
            Controls.Add(_web);
            await _web.EnsureCoreWebView2Async(environment);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.WebMessageReceived += OnMessage;
            _web.NavigateToString(InstallerUi.Html);
            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                _ready = true;
                await SendInit();
            };
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException)
        {
            ShowFallback(ex.Message);
        }
    }

    private async Task SendInit()
    {
        var payload = new
        {
            version = Uninstaller.Version(),
            dir = _options.InstallDir,
            dota = _options.DotaPath ?? SteamProbe.FindDota(),
            cache = _options.CachePath,
            dirFree = FreeSpaceText(_options.InstallDir),
            disks = Disks(),
        };
        await Script($"window.icdk.init({JsonSerializer.Serialize(payload)});");
    }

    private List<object> Disks()
    {
        var list = new List<object>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var vault = Path.Combine(drive.RootDirectory.FullName, "ICDK_AssetVault");
            long ready = 0;
            if (Directory.Exists(vault))
            {
                try
                {
                    ready = Directory.EnumerateFiles(vault, "*.glb", SearchOption.AllDirectories)
                        .Take(20_000)
                        .Sum(f => new FileInfo(f).Length);
                }
                catch (IOException)
                {
                    ready = 0;
                }
            }

            list.Add(new
            {
                letter = drive.Name.TrimEnd('\\'),
                path = vault,
                total = drive.TotalSize,
                free = drive.AvailableFreeSpace,
                ready,
            });
        }

        return list;
    }

    private async Task Script(string script)
    {
        if (_web?.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (COMException)
        {
            // окно уже закрыто
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // только JSON: TryGetWebMessageAsString на объекте бросает исключение
        var message = InstallerBridge.Parse(e.WebMessageAsJson);
        if (message is null)
        {
            return;
        }

        switch (message.Type)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "close":
                Close();
                break;
            case "drag":
                BeginDrag();
                break;
            case "dir":
                _options.Remember(message.Path ?? message.Dir ?? _options.InstallDir, _options.CachePath,
                    _options.DotaPath, true, true, true);
                _ = Script($"window.icdk.dirFree({JsonSerializer.Serialize(FreeSpaceText(message.Path ?? message.Dir ?? string.Empty))});");
                break;
            case "browse":
                BrowseInstallDir();
                break;
            case "pickDota":
                BrowseDota();
                break;
            case "cache":
                _options.Remember(_options.InstallDir, message.Path ?? _options.CachePath, _options.DotaPath, true, true, true);
                break;
            case "install":
                _ = InstallAsync(message);
                break;
        }
    }

    private void BrowseInstallDir()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _options.InstallDir, Description = "Папка установки ICDK" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _options.Remember(dialog.SelectedPath, _options.CachePath, _options.DotaPath, true, true, true);
        _ = Script($"document.getElementById('dir').value={JsonSerializer.Serialize(dialog.SelectedPath)};" +
                   $"window.icdk.dirFree({JsonSerializer.Serialize(FreeSpaceText(dialog.SelectedPath))});");
    }

    private void BrowseDota()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _options.DotaPath ?? string.Empty, Description = "Папка dota 2 beta" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _options.Remember(_options.InstallDir, _options.CachePath, dialog.SelectedPath, true, true, true);
        _ = Script($"document.getElementById('dota').value={JsonSerializer.Serialize(dialog.SelectedPath)};" +
                   "document.getElementById('dota-state').textContent='Выбрана: ' + " + JsonSerializer.Serialize(dialog.SelectedPath) + ";");
    }

    private async Task InstallAsync(InstallerBridge.Message message)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        var options = new Program.Options();
        options.Remember(
            string.IsNullOrWhiteSpace(message.Dir) ? _options.InstallDir : message.Dir!,
            string.IsNullOrWhiteSpace(message.Cache) ? _options.CachePath : message.Cache!,
            string.IsNullOrWhiteSpace(message.Dota) ? null : message.Dota,
            message.Desktop,
            message.StartMenu,
            message.Run);

        try
        {
            await Task.Run(() => InstallerCore.Install(options, (value, text) =>
            {
                _ = Script($"window.icdk.progress({value.ToString("0.###", CultureInfo.InvariantCulture)}, {JsonSerializer.Serialize(text)});");
            }));
            await Script("window.icdk.done();");
            await Task.Delay(800);
            Close();
        }
        catch (Exception ex)
        {
            await Script($"window.icdk.fail({JsonSerializer.Serialize(ex.Message)});");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Свободное место именно того диска, который вписан в поле пути.</summary>
    public static string FreeSpaceText(string path)
    {
        try
        {
            var root = Path.GetPathRoot(string.IsNullOrWhiteSpace(path) ? "C:\\" : path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return string.Empty;
            }

            var drive = new DriveInfo(root);
            var gb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            return $"Свободно {gb:0,0.0} ГБ на {drive.Name.TrimEnd('\\')}";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void BeginDrag()
    {
        _dragging = true;
        _dragFrom = PointToClient(Cursor.Position);
        var timer = new System.Windows.Forms.Timer { Interval = 8 };
        timer.Tick += (_, _) =>
        {
            if (!_dragging || (Control.MouseButtons & MouseButtons.Left) == 0)
            {
                _dragging = false;
                timer.Stop();
                timer.Dispose();
                return;
            }

            var now = PointToClient(Cursor.Position);
            Location = new Point(Location.X + now.X - _dragFrom.X, Location.Y + now.Y - _dragFrom.Y);
        };
        timer.Start();
    }

    /// <summary>Резервный экран, если WebView2 на машине не установлен.</summary>
    private void ShowFallback(string reason)
    {
        Controls.Clear();
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 24) };
        panel.Paint += (_, e) =>
        {
            using var glow = new SolidBrush(Color.FromArgb(60, 220, 38, 38));
            e.Graphics.FillEllipse(glow, 30, 26, 46, 46);
            using var title = new Font("Segoe UI Semibold", 14f);
            using var sub = new Font("Segoe UI", 9.5f);
            e.Graphics.DrawString("Установка ICDK", title, Brushes.White, 92, 30);
            e.Graphics.DrawString("Лаунчер косметики и 3D-примерочная Dota 2", sub, new SolidBrush(Color.FromArgb(154, 163, 173)), 94, 56);
            e.Graphics.DrawString("WebView2 недоступен: " + reason, sub, new SolidBrush(Color.FromArgb(250, 204, 21)), 30, 100);
            e.Graphics.DrawString("Папка: " + _options.InstallDir, sub, Brushes.White, 30, 130);
            e.Graphics.DrawString("Кэш: " + _options.CachePath, sub, Brushes.White, 30, 152);
        };

        var install = new Button
        {
            Text = "Установить ICDK →",
            Size = new Size(210, 44),
            Location = new Point(ClientSize.Width - 240, ClientSize.Height - 64),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(220, 38, 38),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10.5f),
            Cursor = Cursors.Hand,
        };
        install.FlatAppearance.BorderSize = 0;
        install.Click += (_, _) => _ = InstallAsync(new InstallerBridge.Message("install", null, _options.InstallDir,
                _options.DotaPath, _options.CachePath, true, true, true));
        panel.Controls.Add(install);
        Controls.Add(panel);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
