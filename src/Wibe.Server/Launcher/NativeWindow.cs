using System.Runtime.InteropServices;

namespace Wibe.Server.Launcher;

/// <summary>
/// Всё, чего нет в WinForms: скругление углов Windows 11, перетаскивание окна без рамки и
/// попадание мыши по краям для нативного ресайза.
/// </summary>
internal static class NativeWindow
{
    public const int WmNchittest = 0x0084;
    public const int HtClient = 1;
    public const int HtCaption = 2;
    public const int HtLeft = 10;
    public const int HtRight = 11;
    public const int HtTop = 12;
    public const int HtTopLeft = 13;
    public const int HtTopRight = 14;
    public const int HtBottom = 15;
    public const int HtBottomLeft = 16;
    public const int HtBottomRight = 17;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    /// <summary>Скруглённые углы Windows 11; на Windows 10 вызов просто ничего не делает.</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        var preference = DwmwcpRound;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Windows без DWM: окно останется прямоугольным
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10: параметра нет
        }
    }

    /// <summary>Перетаскивание окна за шапку, которую рисует страница: WM_NCHITTEST = HTCAPTION.</summary>
    public static void DragWindow(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WmNchittest, (IntPtr)HtCaption, IntPtr.Zero);
    }

    public static void BringToFront(string title)
    {
        var hwnd = FindWindow(null, title);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
    }
}
