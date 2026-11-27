using System.IO.Compression;
using System.Text;
using Wibe.Installer;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Живой мост окна установщика: та же HTML-разметка, тот же JSON, тот же парсер, что и в
/// WebView2, и реальная распаковка payload с прогрессом до 100%.
/// </summary>
public sealed class InstallerLiveBridgeTests
{
    [Fact]
    public void Page_posts_a_json_message_that_the_real_parser_accepts()
    {
        var html = InstallerUi.Html;
        Assert.Contains("postMessage", html);
        Assert.DoesNotContain("сталь · рубин · белый", html);

        // ровно та строка, которую формирует страница при клике «Установить ICDK →»
        var fromPage = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "install",
            path = @"Z:\ICDK_AssetVault",
            dir = @"Z:\ICDK",
            dota = @"Z:\steam\steamapps\common\dota 2 beta",
            cache = @"Z:\ICDK_AssetVault",
            desktop = true,
            startMenu = true,
            run = true,
        });

        var message = InstallerBridge.Parse(fromPage);
        Assert.NotNull(message);
        Assert.Equal("install", message!.Type);
        Assert.Equal(@"Z:\ICDK", message.Dir);
        Assert.Equal(@"Z:\steam\steamapps\common\dota 2 beta", message.Dota);
        Assert.Equal(@"Z:\ICDK_AssetVault", message.Cache);
        Assert.True(message.Desktop && message.StartMenu && message.Run);
    }

    [Fact]
    public void Broken_or_foreign_messages_are_ignored_instead_of_throwing()
    {
        Assert.Null(InstallerBridge.Parse(null));
        Assert.Null(InstallerBridge.Parse(string.Empty));
        Assert.Null(InstallerBridge.Parse("не json"));
        Assert.Null(InstallerBridge.Parse("[1,2,3]"));
        Assert.Null(InstallerBridge.Parse("{\"path\":\"Z:\\x\"}"));
    }

    [Fact]
    public void Payload_with_trailer_unpacks_to_one_hundred_percent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "icdk-bridge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var setup = Path.Combine(folder, "Setup.exe");
        var expected = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ICDK-payload-", 4096)));

        using (var file = File.Create(setup))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize, true))
        {
            gzip.Write(expected);
            gzip.Flush();
        }

        var length = new FileInfo(setup).Length;
        using (var file = new FileStream(setup, FileMode.Append, FileAccess.Write))
        {
            file.Write(BitConverter.GetBytes(length), 0, 8);
            file.Write(Encoding.ASCII.GetBytes("ICDKINST"), 0, 8);
        }

        Assert.Equal(length, Program.Payload.Length(setup));
        var progress = new List<double>();
        var target = Path.Combine(folder, "ICDK.exe");
        Program.Payload.Extract(target, progress.Add, default, setup);
        Assert.Equal(expected, File.ReadAllBytes(target));
        Assert.Equal(1d, progress[^1]);
        Assert.True(progress.Count > 1 || expected.Length < 65_536);

        Directory.Delete(folder, true);
    }

    [Fact]
    public void Raw_payload_is_renamed_after_the_stream_closes()
    {
        // Регресс: File.Move внутри using не работал — Windows держал .part, и установка
        // падала с IOException, оставляя ICDK.exe.part.
        var folder = Path.Combine(Path.GetTempPath(), "icdk-raw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var setup = Path.Combine(folder, "Setup.exe");
            var expected = new byte[256 * 1024];
            new Random(7).NextBytes(expected);
            File.WriteAllBytes(setup, expected);
            using (var file = new FileStream(setup, FileMode.Append, FileAccess.Write))
            {
                file.Write(BitConverter.GetBytes((long)expected.Length), 0, 8);
                file.Write(Encoding.ASCII.GetBytes("ICDKRAW1"), 0, 8);
            }

            Assert.Equal(expected.Length, Program.Payload.Length(setup));
            var target = Path.Combine(folder, "ICDK.exe");
            Program.Payload.Extract(target, null, default, setup);

            Assert.True(File.Exists(target), "ICDK.exe не появился — распаковка не переименовала .part");
            Assert.False(File.Exists(target + ".part"), "остался .part");
            Assert.Equal(expected, File.ReadAllBytes(target));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Free_space_follows_the_drive_typed_into_the_field()
    {
        var c = InstallerFormHost.FreeSpace(@"C:\ICDK");
        var z = InstallerFormHost.FreeSpace(@"Z:\ICDK");
        if (c.Length > 0)
        {
            Assert.Contains("C:", c);
        }

        if (z.Length > 0)
        {
            Assert.Contains("Z:", z);
        }
    }
}

/// <summary>Доступ к расчёту свободного места из окна установщика.</summary>
public static class InstallerFormHost
{
    public static string FreeSpace(string path) => InstallerForm.FreeSpaceText(path);
}
