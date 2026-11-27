using System.Globalization;
using System.Text;

namespace Wibe.Core.Items;

/// <summary>Перенос блока камеры/света портрета с модели персоны или арканы на стоковую модель героя.</summary>
public sealed record PortraitSwap(string FromModel, string ToModel, double Scale = 1.0);

/// <summary>
/// Правит scripts/npc/portraits.txt как обычный текст: находит top-level блок по пути модели,
/// считает баланс фигурных скобок с учётом кавычек и подменяет блок стоковой модели блоком
/// персоны/арканы. Никаких предположений о конкретном герое — только пути моделей.
/// </summary>
public static class PortraitTxtPatcher
{
    public static string? Apply(string original, IEnumerable<PortraitSwap> swaps)
    {
        var list = swaps
            .Where(s => !string.IsNullOrWhiteSpace(s.FromModel) && !string.IsNullOrWhiteSpace(s.ToModel))
            .GroupBy(s => Normalize(s.ToModel), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var changed = false;
        var text = original;
        foreach (var swap in list)
        {
            if (!TryFind(text, swap.FromModel, out var fromStart, out var fromEnd))
            {
                continue;
            }

            var fromBlock = text[fromStart..fromEnd];
            if (!TryFind(text, swap.ToModel, out var toStart, out var toEnd))
            {
                var at = text.LastIndexOf('}');
                if (at < 0)
                {
                    continue;
                }

                var block = Rename(fromBlock, swap.ToModel);
                text = text.Insert(at, "\t" + block + Environment.NewLine);
                changed = true;
                continue;
            }

            var target = text[toStart..toEnd];
            var replacement = Rename(fromBlock, swap.ToModel);
            if (swap.Scale > 0 && Math.Abs(swap.Scale - 1.0) > 0.0001)
            {
                replacement = ScaleValues(replacement, swap.Scale);
            }

            if (string.Equals(target, replacement, StringComparison.Ordinal))
            {
                continue;
            }

            text = string.Concat(text.AsSpan(0, toStart), replacement, text.AsSpan(toEnd));
            changed = true;
        }

        return changed ? text : null;
    }

    public static bool TryFind(string text, string model, out int start, out int end)
    {
        start = end = -1;
        var normalized = Normalize(model);
        var needle = "\"" + normalized + "\"";
        var index = 0;
        while (index < text.Length)
        {
            var quote = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
            if (quote < 0)
            {
                return false;
            }

            index = quote + needle.Length;
            var lineStart = text.LastIndexOf('\n', Math.Max(0, quote - 1)) + 1;
            var prefix = text[lineStart..quote];
            if (prefix.Trim().Length != 0)
            {
                continue;
            }

            var open = text.IndexOf('{', index);
            if (open < 0)
            {
                return false;
            }

            if (text.AsSpan(index, open - index).ToString().Trim().Length != 0)
            {
                continue;
            }

            if (!TryClose(text, open, out var close))
            {
                return false;
            }

            start = lineStart;
            end = close + 1;
            return true;
        }

        return false;
    }

    private static bool TryClose(string text, int open, out int close)
    {
        close = -1;
        var depth = 0;
        var quoted = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    return true;
                }
            }
        }

        return false;
    }

    private static string Rename(string block, string model)
    {
        var firstQuote = block.IndexOf('"');
        var secondQuote = firstQuote < 0 ? -1 : block.IndexOf('"', firstQuote + 1);
        if (firstQuote < 0 || secondQuote < 0)
        {
            return block;
        }

        return block[..firstQuote] + "\"" + Normalize(model) + "\"" + block[(secondQuote + 1)..];
    }

    private static string ScaleValues(string block, double scale)
    {
        var result = new StringBuilder(block.Length);
        var i = 0;
        while (i < block.Length)
        {
            if (block[i] != '"')
            {
                result.Append(block[i++]);
                continue;
            }

            var close = block.IndexOf('"', i + 1);
            if (close < 0)
            {
                result.Append(block[i..]);
                break;
            }

            result.Append(block, i, close - i + 1);
            i = close + 1;
            var lineEnd = block.IndexOf('\n', i);
            if (lineEnd < 0)
            {
                lineEnd = block.Length;
            }

            var fragment = block[i..lineEnd];
            result.Append(ScaleFragment(fragment, scale));
            i = lineEnd;
        }

        return result.ToString();
    }

    private static string ScaleFragment(string fragment, double scale)
    {
        var tokens = fragment.Split(' ', StringSplitOptions.None);
        var changed = false;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            tokens[i] = (value * scale).ToString("0.000000", CultureInfo.InvariantCulture);
            changed = true;
        }

        return changed ? string.Join(' ', tokens) : fragment;
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
}
