using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Wibe.Server.Launcher;

/// <summary>
/// Окно лаунчера: без системной рамки, с шапкой из самой страницы, скруглёнными углами Windows 11 и
/// нативным ресайзом за края. Внутри — WebView2 на системном рантайме: ни браузера, ни консоли,
/// ни вкладок. Если рантайма в системе нет, страница открывается в msedge --app.
/// </summary>
internal sealed class LauncherForm : Form
{
    public const string WindowTitle = "ICDK";

    private readonly WebView2 _web = new();
    private readonly Label _boot = new();
    private readonly NotifyIcon _tray;
    private ServerHost? _host;
    private Icon? _icon;

    public LauncherForm()
    {
        Text = WindowTitle;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(1380, 860);
        MinimumSize = new Size(1080, 680);
        BackColor = Color.FromArgb(0x16, 0x18, 0x1C);
        DoubleBuffered = true;
        KeyPreview = true;

        _icon = LoadIcon();
        if (_icon is not null)
        {
            Icon = _icon;
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1380, 860);
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);

        _boot.Dock = DockStyle.Fill;
        _boot.TextAlign = ContentAlignment.MiddleCenter;
        _boot.ForeColor = Color.FromArgb(0x9C, 0xA3, 0xAF);
        _boot.BackColor = Color.FromArgb(0x16, 0x18, 0x1C);
        _boot.Font = new Font("Segoe UI", 11f);
        _boot.Text = "ICDK загружается…";
        Controls.Add(_boot);

        _tray = BuildTray();

        Shown += async (_, _) => await StartAsync();
        FormClosing += OnClosing;
    }

    /// <summary>Второй запуск не открывает второе окно, а поднимает уже открытое.</summary>
    public static void BringExistingToFront() => NativeWindow.BringToFront(WindowTitle);

    private static Icon? LoadIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            if (stream is null)
            {
                return null;
            }

            using var loaded = new Icon(stream);
            return (Icon)loaded.Clone();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }

    private NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть ICDK", null, (_, _) => Restore());
        menu.Items.Add("Запустить Dota 2", null, (_, _) => Game(() => AppState.Instance.LaunchGame()));
        menu.Items.Add("Отключить моды", null, (_, _) => Game(() => AppState.Instance.RevertSession()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        var tray = new NotifyIcon
        {
            Text = "ICDK — примерочная Dota 2",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = _icon ?? SystemIcons.Application,
        };
        tray.DoubleClick += (_, _) => Restore();
        return tray;
    }

    private static void Game(Func<object> action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "ICDK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Restore()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp() => Close();

    private async Task StartAsync()
    {
        _host = ServerHost.Start();
        if (_host is null)
        {
            _boot.Text = "не удалось занять порт 17890–17990: закройте другой экземпляр ICDK";
            return;
        }

        var url = _host.Url;
        try
        {
            // Явный путь к рантайму: у пользователя он свой, а свой браузер мы не таскаем.
            var runtime = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrEmpty(runtime))
            {
                throw new InvalidOperationException("WebView2 Runtime не найден");
            }

            // постоянный профиль WebView2: иначе настройки UI слетают при каждом запуске
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ICDK", "WebView2Data");
            Directory.CreateDirectory(dataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            _web.Dock = DockStyle.Fill;
            _web.BackColor = Color.FromArgb(0x16, 0x18, 0x1C);
            Controls.Add(_web);
            _web.BringToFront();

            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, e) => e.Handled = true; // никаких новых окон браузера

            core.Navigate(url);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or DllNotFoundException
                                       or System.Runtime.InteropServices.COMException or IOException)
        {
            Fallback(url, ex.Message);
        }
    }

    /// <summary>Рантайма WebView2 в системе нет — показываем ту же страницу в окне Edge.</summary>
    private void Fallback(string url, string reason)
    {
        try
        {
            Process.Start(new ProcessStartInfo("msedge.exe", $"--app={url}") { UseShellExecute = false });
            _boot.Text = "ICDK открыт в окне Edge: " + reason;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                _boot.Text = "ICDK открыт в браузере: " + reason;
            }
            catch (Exception inner) when (inner is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _boot.Text = "нет WebView2 Runtime и не нашёлся Edge — откройте " + url;
            }
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string action;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            action = document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString() ?? string.Empty
                : document.RootElement.TryGetProperty("action", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            action = e.TryGetWebMessageAsString();
        }

        switch (action)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "maximize":
            case "toggle":
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case "close":
                ExitApp();
                break;
            case "drag":
                BeginWindowDrag();
                break;
            case "drag-end":
                _dragTimer.Stop();
                break;
        }
    }

    private readonly System.Windows.Forms.Timer _dragTimer = new() { Interval = 8 };
    private Point _dragFormStart;
    private Point _dragCursorStart;
    private bool _dragWired;

    /// <summary>
    /// Тащим окно таймером: Chrome_WidgetWin внутри WebView2 съедает WM_NCLBUTTONDOWN,
    /// поэтому одиночный ReleaseCapture + SendMessage не работает. Пока зажата ЛКМ —
    /// окно следует за курсором, отпустили — таймер встал.
    /// </summary>
    private void BeginWindowDrag()
    {
        if (!_dragWired)
        {
            _dragWired = true;
            _dragTimer.Tick += (_, _) =>
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0)
                {
                    _dragTimer.Stop();
                    return;
                }

                Location = WindowDragMath.Next(_dragFormStart, _dragCursorStart, Cursor.Position);
            };
        }

        if (WindowState == FormWindowState.Maximized)
        {
            var cursor = Cursor.Position;
            var ratio = WindowDragMath.GripRatio(Bounds, cursor);
            WindowState = FormWindowState.Normal;
            Left = cursor.X - (int)(Width * ratio);
            Top = Math.Max(0, cursor.Y - 18);
        }

        _dragFormStart = Location;
        _dragCursorStart = Cursor.Position;
        _dragTimer.Start();
    }

    /// <summary>Нативный ресайз за края и углы: у окна без рамки его иначе не поймать.</summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != NativeWindow.WmNchittest || (int)m.Result != NativeWindow.HtClient || WindowState == FormWindowState.Maximized)
        {
            return;
        }

        var screen = new Point((short)(long)m.LParam, (short)((long)m.LParam >> 16));
        var at = PointToClient(screen);
        const int grip = 7;
        var left = at.X <= grip;
        var right = at.X >= ClientSize.Width - grip;
        var top = at.Y <= grip;
        var bottom = at.Y >= ClientSize.Height - grip;

        m.Result = (IntPtr)((left, top, right, bottom) switch
        {
            (true, true, _, _) => NativeWindow.HtTopLeft,
            (_, true, true, _) => NativeWindow.HtTopRight,
            (true, _, _, true) => NativeWindow.HtBottomLeft,
            (_, _, true, true) => NativeWindow.HtBottomRight,
            (true, _, _, _) => NativeWindow.HtLeft,
            (_, _, true, _) => NativeWindow.HtRight,
            (_, true, _, _) => NativeWindow.HtTop,
            (_, _, _, true) => NativeWindow.HtBottom,
            _ => NativeWindow.HtClient,
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeWindow.RoundCorners(Handle);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        // Закрытие — это выход: сервер гаснет, значок в трее убирается, процессов не остаётся.
        _tray.Visible = false;
        _tray.Dispose();
        _web.Dispose();
        _host?.Dispose();
        _host = null;
        _icon?.Dispose();
        _icon = null;
    }
}
