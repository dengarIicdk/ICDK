using System.Security.Cryptography;
using System.Text;
using Wibe.Core.Mods;

namespace Wibe.Core.Vpk;

/// <summary>
/// Чистая запись VPK версии 2 (dir-файл с встроенными данными, как pak01_dir.vpk у Dota 2).
/// Без внешних утилит: заголовок → дерево → данные → MD5-секции.
/// </summary>
public static class VpkWriter
{
    public const uint Signature = 0x55aa1234;
    public const ushort EmbeddedArchive = 0x7fff;

    /// <summary>
    /// Собирает dir-VPK. Ключи — пути внутри пака ("scripts/items/items_game.txt"), значения — содержимое.
    /// Файлы идут в секцию данных самого dir-файла (archiveIndex = 0x7fff).
    /// </summary>
    public static void Write(string path, IReadOnlyDictionary<string, byte[]> files)
    {
        var tree = BuildTree(files, out var data);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var whole = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
        {
            var header = new byte[28];
            WriteUInt32(header, 0, Signature);
            WriteUInt32(header, 4, 2);
            WriteUInt32(header, 8, (uint)tree.Length);
            WriteUInt32(header, 12, (uint)data.Length);
            WriteUInt32(header, 16, 0);   // archiveMd5SectionSize
            WriteUInt32(header, 20, 48);  // otherMd5SectionSize
            WriteUInt32(header, 24, 0);   // signatureSectionSize

            stream.Write(header);
            whole.AppendData(header);
            stream.Write(tree);
            whole.AppendData(tree);
            stream.Write(data);
            whole.AppendData(data);

            var treeMd5 = MD5.HashData(tree);
            var emptyMd5 = MD5.HashData(Array.Empty<byte>());
            var wholeMd5 = whole.GetHashAndReset();

            stream.Write(treeMd5);
            stream.Write(emptyMd5);
            stream.Write(wholeMd5);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Дерево VPK: расширение → путь → файл → запись 18 байт.</summary>
    private static byte[] BuildTree(IReadOnlyDictionary<string, byte[]> files, out byte[] data)
    {
        var grouped = new SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, byte[]>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in files)
        {
            var normalized = pair.Key.Replace('\\', '/').Trim('/').ToLowerInvariant();
            var slash = normalized.LastIndexOf('/');
            var dir = slash < 0 ? " " : normalized[..slash];
            var name = slash < 0 ? normalized : normalized[(slash + 1)..];
            var dot = name.LastIndexOf('.');
            var ext = dot < 0 ? " " : name[(dot + 1)..];
            var stem = dot < 0 ? name : name[..dot];

            if (!grouped.TryGetValue(ext, out var byPath))
            {
                byPath = new SortedDictionary<string, SortedDictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
                grouped[ext] = byPath;
            }

            if (!byPath.TryGetValue(dir, out var byName))
            {
                byName = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                byPath[dir] = byName;
            }

            byName[stem] = pair.Value;
        }

        data = AssembleData(grouped);

        using var tree = new MemoryStream(64 * 1024);
        using var writer = new BinaryWriter(tree, Encoding.UTF8, leaveOpen: true);

        var offset = 0;
        foreach (var ext in grouped)
        {
            WriteString(writer, ext.Key);
            foreach (var dir in ext.Value)
            {
                WriteString(writer, dir.Key);
                foreach (var file in dir.Value)
                {
                    WriteString(writer, file.Key);
                    writer.Write(Crc32.Compute(file.Value));
                    writer.Write((ushort)0);              // preloadBytes
                    writer.Write(EmbeddedArchive);        // archiveIndex
                    writer.Write((uint)offset);
                    writer.Write((uint)file.Value.Length);
                    writer.Write((ushort)0xffff);         // terminator записи
                    offset += file.Value.Length;
                }

                writer.Write((byte)0);                    // конец списка файлов в папке
            }

            writer.Write((byte)0);                        // конец списка папок в расширении
        }

        writer.Write((byte)0);                            // конец списка расширений
        writer.Flush();
        return tree.ToArray();
    }

    private static byte[] AssembleData(SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, byte[]>>> grouped)
    {
        var total = grouped.Sum(e => e.Value.Sum(d => d.Value.Sum(f => (long)f.Value.Length)));
        var data = new byte[total];
        var position = 0;
        foreach (var ext in grouped)
        {
            foreach (var dir in ext.Value)
            {
                foreach (var file in dir.Value)
                {
                    Buffer.BlockCopy(file.Value, 0, data, position, file.Value.Length);
                    position += file.Value.Length;
                }
            }
        }

        return data;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
        buffer[offset + 2] = (byte)((value >> 16) & 0xff);
        buffer[offset + 3] = (byte)((value >> 24) & 0xff);
    }
}
