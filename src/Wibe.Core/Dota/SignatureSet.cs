using System.Security.Cryptography;
using System.Text;

namespace Wibe.Core.Dota;

public sealed record SignatureEntry(string RawPath, string Sha1, string Crc)
{
    /// <summary>Путь относительно каталога, где лежит dota.signatures (game/bin/win64).</summary>
    public string RelativePath => RawPath.StartsWith("...\\", StringComparison.Ordinal)
        ? RawPath[4..]
        : RawPath.Replace('\\', Path.DirectorySeparatorChar);
}

public sealed record SignatureCheckResult(string Path, bool Exists, bool Matches, string? ActualSha1, string ExpectedSha1);

/// <summary>
/// Читает game/bin/win64/dota.signatures — список подписанных клиентских файлов Valve.
/// Нужен только для самопроверки: приложение обязано НИКОГДА не менять эти файлы,
/// а значит их хеши всегда должны совпадать с диском.
/// </summary>
public sealed class SignatureSet
{
    private SignatureSet(string filePath, IReadOnlyList<SignatureEntry> entries)
    {
        FilePath = filePath;
        BaseDir = Path.GetDirectoryName(filePath)!;
        Entries = entries;
    }

    public string FilePath { get; }

    public string BaseDir { get; }

    public IReadOnlyList<SignatureEntry> Entries { get; }

    public static SignatureSet? TryLoad(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var entries = new List<SignatureEntry>();
        foreach (var line in File.ReadLines(filePath, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split('~');
            if (parts.Length < 2)
            {
                continue;
            }

            var rawPath = parts[0].Trim();
            var sha1 = string.Empty;
            var crc = string.Empty;
            foreach (var segment in parts.Skip(1))
            {
                var s = segment.Trim();
                if (s.StartsWith("SHA1:", StringComparison.OrdinalIgnoreCase))
                {
                    sha1 = CutAtSemicolon(s[5..]);
                }
                else if (s.StartsWith("CRC:", StringComparison.OrdinalIgnoreCase))
                {
                    crc = CutAtSemicolon(s[4..]);
                }
            }

            if (rawPath.Length == 0 || sha1.Length == 0)
            {
                continue;
            }

            entries.Add(new SignatureEntry(rawPath, sha1.ToUpperInvariant(), crc.ToUpperInvariant()));
        }

        return new SignatureSet(filePath, entries);
    }

    public string ResolveFullPath(SignatureEntry entry) =>
        Path.GetFullPath(Path.Combine(BaseDir, entry.RelativePath));

    public SignatureCheckResult Verify(SignatureEntry entry)
    {
        var full = ResolveFullPath(entry);
        if (!File.Exists(full))
        {
            return new SignatureCheckResult(full, false, false, null, entry.Sha1);
        }

        var actual = ComputeSha1(full);
        return new SignatureCheckResult(full, true, string.Equals(actual, entry.Sha1, StringComparison.OrdinalIgnoreCase), actual, entry.Sha1);
    }

    public IReadOnlyList<SignatureCheckResult> VerifyAll()
    {
        var results = new List<SignatureCheckResult>(Entries.Count);
        foreach (var entry in Entries)
        {
            results.Add(Verify(entry));
        }

        return results;
    }

    private static string CutAtSemicolon(string value)
    {
        var index = value.IndexOf(';');
        return (index >= 0 ? value[..index] : value).Trim();
    }

    public static string ComputeSha1(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}
