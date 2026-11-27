using System.Text;

namespace Wibe.Core.Vpk;

/// <summary>
/// The file list of a VPK, read from its tree. Used to tell a whole playable map from a
/// pack of terrain assets before one is put in place of the game's own map: a map without
/// its navigation grid (maps/dota.gnv) and entity lump (maps/dota.vhcg) leaves the hero
/// unable to walk and the HUD without hotkeys.
/// </summary>
public static class VpkIndex
{
    /// <summary>Every path in the pak, in the form "dir/name.ext". Empty when unreadable.</summary>
    public static HashSet<string> Paths(string pak)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(pak) || new FileInfo(pak).Length < 28)
        {
            return result;
        }

        try
        {
            using var stream = File.OpenRead(pak);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != 0x55aa1234u)
            {
                return result;
            }

            _ = reader.ReadUInt32();          // version
            var treeSize = reader.ReadUInt32();
            stream.Position = 28;             // the two-chunk header; the tree follows it
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

                        _ = reader.ReadUInt32();  // crc
                        var preload = reader.ReadUInt16();
                        var archive = reader.ReadUInt16();
                        _ = reader.ReadUInt32();  // offset
                        _ = reader.ReadUInt32();  // length
                        _ = reader.ReadUInt16();  // 0xffff ends the record
                        if (archive == 0xffff)
                        {
                            break;
                        }

                        // preload bytes sit right behind the fields of an entry kept inside the tree
                        if (preload > 0)
                        {
                            stream.Position += preload;
                        }

                        result.Add((dir.Length == 0 ? string.Empty : dir + "/") + name + "." + ext);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return result;
        }

        return result;
    }

    /// <summary>
    /// True when the pak can stand in for maps/dota.vpk: it carries the compiled map, its
    /// navigation grid and its entity lump, all three of which the terrain packs lack.
    /// </summary>
    public static bool IsPlayableMap(string pak)
    {
        var paths = Paths(pak);
        return paths.Contains("maps/dota.vmap_c")
               && paths.Contains("maps/dota.gnv")
               && paths.Contains("maps/dota.vhcg");
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
            if (bytes.Count > 1024)
            {
                throw new ArgumentException("vpk path is too long");
            }
        }
    }
}
