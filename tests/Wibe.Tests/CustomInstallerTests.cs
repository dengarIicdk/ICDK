using System.Text;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Наш установщик: payload лежит в хвосте файла (int64 длина + magic ICDKINST), версия
/// берётся из сборки, а не зашита строкой. Проверяем готовый dist\ICDK_Setup, если он собран.
/// </summary>
public sealed class CustomInstallerTests
{
    private static string? SetupPath()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            var dist = Path.Combine(at.FullName, "dist");
            if (!Directory.Exists(dist))
            {
                continue;
            }

            var file = Directory.EnumerateFiles(dist, "ICDK_Setup*.exe").OrderByDescending(f => f).FirstOrDefault();
            if (file is not null)
            {
                return file;
            }
        }

        return null;
    }

    [Fact]
    public void Setup_carries_a_raw_payload_with_a_magic_trailer()
    {
        var setup = SetupPath();
        if (setup is null)
        {
            return;
        }

        using var stream = File.OpenRead(setup);
        Assert.True(stream.Length > 1_000_000, "установщик подозрительно маленький");
        stream.Seek(-16, SeekOrigin.End);
        Span<byte> trailer = stackalloc byte[16];
        stream.ReadExactly(trailer);
        var magic = Encoding.ASCII.GetString(trailer[8..16]);
        Assert.Equal("ICDKRAW1", magic);

        var length = BitConverter.ToInt64(trailer[..8]);
        Assert.True(length > 100_000, $"payload всего {length} байт");
        Assert.True(stream.Length - 16 - length > 1_000_000, "перед payload нет самого установщика");
    }

    [Fact]
    public void Installer_project_is_a_frameless_winforms_gui()
    {
        string? repo = null;
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Directory.Build.props")))
            {
                repo = at.FullName;
                break;
            }
        }

        Assert.NotNull(repo);
        var project = File.ReadAllText(Path.Combine(repo!, "src", "Wibe.Installer", "Wibe.Installer.csproj"));
        Assert.Contains("WinExe", project);
        Assert.Contains("net8.0-windows", project);

        var form = File.ReadAllText(Path.Combine(repo!, "src", "Wibe.Installer", "InstallerForm.cs"));
        Assert.Contains("FormBorderStyle.None", form);
        Assert.Contains("DwmSetWindowAttribute", form);
        Assert.Contains("AutoScaleMode.Dpi", form);

        var publish = File.ReadAllText(Path.Combine(repo!, "publish.ps1"));
        Assert.DoesNotContain("ISCC", publish);
        Assert.Contains("ICDKRAW1", publish);
    }
}
