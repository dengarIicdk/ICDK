using Wibe.Server.Launcher;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Боевой маршрут сборки: собранный мод уходит в изолированный контейнер
/// <c>game/dota_icdk/pak02_dir.vpk</c>, старые хвосты в языковой папке Valve вычищаются,
/// а снятие мода удаляет контейнер целиком. Запуск игры знает про <c>-language danish</c>.
/// </summary>
public sealed class BuildRoutesToLanguageMountTests
{
    [Fact]
    public void Deploy_moves_the_built_pak_into_the_language_container_and_cleans_legacy()
    {
        var game = Path.Combine(Path.GetTempPath(), "icdk-route-" + Guid.NewGuid().ToString("N")[..8]);
        var russian = Path.Combine(game, "dota_russian");
        Directory.CreateDirectory(Path.Combine(russian, "maps"));
        Directory.CreateDirectory(Path.Combine(russian, "resource"));

        File.WriteAllText(Path.Combine(russian, "pak01_dir.vpk"), "valve");
        File.WriteAllText(Path.Combine(russian, "pak06_dir.vpk"), "our-built-mod");
        File.WriteAllText(Path.Combine(russian, "maps", "dota.vpk"), "our-map");
        File.WriteAllText(Path.Combine(russian, "resource", "dota_russian.txt"), "Привет");

        try
        {
            var deployed = LanguageMount.Deploy(game, russian);

            Assert.Equal("pak02_dir.vpk", Path.GetFileName(LanguageMount.ModPakPath(game)));
            Assert.True(File.Exists(LanguageMount.ModPakPath(game)), "pak02_dir.vpk не создан в контейнере");
            Assert.Equal("our-built-mod", File.ReadAllText(LanguageMount.ModPakPath(game)));
            Assert.Equal("our-map", File.ReadAllText(LanguageMount.MapPath(game)));

            // официальный пак Valve лежит рядом под родным именем
            var folder = LanguageMount.Folder(game);
            Assert.Equal("valve", File.ReadAllText(Path.Combine(folder, "pak01_dir.vpk")));

            // старые хвосты в dota_russian убраны, пак Valve цел
            Assert.False(File.Exists(Path.Combine(russian, "pak06_dir.vpk")));
            Assert.False(File.Exists(Path.Combine(russian, "maps", "dota.vpk")));
            Assert.Equal("valve", File.ReadAllText(Path.Combine(russian, "pak01_dir.vpk")));

            // локализация продублирована под имя нашего языка, чтобы в меню не было токенов
            var localized = Path.Combine(LanguageMount.Folder(game), "resource", "dota_danish.txt");
            Assert.True(File.Exists(localized), "русская локализация не продублирована как dota_danish.txt");
            Assert.Equal("Привет", File.ReadAllText(localized));

            Assert.Contains("pak02_dir.vpk", deployed);
            Assert.Contains("removed:pak06_dir.vpk", deployed);

            // снятие мода удаляет контейнер целиком
            Assert.True(LanguageMount.Unmount(game));
            Assert.False(Directory.Exists(LanguageMount.Folder(game)));
            Assert.True(Directory.Exists(russian));

            // запуск всегда с нашим языком
            Assert.Contains("-language danish", ModSession.LaunchArguments(false));
            Assert.Contains("-language danish", ModSession.LaunchArguments(true));
            Assert.Contains("-condebug", ModSession.LaunchArguments(true));
            Assert.Equal("steam://rungameid/570//-language danish", LanguageMount.SteamUri());
        }
        finally
        {
            if (Directory.Exists(game))
            {
                Directory.Delete(game, true);
            }
        }
    }
}
