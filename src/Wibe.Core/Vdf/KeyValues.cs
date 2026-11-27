using System.Text;

namespace Wibe.Core.Vdf;

/// <summary>
/// Один узел KeyValues/VDF: либо пара "ключ значение", либо блок "ключ { ... }".
/// Ключи могут повторяться — поэтому Children — список, а не словарь.
/// </summary>
public sealed class KeyValuesNode
{
    public KeyValuesNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public string? Value { get; set; }

    public List<KeyValuesNode> Children { get; } = new();

    public bool IsBlock => Value is null;

    public KeyValuesNode? Child(string key)
    {
        foreach (var child in Children)
        {
            if (string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    public IEnumerable<KeyValuesNode> All(string key) =>
        Children.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? String(string key) => Child(key)?.Value;

    public int? Int(string key) => int.TryParse(String(key), out var v) ? v : null;

    public override string ToString() => IsBlock ? $"{Key} {{ {Children.Count} }}" : $"{Key} = {Value}";
}

/// <summary>
/// Толерантный парсер Valve KeyValues (используется в boot.vcfg, appmanifest_*.acf, libraryfolders.vdf).
/// Поддерживает: кавычки, bare-токены, комментарии //, вложенность, дубли ключей, escaped-последовательности.
/// </summary>
public static class KeyValues
{
    public static KeyValuesNode Parse(string text)
    {
        var reader = new TokenReader(text);
        var root = new KeyValuesNode("__root__");
        ParseInto(reader, root, expectClosingBrace: false);
        return root;
    }

    public static KeyValuesNode ParseFile(string path) =>
        Parse(File.ReadAllText(path, Encoding.UTF8));

    public static KeyValuesNode? TryParseFile(string path)
    {
        try
        {
            return File.Exists(path) ? ParseFile(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ParseInto(TokenReader reader, KeyValuesNode parent, bool expectClosingBrace)
    {
        while (true)
        {
            var token = reader.Next();
            if (token is null)
            {
                return;
            }

            if (token == "}")
            {
                if (expectClosingBrace)
                {
                    return;
                }

                continue;
            }

            var key = token;
            var next = reader.Peek();
            if (next == "{")
            {
                reader.Next();
                var block = new KeyValuesNode(key);
                ParseInto(reader, block, expectClosingBrace: true);
                parent.Children.Add(block);
                continue;
            }

            if (next is null)
            {
                parent.Children.Add(new KeyValuesNode(key, string.Empty));
                return;
            }

            var value = reader.Next();
            if (value == "{")
            {
                // "key { ... }" без кавычек — тоже блок.
                var block = new KeyValuesNode(key);
                ParseInto(reader, block, expectClosingBrace: true);
                parent.Children.Add(block);
                continue;
            }

            parent.Children.Add(new KeyValuesNode(key, value));
        }
    }

    private sealed class TokenReader(string text)
    {
        private readonly string _text = text;
        private int _pos;
        private string? _peeked;
        private bool _hasPeeked;

        public string? Peek()
        {
            if (!_hasPeeked)
            {
                _peeked = Read();
                _hasPeeked = true;
            }

            return _peeked;
        }

        public string? Next()
        {
            if (_hasPeeked)
            {
                _hasPeeked = false;
                return _peeked;
            }

            return Read();
        }

        private string? Read()
        {
            SkipTrivia();
            if (_pos >= _text.Length)
            {
                return null;
            }

            var c = _text[_pos];
            if (c == '{' || c == '}')
            {
                _pos++;
                return c.ToString();
            }

            if (c == '"')
            {
                _pos++;
                var sb = new StringBuilder();
                while (_pos < _text.Length)
                {
                    var ch = _text[_pos++];
                    if (ch == '\\' && _pos < _text.Length)
                    {
                        var esc = _text[_pos++];
                        sb.Append(esc switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            '\\' => '\\',
                            '"' => '"',
                            _ => esc,
                        });
                        continue;
                    }

                    if (ch == '"')
                    {
                        return sb.ToString();
                    }

                    sb.Append(ch);
                }

                return sb.ToString();
            }

            var start = _pos;
            while (_pos < _text.Length)
            {
                var ch = _text[_pos];
                if (char.IsWhiteSpace(ch) || ch == '{' || ch == '}' || ch == '"')
                {
                    break;
                }

                _pos++;
            }

            return _pos > start ? _text[start.._pos] : null;
        }

        private void SkipTrivia()
        {
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (char.IsWhiteSpace(c) || c == '\uFEFF')
                {
                    _pos++;
                    continue;
                }

                if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                {
                    while (_pos < _text.Length && _text[_pos] != '\n')
                    {
                        _pos++;
                    }

                    continue;
                }

                break;
            }
        }
    }
}
