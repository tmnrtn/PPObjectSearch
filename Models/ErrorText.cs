using System.Text.Json;

namespace PPObjectSearch.Models;

/// <summary>An error message boiled down to one line for a list.</summary>
public static class ErrorText
{
    /// <summary>
    /// The message's first non-empty line. A cloud flow run's error is stored as JSON - a code and a
    /// message over several lines - so its message is read out first, or its code when there is none.
    /// </summary>
    public static string? FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var text = JsonMessage(message) ?? message;
        return text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static string? JsonMessage(string message)
    {
        if (!message.TrimStart().StartsWith('{')) return null;

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Either {"code", "message"} or the same wrapped in "error".
            var error = root.TryGetProperty("error", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
            return Text(error, "message") ?? Text(error, "code");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
