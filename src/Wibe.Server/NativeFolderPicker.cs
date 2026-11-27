using System.Runtime.InteropServices;
using System.Text;

namespace Wibe.Server;

/// <summary>
/// Системный выбор папки через shell32 без WinForms: не тянет в одиночный exe
/// весь Windows Desktop Runtime ради одного диалога.
/// </summary>
internal static class NativeFolderPicker
{
    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifEditBox = 0x0010;
    private const uint BifNewDialogStyle = 0x0040;

    public static string? Pick(string? current)
    {
        string? selected = null;
        var owner = GetForegroundWindow();
        var thread = new Thread(() =>
        {
            IntPtr root = IntPtr.Zero;
            IntPtr result = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrWhiteSpace(current))
                {
                    SHParseDisplayName(current, IntPtr.Zero, out root, 0, out _);
                }

                var info = new BrowseInfo
                {
                    hwndOwner = owner,
                    pidlRoot = root,
                    pszDisplayName = new string('\0', 260),
                    lpszTitle = "Выбери папку Dota 2 — внутри steamapps\\common\\dota 2 beta",
                    ulFlags = BifReturnOnlyFsDirs | BifEditBox | BifNewDialogStyle,
                };

                result = SHBrowseForFolder(ref info);
                if (result != IntPtr.Zero)
                {
                    var path = new StringBuilder(1024);
                    if (SHGetPathFromIDList(result, path) && path.Length > 0)
                    {
                        selected = path.ToString();
                    }
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or COMException)
            {
                selected = null;
            }
            finally
            {
                if (result != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(result);
                }

                if (root != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(root);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(5)))
        {
            return null;
        }

        return selected;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder path);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
