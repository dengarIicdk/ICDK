using Wibe.Core.Cache;
using Wibe.Installer;
using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Единый кэш ICDK AssetVault: установщик, сервер и иконки берут один и тот же корень,
/// а готовые иконки читаются из кэша без распаковки pak01_dir.vpk.
/// </summary>
public sealed class VaultCacheTests
{
    private const string Vault = @"Z:\ICDK_AssetVault";

    [Fact]
    public void Installer_defaults_to_a_ready_vault_on_any_drive()
    {
        var ready = Program.Options.ReadyVault();
        if (ready is null)
        {
            return; // на этой машине готового кэша нет — проверять нечего
        }

        Assert.Equal("ICDK_AssetVault", Path.GetFileName(ready.TrimEnd(Path.DirectorySeparatorChar)));
        Assert.Equal(ready, Program.Options.DefaultCachePath());
    }

    [Fact]
    public void Settings_cache_path_is_used_by_the_shared_locator()
    {
        var folder = Path.Combine(Path.GetTempPath(), "icdk-settings-" + Guid.NewGuid().ToString("N")[..8]);
        var cache = Path.Combine(folder, "vault");
        Directory.CreateDirectory(cache);
        var settings = Path.Combine(folder, "settings.json");
        File.WriteAllText(settings, "{\"cachePath\":" + System.Text.Json.JsonSerializer.Serialize(cache) + "}");

        var previous = Environment.GetEnvironmentVariable("WIBE_CACHE");
        Environment.SetEnvironmentVariable("WIBE_CACHE", null);
        try
        {
            Assert.Equal(Path.GetFullPath(cache), AssetVaultLocator.Resolve(settings));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WIBE_CACHE", previous);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Server_and_3d_preview_read_the_same_vault_root()
    {
        if (!Directory.Exists(Vault))
        {
            return;
        }

        var previous = Environment.GetEnvironmentVariable("WIBE_CACHE");
        Environment.SetEnvironmentVariable("WIBE_CACHE", Vault);
        CacheLocator.Reset();
        ImageStore.ResetVaultIcons();
        try
        {
            Assert.Equal(Vault, CacheLocator.Root);
            Assert.Equal(
                Path.Combine(Vault, "meshes"),
                GlbPreviewService.CacheDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WIBE_CACHE", previous);
            CacheLocator.Reset();
            ImageStore.ResetVaultIcons();
        }
    }

    [Fact]
    public void Item_icon_comes_from_the_vault_without_unpacking_the_game_pak()
    {
        if (!Directory.Exists(Vault))
        {
            return;
        }

        var previous = Environment.GetEnvironmentVariable("WIBE_CACHE");
        Environment.SetEnvironmentVariable("WIBE_CACHE", Vault);
        CacheLocator.Reset();
        ImageStore.ResetVaultIcons();
        try
        {
            var file = ImageStore.FindItem("econ/items/axe/axe_ti9_immortal_head/axe_ti9_immortal_head");
            Assert.NotNull(file);
            Assert.True(File.Exists(file), "иконка из кэша не найдена на диске: " + file);
            Assert.StartsWith(
                Path.Combine(Vault, "temp", "icons", "icons_v10"),
                file!,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(ImageStore.VaultIconCount > 1_000, "индекс готовых иконок не собрался");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WIBE_CACHE", previous);
            CacheLocator.Reset();
            ImageStore.ResetVaultIcons();
        }
    }
}
