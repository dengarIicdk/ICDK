using Wibe.Core.Dota;
using Xunit;

namespace Wibe.Tests;

public class DotaPathTests
{
    [Fact]
    public void Finds_the_game_root_from_the_game_or_dota_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "wibe-path-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(root, "steamapps", "common", "dota 2 beta");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameDir, "game", "dota"));
            File.WriteAllText(Path.Combine(gameDir, "game", "dota", "pak01_dir.vpk"), "");
            File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_570.acf"), """
                "AppState"
                {
                	"appid"		"570"
                	"name"		"Dota 2"
                	"installdir"		"dota 2 beta"
                	"buildid"		"26877585"
                }
                """);

            Assert.Equal(gameDir, DotaInstallation.NormalizeGameDir(gameDir, out var error), ignoreCase: true);
            Assert.Null(error);
            Assert.Equal(gameDir, DotaInstallation.NormalizeGameDir(Path.Combine(gameDir, "game", "dota"), out _), ignoreCase: true);

            var install = DotaInstallation.FromGameDir(gameDir, out error);
            Assert.NotNull(install);
            Assert.Null(error);
            Assert.Equal("26877585", install!.BuildId);
            Assert.Equal(gameDir, install.GameDir, ignoreCase: true);
            Assert.True(install.IsManual);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }


    [Fact]
    public void Stores_the_manual_path_for_the_next_start()
    {
        var before = DotaPathStore.Read();
        var wanted = Path.Combine(Path.GetTempPath(), "wibe-store");
        try
        {
            Assert.True(DotaPathStore.Save(wanted, out var saveError), saveError);
            Assert.Equal(wanted, DotaPathStore.Read());
        }
        finally
        {
            DotaPathStore.Save(before, out _);
        }
    }

    [Fact]
    public void Rejects_a_folder_without_the_game_pak()
    {
        var empty = Path.Combine(Path.GetTempPath(), "wibe-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Null(DotaInstallation.NormalizeGameDir(empty, out var error));
            Assert.Contains("pak01_dir.vpk", error);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
