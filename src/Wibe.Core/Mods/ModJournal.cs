using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wibe.Core.Mods;

public sealed record InstalledMod
{
    public required string Id { get; init; }

    public required int Slot { get; init; }

    /// <summary>hero.body / hero.slot / courier / tower / creep / ward / hud / terrain</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string TargetPath { get; init; }

    public required string SourcePath { get; init; }

    public required uint SourceCrc { get; init; }

    public required long Size { get; init; }

    public required string VpkPath { get; init; }

    public string? BackupPath { get; init; }

    public string? HeroId { get; init; }

    /// <summary>Все пути, которые лежат в паке (для сетов: тело + слоты).</summary>
    public List<string>? Files { get; init; }

    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Журнал установленного. Лежит в %APPDATA%\WibeChanger\journal.json.
/// Единственный источник правды для отката: даже если приложение удалили, файл остался.
/// </summary>
public sealed class ModJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<InstalledMod> Mods { get; init; } = new();

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WibeChanger");

    public static string FilePath => Path.Combine(DirectoryPath, "journal.json");

    public static ModJournal Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new ModJournal();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<ModJournal>(json, JsonOptions) ?? new ModJournal();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ModJournal();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public InstalledMod? BySlot(int slot) => Mods.FirstOrDefault(m => m.Slot == slot);

    public void Upsert(InstalledMod mod)
    {
        Mods.RemoveAll(m => m.Slot == mod.Slot);
        Mods.Add(mod);
        Mods.Sort((a, b) => a.Slot.CompareTo(b.Slot));
    }

    public bool RemoveBySlot(int slot) => Mods.RemoveAll(m => m.Slot == slot) > 0;
}
