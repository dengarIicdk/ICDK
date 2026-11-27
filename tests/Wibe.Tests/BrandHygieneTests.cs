using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Чистота бренда: в наших исходниках нет чужих названий кэша и чужих папок.
/// Наш кэш называется AssetVault и живёт в meshes/ + icons/.
/// </summary>
public sealed class BrandHygieneTests
{
    private static string Repo()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Directory.Build.props")))
            {
                return at.FullName;
            }
        }

        throw new InvalidOperationException("корень репозитория не найден");
    }

    [Theory]
    [InlineData("OverlayDotaCache")]
    [InlineData("overlay_glb_cache")]
    [InlineData("overlay_icons_cache")]
    [InlineData("Overlay_Setup")]
    public void No_foreign_cache_names_in_our_sources(string forbidden)
    {
        var hits = new List<string>();
        foreach (var folder in new[] { "src", "tests", "installer" })
        {
            var root = Path.Combine(Repo(), folder);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Path.GetFileName(file).Equals("BrandHygieneTests.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                   p.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                   p.Equals("target", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".cs" or ".rs" or ".js" or ".css" or ".html" or ".iss" or ".json" or ".ps1"))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                if (text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(Path.GetRelativePath(Repo(), file));
                }
            }
        }

        Assert.True(hits.Count == 0, $"чужое имя {forbidden} встречается: {string.Join(", ", hits.Take(5))}");
    }

    [Fact]
    public void Our_cache_layout_is_assetvault_with_meshes_and_icons()
    {
        var locator = File.ReadAllText(Path.Combine(Repo(), "src", "Wibe.Server", "CacheLocator.cs"));
        Assert.Contains("AssetVault", locator);
        var preview = File.ReadAllText(Path.Combine(Repo(), "src", "Wibe.Server", "GlbPreviewService.cs"));
        Assert.Contains("meshes", preview);
    }
}
