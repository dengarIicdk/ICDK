using System.Runtime.InteropServices;

namespace Wibe.Server;

/// <summary>
/// Жёсткая ссылка на файл игры: карта ландшафта весит гигабайты, а ссылка занимает ноль байт и
/// исчезает с удалением нашей папки. Там, где ссылки нет (FAT, чужой том, права), вызывающий код
/// копирует файл сам.
/// </summary>
internal static class NativeHardLink
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    public static bool Create(string link, string existing)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(existing))
        {
            return false;
        }

        // a link across volumes is refused by the system anyway; say so before the call
        if (!string.Equals(Path.GetPathRoot(link), Path.GetPathRoot(existing), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return CreateHardLink(link, existing, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
