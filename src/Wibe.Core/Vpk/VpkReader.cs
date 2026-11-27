using System.Text;

namespace Wibe.Core.Vpk;

/// <summary>
/// Чтение VPK целиком: путь → байты. Нужен там, где пак игры и пак ландшафта надо свести
/// в один файл (мир ландшафта поверх настоящей карты), а не просто заменить один другим.
/// </summary>
public static class VpkReader
{
    /// <summary>Все файлы пака. Пустой словарь, если файл не VPK.</summary>
    public static Dictionary<string, byte[]> ReadAll(string pak) => ReadMatching(pak, null);

    /// <summary>
    /// Только те файлы, чьи пути прошли фильтр: огромный пак игры целиком в память не тянем.
    /// <paramref name="match"/> = null — читать всё.
    /// </summary>
    public static Dictionary<string, byte[]> ReadMatching(string pak, Func<string, bool>? match)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(pak))
        {
            return result;
        }

        using var stream = File.OpenRead(pak);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != VpkWriter.Signature)
        {
            return result;
        }

        var version = reader.ReadUInt32();
        var treeSize = reader.ReadUInt32();
        long headerSize = version >= 2 ? 28 : 12;
        if (version >= 2)
        {
            stream.Position = 28;               // data / md5 / signature sizes are not needed to read
        }

        var inline = new List<(string Path, uint Offset, uint Length, byte[] Preload)>();
        var chunked = new List<(string Path, ushort Archive, uint Offset, uint Length, byte[] Preload)>();
        stream.Position = headerSize;
        var end = headerSize + treeSize;
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

                    _ = reader.ReadUInt32();        // crc
                    var preload = reader.ReadUInt16();
                    var archive = reader.ReadUInt16();
                    var offset = reader.ReadUInt32();
                    var length = reader.ReadUInt32();
                    _ = reader.ReadUInt16();        // 0xffff ends the record
                    var bytes = preload > 0 ? reader.ReadBytes(preload) : Array.Empty<byte>();
                    if (archive == 0xffff)
                    {
                        break;
                    }

                    var path = (dir.Length == 0 ? string.Empty : dir + "/") + name + "." + ext;
                    if (match is not null && !match(path))
                    {
                        continue;
                    }

                    if (archive == VpkWriter.EmbeddedArchive)
                    {
                        inline.Add((path, offset, length, bytes));
                    }
                    else
                    {
                        chunked.Add((path, archive, offset, length, bytes));
                    }
                }
            }
        }

        var dataStart = headerSize + treeSize;
        foreach (var (path, offset, length, preload) in inline)
        {
            if (length == 0)
            {
                result[path] = preload;
                continue;
            }

            var data = new byte[preload.Length + length];
            preload.CopyTo(data, 0);
            stream.Position = dataStart + offset;
            stream.ReadExactly(data, preload.Length, (int)length);
            result[path] = data;
        }

        // a split pak keeps its bytes in the _NNN volumes beside the dir file
        var folder = Path.GetDirectoryName(pak)!;
        var stem = Path.GetFileName(pak);
        var cut = stem.IndexOf("_dir.", StringComparison.OrdinalIgnoreCase);
        if (cut > 0 && chunked.Count > 0)
        {
            foreach (var group in chunked.GroupBy(c => c.Archive))
            {
                var volume = Path.Combine(folder, stem[..cut] + "_" + group.Key.ToString("000") + ".vpk");
                if (!File.Exists(volume))
                {
                    continue;
                }

                using var chunks = File.OpenRead(volume);
                foreach (var (path, _, offset, length, preload) in group)
                {
                    var data = new byte[preload.Length + length];
                    preload.CopyTo(data, 0);
                    if (length > 0)
                    {
                        chunks.Position = offset;
                        chunks.ReadExactly(data, preload.Length, (int)length);
                    }

                    result[path] = data;
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
            if (bytes.Count > 1024)
            {
                throw new ArgumentException("vpk path is too long");
            }
        }
    }
}
