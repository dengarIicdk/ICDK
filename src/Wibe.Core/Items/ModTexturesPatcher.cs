using System.Text;

namespace Wibe.Core.Items;

/// <summary>
/// Правит scripts/mod_textures.txt: спрайт миникарты и ХП-бара — это ячейка общего атласа,
/// а не отдельная картинка. Переносим блок ячейки персоны/арканы на ячейку стокового героя,
/// как with_sprites из crates/mm-core/src/item_builder/item_portraits.rs.
/// </summary>
public static class ModTexturesPatcher
{
    public static string? Apply(string original, IEnumerable<(string Sprite, string As)> swaps)
    {
        var text = original;
        var changed = false;
        foreach (var (sprite, from) in swaps.GroupBy(s => s.Sprite, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            if (!TryFindBody(text, from, out var source))
            {
                continue;
            }

            if (!TryFindBody(text, sprite, out var target))
            {
                continue;
            }

            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                continue;
            }

            var at = text.IndexOf(target, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            text = string.Concat(text.AsSpan(0, at), source, text.AsSpan(at + target.Length));
            changed = true;
        }

        return changed ? text : null;
    }

    public static bool TryFindBody(string text, string name, out string body)
    {
        body = string.Empty;
        var needle = "\"" + name + "\"";
        var index = 0;
        while (index < text.Length)
        {
            var quote = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
            if (quote < 0)
            {
                return false;
            }

            index = quote + needle.Length;
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

            body = text[open..(close + 1)];
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
}
