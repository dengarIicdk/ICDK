using System.Text.Json;

namespace Wibe.Installer;

/// <summary>
/// Разбор сообщений окна установщика. WebView2 отдаёт JSON либо объектом, либо строкой
/// (когда страница зовёт postMessage с JSON.stringify), поэтому разворачиваем оба случая.
/// Одна точка правды: живое окно и тест прогоняют через неё ровно тот JSON, что приходит из окна.
/// </summary>
public static class InstallerBridge
{
    public sealed record Message(
        string Type,
        string? Path,
        string? Dir,
        string? Dota,
        string? Cache,
        bool Desktop,
        bool StartMenu,
        bool Run);

    public static Message? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var outer = JsonDocument.Parse(json);
            var root = outer.RootElement;

            // "{\"type\":\"install\",...}" — строка с JSON внутри
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                {
                    return null;
                }

                using var nested = JsonDocument.Parse(inner);
                return Read(nested.RootElement);
            }

            return Read(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Message? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? Text(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        bool Flag(string name, bool fallback) =>
            root.TryGetProperty(name, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => fallback,
                }
                : fallback;

        var type = Text("type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return new Message(
            type!,
            Text("path"),
            Text("dir"),
            Text("dota"),
            Text("cache"),
            Flag("desktop", true),
            Flag("startMenu", true),
            Flag("run", true));
    }
}
