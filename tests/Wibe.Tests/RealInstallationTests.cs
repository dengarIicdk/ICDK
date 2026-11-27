using Wibe.Core.Diagnostics;
using Wibe.Core.Dota;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Проверки на реальной установке Dota 2. Если игры нет — тест просто ничего не проверяет
/// (чтобы CI без Steam оставался зелёным).
/// </summary>
public class RealInstallationTests
{
    private static DotaInstallation? Install => DotaInstallation.Detect(out _);

    [Fact]
    public void Finds_dota_and_build_id()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        Assert.True(File.Exists(install.MainPakDir));
        Assert.False(string.IsNullOrWhiteSpace(install.BuildId));
    }

    [Fact]
    public void Resolves_mod_folder_from_audio_language()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        if (string.IsNullOrEmpty(install.AudioLanguage))
        {
            return;
        }

        Assert.Equal($"dota_{install.AudioLanguage}", install.ModFolderName);
        Assert.True(install.ModFolderLayeredOnDota, "папка модов должна быть LayeredOnMod dota");
        Assert.True(install.ModFolderHasSelfSearchPath, "папка модов должна быть первой в SearchPaths");
    }

    [Fact]
    public void Free_slot_is_outside_valve_slots()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        var free = install.RecommendedFreeSlot;
        Assert.NotNull(free);
        Assert.InRange(free!.Value, DotaInstallation.FirstUsableSlot, DotaInstallation.LastUsableSlot);
        Assert.DoesNotContain(install.OccupiedSlots, s => s.Number == free.Value);
    }

    [Fact]
    public void Signed_valve_files_are_untouched()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        var set = SignatureSet.TryLoad(install.SignaturesPath);
        if (set is null)
        {
            return;
        }

        var results = set.VerifyAll();
        var mismatched = results.Where(r => r.Exists && !r.Matches).ToList();
        Assert.Empty(mismatched);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Doctor_reports_ready_or_explains_why_not()
    {
        var report = Doctor.Run();
        if (report.Installation is null)
        {
            return;
        }

        Assert.Empty(report.SignatureMismatched);
        Assert.True(report.Installation!.RecommendedFreeSlot is not null);

        // Диагностика не должна падать и обязана иметь вердикт по каждому разделу.
        Assert.NotEmpty(report.Lines);
        Assert.Contains(report.Lines, l => l.Section == "Готовность");
    }
}
