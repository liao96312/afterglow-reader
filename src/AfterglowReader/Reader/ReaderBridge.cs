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
    public static ReaderMessage? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement))
        {
            return null;
        }

        return typeElement.GetString() switch
        {
            "readerReady" => new ReaderReadyMessage(),
            "requestWindow" => new WindowRequestMessage(
                GetInt32(root, "direction"),
                GetString(root, "anchorId"),
                GetDouble(root, "anchorOffset")),
            "progressChanged" => new ProgressChangedMessage(
                GetString(root, "paragraphId"),
                GetDouble(root, "offset"),
                GetInt64(root, "sequence")),
            "selectChapter" => new ChapterSelectionMessage(GetString(root, "chapterId")),
            "openFile" => new OpenFileMessage(),
            "beginWindowDrag" => new WindowDragMessage(),
            "beginWindowResize" => new WindowResizeMessage(GetString(root, "edge")),
            "readerPointerEntered" => new ReaderPointerEnteredMessage(),
            "settingsChanged" => new SettingsChangedMessage(GetString(root, "fontFamily"), GetDouble(root, "fontSize"), GetDouble(root, "lineHeight"), GetDouble(root, "opacity"), GetDouble(root, "scrollPixelsPerSecond")),
            _ => null
        };
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) ? element.GetString() : null;

    private static int GetInt32(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.TryGetInt32(out var value) ? value : 0;

    private static long GetInt64(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.TryGetInt64(out var value) ? value : 0;

    private static double GetDouble(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.TryGetDouble(out var value) ? value : 0;
}
