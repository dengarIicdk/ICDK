using System.Text;
using Wibe.Core.Mods;
using Xunit;

namespace Wibe.Tests;

public class Crc32Tests
{
    [Fact]
    public void Matches_known_vector()
    {
        // Стандартный тест CRC-32/IEEE: "123456789" → 0xCBF43926
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void File_and_span_agree()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wibe-crc-{Guid.NewGuid():n}.bin");
        try
        {
            var data = Encoding.UTF8.GetBytes(new string('a', 100_003));
            File.WriteAllBytes(file, data);
            Assert.Equal(Crc32.Compute(data), Crc32.ComputeFile(file));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}

public class ModJournalTests
{
    [Fact]
    public void Upsert_replaces_same_slot_and_keeps_order()
    {
        var journal = new ModJournal();
        journal.Upsert(NewMod(slot: 7, title: "old"));
        journal.Upsert(NewMod(slot: 6, title: "six"));
        journal.Upsert(NewMod(slot: 7, title: "new"));

        Assert.Equal(2, journal.Mods.Count);
        Assert.Equal(new[] { 6, 7 }, journal.Mods.Select(m => m.Slot));
        Assert.Equal("new", journal.BySlot(7)!.Title);
        Assert.True(journal.RemoveBySlot(6));
        Assert.Null(journal.BySlot(6));
    }

    [Fact]
    public void Normalizes_asset_paths()
    {
        Assert.Equal("models/heroes/axe/axe.vmdl_c", ModInstaller.NormalizeAssetPath(@"\Models\Heroes\Axe\Axe.vmdl_c"));
    }

    private static InstalledMod NewMod(int slot, string title) => new()
    {
        Id = $"test-{slot}",
        Slot = slot,
        Kind = "hero.body",
        Title = title,
        TargetPath = "models/heroes/axe/axe.vmdl_c",
        SourcePath = "models/items/axe/axe_carnival/axe_carnival_base.vmdl_c",
        SourceCrc = 0x5890d3a8,
        Size = 1314278,
        VpkPath = $"pak{slot:00}_dir.vpk",
    };
}
