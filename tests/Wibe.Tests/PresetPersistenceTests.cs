using System.Diagnostics;
using System.Text.Json;
using Wibe.Server;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Пресет переживает перезапуск. Раньше путь брался из AppContext.BaseDirectory: у single-file
/// это временная распаковка %TEMP%\.net\..., она чистится — и созданный пресет исчезал. Теперь и
/// пресеты, и примерка живут в %LOCALAPPDATA%\ICDK\UserData (в тесте — во временной папке через
/// WIBE_USER_DATA), запись атомарная, старые файлы переносятся один раз.
/// </summary>
public sealed class PresetPersistenceTests
{
    [Fact]
    public void Preset_survives_a_full_restart_from_another_working_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "icdk-userdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("WIBE_USER_DATA", root);
        try
        {
            // первый запуск: создаём пресет
            Assert.StartsWith(root, ImageStore.PresetsPath, StringComparison.OrdinalIgnoreCase);
            var first = new PresetStore(ImageStore.PresetsPath);
            var saved = first.Save(
                "Мой сет",
                new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["axe"] = new(StringComparer.OrdinalIgnoreCase) { ["weapon"] = 7401, ["head"] = 13543 },
                },
                null,
                new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["axe"] = new() { ["head"] = 1 },
                });
            Assert.Equal("Мой сет", saved.Name);
            Assert.True(File.Exists(ImageStore.PresetsPath), "presets.json не появился на диске");

            // «перезапуск»: новый экземпляр стора читает файл с диска, а не память прошлого
            var second = new PresetStore(ImageStore.PresetsPath);
            var only = Assert.Single(second.All);
            Assert.Equal("Мой сет", only.Name);
            Assert.Equal(7401, only.Heroes["axe"]["weapon"]);

            // путь не зависит ни от BaseDirectory (у single-file это %TEMP%\.net\...), ни от cwd
            Assert.DoesNotContain(
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(ImageStore.PresetsPath),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(ImageStore.PresetsPath),
                StringComparison.OrdinalIgnoreCase);

            // и сам файл лежит там, где обещано
            var text = File.ReadAllText(ImageStore.PresetsPath);
            Assert.Contains("presets", text, StringComparison.Ordinal);
            Assert.Contains("axe", text, StringComparison.Ordinal);
            Assert.False(File.Exists(ImageStore.PresetsPath + ".tmp"), "остался .tmp — запись не атомарна");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WIBE_USER_DATA", null);
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // временная папка уберётся позже
            }
        }
    }

    [Fact]
    public void Old_loadout_beside_the_exe_is_migrated_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "icdk-userdata-" + Guid.NewGuid().ToString("N"));
        var beside = Path.Combine(Path.GetTempPath(), "icdk-beside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(beside);
        Environment.SetEnvironmentVariable("WIBE_USER_DATA", root);
        try
        {
            var legacy = Path.Combine(beside, "selected_loadout.json");
            File.WriteAllText(legacy, "{\"heroes\":{\"axe\":{\"weapon\":7401}}}");

            // миграция смотрит рядом с exe: в тесте exe лежит в bin, поэтому кладём файл и туда
            var nearExe = Path.Combine(AppContext.BaseDirectory, "selected_loadout.json");
            var existed = File.Exists(nearExe);
            if (!existed)
            {
                File.WriteAllText(nearExe, "{\"heroes\":{\"axe\":{\"weapon\":7401}}}");
            }

            try
            {
                var target = ImageStore.LoadoutPath;
                Assert.StartsWith(root, target, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(target), "старая примерка не перенеслась");
                Assert.Contains("7401", File.ReadAllText(target), StringComparison.Ordinal);
            }
            finally
            {
                if (!existed && File.Exists(nearExe))
                {
                    File.Delete(nearExe);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("WIBE_USER_DATA", null);
            foreach (var dir in new[] { root, beside })
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // временная папка уберётся позже
                }
            }
        }
    }
}
