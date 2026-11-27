using System.Buffers.Binary;
using System.Text;
using Wibe.Core.Dota;
using Xunit;

namespace Wibe.Tests;

/// <summary>
/// Страховка от регрессии «тело героя затёрто частью»: собранный pak06 не должен
/// подменять models/heroes/&lt;hero&gt;/&lt;hero&gt;.vmdl_c пустышкой или моделью слота.
/// </summary>
public class PakBodyIntegrityTests
{
    private static DotaInstallation? Install => DotaInstallation.Detect(out _);

    [Fact]
    public void Pak06_hero_bodies_are_not_parts_or_empty()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        var mod = Path.Combine(install.ModFolderPath, "pak06_dir.vpk");
        if (!File.Exists(mod) || !File.Exists(install.MainPakDir))
        {
            return;
        }

        var made = ReadSizes(mod);
        var stock = ReadSizes(install.MainPakDir);
        // a hero's body is models/heroes/<hero>/<hero>.vmdl_c; other models there are parts
        var bodies = made
            .Where(e => e.Key.StartsWith("models/heroes/", StringComparison.OrdinalIgnoreCase)
                && e.Key.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
            .Where(e =>
            {
                var parts = e.Key.Split('/');
                return parts.Length == 4
                    && string.Equals(parts[1], "heroes", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileNameWithoutExtension(parts[3]), parts[2], StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        Assert.NotEmpty(bodies);
        Assert.True(bodies.Max(e => e.Value) >= 64 * 1024, "в паке нет ни одного целого тела героя");
        foreach (var (path, size) in bodies)
        {
            Assert.True(size >= 5 * 1024, $"{path}: {size} байт — это пустышка, а не тело");
            if (stock.TryGetValue(path, out var clean) && clean > 500 * 1024)
            {
                Assert.True(size > clean / 2, $"{path}: {size} байт против стока {clean} — тело затёрто частью");
            }
        }
    }

    [Fact]
    public void Pak06_carries_no_valve_schema_or_gameinfo()
    {
        var install = Install;
        if (install is null || !install.IsValid)
        {
            return;
        }

        var mod = Path.Combine(install.ModFolderPath, "pak06_dir.vpk");
        if (!File.Exists(mod))
        {
            return;
        }

        var made = ReadSizes(mod);
        foreach (var name in new[] { "items_game.txt", "npc_heroes.txt", "gameinfo.gi", "gameinfo_branchspecific.gi" })
        {
            Assert.DoesNotContain(made.Keys, p => p.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Размеры файлов из дерева VPK: путь → длина. Данные не читаются.</summary>
    internal static Dictionary<string, long> ReadSizes(string pak)
    {
        return ReadTree(pak).ToDictionary(e => e.Path, e => (long)e.Length, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Байты одного файла из встроенного дерева VPK; null, если записи нет.</summary>
    internal static byte[]? ReadEntry(string pak, string path)
    {
        var at = ReadTree(pak).FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (at.Path is null || at.Length <= 0)
        {
            return null;
        }

        using var stream = File.OpenRead(pak);
        stream.Position = 28 + at.Offset;
        var data = new byte[at.Length];
        stream.ReadExactly(data);
        return data;
    }

    private static List<(string Path, uint Offset, uint Length)> ReadTree(string pak)
    {
        var result = new List<(string Path, uint Offset, uint Length)>();
        using var stream = File.OpenRead(pak);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        var sig = reader.ReadUInt32();
        Assert.Equal(0x55aa1234u, sig);
        _ = reader.ReadUInt32();
        var treeSize = reader.ReadUInt32();
        stream.Position = 28;
        var end = 28L + treeSize;
        while (stream.Position < end)
        {
            var ext = ReadNull(reader);
            if (ext.Length == 0)
            {
                break;
            }

            while (true)
            {
                var dir = ReadNull(reader);
                if (dir.Length == 0)
                {
                    break;
                }

                while (true)
                {
                    var name = ReadNull(reader);
                    if (name.Length == 0)
                    {
                        break;
                    }

                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    var archive = reader.ReadUInt16();
                    if (archive == 0xffff)
                    {
                        break;
                    }

                    var offset = reader.ReadUInt32();
                    var length = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    result.Add((((dir.Length == 0 ? string.Empty : dir + "/") + name + "." + ext), offset, length));
                }
            }
        }

        return result;
    }

    private static string ReadNull(BinaryReader reader)
    {
        var bytes = new List<byte>(32);
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 0)
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            bytes.Add(b);
        }
    }
}
