using System.Text;

namespace Wibe.Core.Vdf;

/// <summary>
/// Сериализация KeyValues в формат Valve: табы, кавычки, вложенные блоки.
/// Нужен, чтобы отдать движку изменённый items_game.txt.
/// </summary>
public static class KeyValuesWriter
{
    public static string Serialize(KeyValuesNode root, bool useTabs = true)
    {
        var builder = new StringBuilder(4 * 1024 * 1024);
        foreach (var child in root.Children)
        {
            WriteNode(builder, child, 0, useTabs);
        }

        return builder.ToString();
    }

    public static void Save(KeyValuesNode root, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(root), new UTF8Encoding(false));
    }

    private static void WriteNode(StringBuilder builder, KeyValuesNode node, int depth, bool useTabs)
    {
        var indent = useTabs ? new string('\t', depth) : new string(' ', depth * 4);
        if (node.IsBlock)
        {
            builder.Append(indent).Append('"').Append(Escape(node.Key)).Append("\"\n");
            builder.Append(indent).Append("{\n");
            foreach (var child in node.Children)
            {
                WriteNode(builder, child, depth + 1, useTabs);
            }

            builder.Append(indent).Append("}\n");
            return;
        }

        builder.Append(indent)
            .Append('"').Append(Escape(node.Key)).Append('"')
            .Append('\t').Append('\t')
            .Append('"').Append(Escape(node.Value ?? string.Empty)).Append('"')
            .Append('\n');
    }

    private static string Escape(string value) =>
        value.IndexOfAny(new[] { '"', '\\' }) < 0
            ? value
            : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
