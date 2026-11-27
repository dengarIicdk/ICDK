using System.Text;
using Wibe.Core.Dota;
using Wibe.Core.Tools;

namespace Wibe.Core.Index;

/// <summary>
/// Индекс файлов основного пака игры, закешированный на диск и привязанный к buildid.
/// Один скан ≈ 2 с; кеш живёт в %LOCALAPPDATA%\WibeChanger\cache\pack_&lt;build&gt;.txt.
/// </summary>
public sealed class PakIndex
{
    private PakIndex(string buildId, IReadOnlyList<VpkEntryInfo> entries, bool fromCache)
    {
        BuildId = buildId;
        Entries = entries;
        FromCache = fromCache;
        var byPath = new Dictionary<string, VpkEntryInfo>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            byPath.TryAdd(entry.Path, entry);
        }

        ByPath = byPath;
    }

    public string BuildId { get; }

    public IReadOnlyList<VpkEntryInfo> Entries { get; }

    public bool FromCache { get; }

    public IReadOnlyDictionary<string, VpkEntryInfo> ByPath { get; }

    public static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WibeChanger", "cache");

    public static PakIndex Load(DotaInstallation install, bool forceRefresh = false)
    {
        var build = install.BuildId ?? "unknown";
        var cacheFile = Path.Combine(CacheDirectory, $"pack_{build}.txt");

        if (!forceRefresh && File.Exists(cacheFile))
        {
            var cached = ReadCache(cacheFile, build);
            if (cached is not null)
            {
                return new PakIndex(build, cached, fromCache: true);
            }
        }

        var entries = Source2ViewerCli.ListVpk(install.MainPakDir);
        WriteCache(cacheFile, build, entries);
        return new PakIndex(build, entries, fromCache: false);
    }

    public IEnumerable<VpkEntryInfo> Find(string substring, int limit = 50) =>
        Entries
            .Where(e => e.Path.Contains(substring, StringComparison.OrdinalIgnoreCase))
            .Take(limit);

    private static IReadOnlyList<VpkEntryInfo>? ReadCache(string file, string build)
    {
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            var header = reader.ReadLine();
            if (header != $"#wibe2 {build}")
            {
                return null;
            }

            var list = new List<VpkEntryInfo>(400_000);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    continue;
                }

                var rest = line[(tab + 1)..];
                var tab2 = rest.IndexOf('	');
                var crcText = tab2 < 0 ? rest : rest[..tab2];
                if (!uint.TryParse(crcText, System.Globalization.NumberStyles.HexNumber, null, out var crc))
                {
                    continue;
                }

                long size = 0;
                if (tab2 > 0)
                {
                    long.TryParse(rest[(tab2 + 1)..], out size);
                }

                list.Add(new VpkEntryInfo(line[..tab], crc, size));
            }

            return list.Count > 0 ? list : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteCache(string file, string build, IReadOnlyList<VpkEntryInfo> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var writer = new StreamWriter(file, false, Encoding.UTF8);
            writer.WriteLine($"#wibe {build}");
            foreach (var entry in entries)
            {
                writer.Write(entry.Path);
                writer.Write('\t');
                writer.WriteLine(entry.Crc.ToString("x8"));
            }
        }
        catch (IOException)
        {
            // кеш — необязательная оптимизация
        }
    }
}
