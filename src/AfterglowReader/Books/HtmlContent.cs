using System.Net;
using System.Text.RegularExpressions;

namespace AfterglowReader.Books;

internal static partial class HtmlContent
{
    private static readonly string[] BlockTags =
    ["p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote", "pre"];

    internal static IReadOnlyList<BookParagraph> ToParagraphs(string source, string idPrefix)
    {
        var withoutUnsafeBlocks = UnsafeBlockRegex().Replace(source, string.Empty);
        var candidates = BlockRegex().Matches(withoutUnsafeBlocks)
            .Select(match => (Tag: match.Groups[1].Value, Inner: match.Groups[2].Value))
            .Where(item => BlockTags.Contains(item.Tag, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            var text = StripTags(withoutUnsafeBlocks).Trim();
            return text.Length == 0
                ? []
                : [CreateParagraph($"{idPrefix}-0", text)];
        }

        return candidates
            .Select((item, index) =>
            {
                var plainText = WebUtility.HtmlDecode(StripTags(item.Inner)).Trim();
                var html = SanitizeInline(item.Inner, item.Tag);
                return plainText.Length == 0 ? null : new BookParagraph($"{idPrefix}-{index}", html, plainText);
            })
            .Where(paragraph => paragraph is not null)
            .Cast<BookParagraph>()
            .ToArray();
    }

    internal static BookParagraph CreateParagraph(string id, string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var html = WebUtility.HtmlEncode(normalized).Replace("\n", "<br>", StringComparison.Ordinal);
        return new BookParagraph(id, $"<p>{html}</p>", normalized);
    }

    private static string SanitizeInline(string inner, string tag)
    {
        var safe = UnsafeTagRegex().Replace(inner, string.Empty);
        safe = EventAttributeRegex().Replace(safe, string.Empty);
        safe = AttributeRegex().Replace(safe, string.Empty);
        return $"<{tag.ToLowerInvariant()}>{safe.Trim()}</{tag.ToLowerInvariant()}>";
    }

    private static string StripTags(string source)
        => WebUtility.HtmlDecode(TagRegex().Replace(source, " "));

    [GeneratedRegex("<(script|style|iframe|object|embed|form|svg|math)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UnsafeBlockRegex();

    [GeneratedRegex("<\\s*(p|div|h[1-6]|li|blockquote|pre)\\b[^>]*>(.*?)<\\s*/\\s*\\1\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex("</?(script|style|iframe|object|embed|form|svg|math)\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeTagRegex();

    [GeneratedRegex("\\s+on[a-z]+\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttributeRegex();

    [GeneratedRegex("\\s+[a-zA-Z_:][-a-zA-Z0-9_:.]*(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+))?")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}
