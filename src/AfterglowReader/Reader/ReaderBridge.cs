using System.Text.Json;

namespace AfterglowReader.Reader;

internal abstract record ReaderMessage;

internal sealed record ReaderReadyMessage : ReaderMessage;

internal sealed record WindowRequestMessage(int Direction, string? AnchorId, double AnchorOffset) : ReaderMessage;

internal sealed record ProgressChangedMessage(string? ParagraphId, double Offset, long Sequence) : ReaderMessage;

internal sealed record ChapterSelectionMessage(string? ChapterId) : ReaderMessage;

internal sealed record OpenFileMessage : ReaderMessage;

internal sealed record WindowDragMessage : ReaderMessage;

internal sealed record WindowResizeMessage(string? Edge) : ReaderMessage;

internal sealed record ReaderPointerEnteredMessage : ReaderMessage;
internal sealed record SettingsChangedMessage(string? FontFamily, double FontSize, double LineHeight, double Opacity, double ScrollPixelsPerSecond) : ReaderMessage;

internal static class ReaderBridge
{
    private const int MaxStringLength = 256;

    public static ReaderMessage? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out var type))
            {
                return null;
            }

            return type switch
            {
                "readerReady" => new ReaderReadyMessage(),
                "requestWindow" when TryGetDirection(root, out var direction)
                    && TryGetFiniteDouble(root, "anchorOffset", out var anchorOffset)
                    && TryGetOptionalString(root, "anchorId", out var anchorId)
                    => new WindowRequestMessage(direction, anchorId, anchorOffset),
                "progressChanged" when TryGetOptionalString(root, "paragraphId", out var paragraphId)
                    && TryGetFiniteDouble(root, "offset", out var offset)
                    && TryGetInt64(root, "sequence", out var sequence)
                    && sequence >= 0
                    => new ProgressChangedMessage(paragraphId, offset, sequence),
                "selectChapter" when TryGetString(root, "chapterId", out var chapterId)
                    => new ChapterSelectionMessage(chapterId),
                "openFile" => new OpenFileMessage(),
                "beginWindowDrag" => new WindowDragMessage(),
                "beginWindowResize" when TryGetString(root, "edge", out var edge)
                    && edge is "top" or "right" or "bottom" or "left" or "topLeft" or "topRight" or "bottomLeft" or "bottomRight"
                    => new WindowResizeMessage(edge),
                "readerPointerEntered" => new ReaderPointerEnteredMessage(),
                "settingsChanged" when TryGetString(root, "fontFamily", out var fontFamily)
                    && TryGetFiniteDouble(root, "fontSize", out var fontSize)
                    && TryGetFiniteDouble(root, "lineHeight", out var lineHeight)
                    && TryGetFiniteDouble(root, "opacity", out var opacity)
                    && TryGetFiniteDouble(root, "scrollPixelsPerSecond", out var speed)
                    => new SettingsChangedMessage(fontFamily, fontSize, lineHeight, opacity, speed),
                _ => null
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parsed = element.GetString();
        if (string.IsNullOrWhiteSpace(parsed) || parsed.Length > MaxStringLength)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetOptionalString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is null || value.Length <= MaxStringLength;
    }

    private static bool TryGetDirection(JsonElement root, out int value)
    {
        value = 0;
        return root.TryGetProperty("direction", out var element)
            && element.TryGetInt32(out value)
            && value is -1 or 1;
    }

    private static bool TryGetFiniteDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var element)
            && element.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static bool TryGetInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var element) && element.TryGetInt64(out value);
    }
}
