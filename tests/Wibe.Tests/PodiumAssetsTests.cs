using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Файлы 3D-подиума лежат локально: без CDN, Babylon.js и загрузчик glTF отдаются
/// тем же сервером, что и интерфейс, а podium.js подключает сцену.
/// </summary>
public sealed class PodiumAssetsTests
{
    private static string Www()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            var candidate = Path.Combine(at.FullName, "src", "Wibe.Server", "www");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("папка www не найдена");
    }

    [Theory]
    [InlineData("podium.bundle.js", 400_000)]
    [InlineData("vendor/three.module.js", 500_000)]
    [InlineData("vendor/addons/GLTFLoader.js", 50_000)]
    [InlineData("vendor/addons/OrbitControls.js", 15_000)]
    [InlineData("vendor/addons/RoomEnvironment.js", 1_500)]
    [InlineData("vendor/meshopt_decoder.js", 10_000)]
    [InlineData("podium.js", 4_000)]
    public void Podium_assets_exist_and_are_real_javascript(string relative, int minBytes)
    {
        var file = Path.Combine(Www(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(file), $"нет файла {relative}");
        var bytes = new FileInfo(file).Length;
        Assert.True(bytes >= minBytes, $"{relative}: {bytes} байт — меньше ожидаемого {minBytes}");

        var head = File.ReadAllText(file).TrimStart();
        Assert.False(head.StartsWith("<", StringComparison.Ordinal), $"{relative}: вместо JS отдаётся разметка");
    }

    [Fact]
    public void Hero_screen_mounts_the_3d_podium_and_the_page_loads_it_locally()
    {
        var www = Www();
        var index = File.ReadAllText(Path.Combine(www, "index.html"));
        Assert.Contains("podium.bundle.js", index);
        Assert.DoesNotContain("importmap", index);
        Assert.Contains("podium.bundle.js", index);
        Assert.Contains("/static/podium.bundle.js", index);

        var app = File.ReadAllText(Path.Combine(www, "app.js"));
        Assert.Contains("id=\"podium\"", app);
        Assert.Contains("data-preview-mode=\"2d\"", app);
        Assert.Contains("Podium?.sync", app);

        var podium = File.ReadAllText(Path.Combine(www, "podium.js"));
        Assert.DoesNotContain("BABYLON", podium);
        Assert.Contains("/api/mesh/hero", podium);
        Assert.Contains("/api/mesh/item", podium);
        Assert.Contains("meshopt_decoder.js", podium);
        Assert.Contains("heroSkeleton", podium);
        Assert.Contains("bindMatrix", podium);
        Assert.Contains("PMREMGenerator", podium);
    }
}
