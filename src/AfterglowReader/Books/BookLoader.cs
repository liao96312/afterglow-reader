using System.Net;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Roler.Toolkit.File.Mobi;
using VersOne.Epub;
using VersOne.Epub.Options;

namespace AfterglowReader.Books;

public static partial class BookLoader
{
    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
    private static readonly Encoding Gb18030 = CreateGb18030();

    public static Task<BookDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new BookReaderException($"找不到书籍文件：{path}");
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" => LoadTextAsync(path, cancellationToken),
            ".epub" => LoadEpubAsync(path, cancellationToken),
            ".mobi" => LoadMobiAsync(path, cancellationToken),
            _ => throw new BookReaderException("当前仅支持 TXT、EPUB 和 MOBI 文件。")
        };
    }

    public static async Task<BookDocument> LoadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var text = DecodeText(bytes);
        var title = Path.GetFileNameWithoutExtension(path);
        return await Task.Run(() => BuildTextBook(path, title, text, cancellationToken), cancellationToken);
    }

    public static async Task<BookDocument> LoadEpubAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var book = await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return EpubReader.ReadBook(stream, EpubReaderOptionsPreset.RELAXED);
        }, cancellationToken);
        var chapters = new List<BookChapter>();
        for (var chapterIndex = 0; chapterIndex < book.ReadingOrder.Count; chapterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = book.ReadingOrder[chapterIndex];
            var paragraphs = HtmlContent.ToParagraphs(content.Content, $"ch-{chapterIndex}");
            if (paragraphs.Count > 0)
            {
                chapters.Add(new BookChapter(
                    $"ch-{chapterIndex}",
                    GuessChapterTitle(content.Content, chapterIndex),
                    paragraphs));
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (chapters.Count == 0)
        {
            throw new BookReaderException("EPUB 中没有可显示的正文。 ");
        }

        return new BookDocument(path, string.IsNullOrWhiteSpace(book.Title) ? Path.GetFileNameWithoutExtension(path) : book.Title, chapters);
    }

    public static async Task<BookDocument> LoadMobiAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var mobi = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(path);
                using var reader = new MobiReader(stream);
                return reader.Read();
            }, cancellationToken);

            if (mobi is null || string.IsNullOrWhiteSpace(mobi.Text))
            {
                throw new BookReaderException("MOBI 中没有可显示的正文。 ");
            }

            var title = string.IsNullOrWhiteSpace(mobi.Title) ? Path.GetFileNameWithoutExtension(path) : mobi.Title;
            var paragraphs = HtmlContent.ToParagraphs(mobi.Text, "mobi-0");
            cancellationToken.ThrowIfCancellationRequested();
            if (paragraphs.Count == 0)
            {
                var plain = Regex.Replace(mobi.Text, "<[^>]+>", " ", RegexOptions.Singleline).Trim();
                paragraphs = TextToParagraphs(plain, "mobi-0");
            }

            return new BookDocument(path, title, ChunkParagraphsIntoChapters(paragraphs, title));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BookReaderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new BookReaderException("MOBI 读取失败；仅支持未加密的 MOBI 文件。", exception);
        }
    }

    private static BookDocument BuildTextBook(string path, string title, string text, CancellationToken cancellationToken)
    {
        var chapters = SplitTextIntoChapters(text, cancellationToken);
        return new BookDocument(path, title, chapters);
    }

    private static IReadOnlyList<BookChapter> SplitTextIntoChapters(string text, CancellationToken cancellationToken)
    {
        var matches = ChapterHeadingRegex().Matches(text);
        if (matches.Count == 0)
        {
            return ChunkParagraphsIntoChapters(TextToParagraphs(text, "ch-0", cancellationToken), "正文", cancellationToken);
        }

        var chapters = new List<BookChapter>();
        for (var index = 0; index < matches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = matches[index].Index;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            var body = text[start..end].Trim();
            var title = matches[index].Value.Trim();
            var paragraphs = TextToParagraphs(body, $"ch-{index}", cancellationToken);
            if (paragraphs.Count > 0)
            {
                chapters.Add(new BookChapter($"ch-{index}", title, paragraphs));
            }
        }

        return chapters.Count == 0
            ? ChunkParagraphsIntoChapters(TextToParagraphs(text, "ch-0", cancellationToken), "正文", cancellationToken)
            : chapters;
    }

    private static IReadOnlyList<BookParagraph> TextToParagraphs(string text, string idPrefix, CancellationToken cancellationToken = default)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var paragraphs = new List<BookParagraph>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paragraphs.Add(HtmlContent.CreateParagraph($"{idPrefix}-{index}", lines[index]));
        }

        return paragraphs;
    }

    private static IReadOnlyList<BookChapter> ChunkParagraphsIntoChapters(
        IReadOnlyList<BookParagraph> paragraphs,
        string title,
        CancellationToken cancellationToken = default)
    {
        const int targetCharacters = 40_000;
        var chapters = new List<BookChapter>();
        var current = new List<BookParagraph>();
        var length = 0;
        var chapterIndex = 0;
        foreach (var paragraph in paragraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current.Add(paragraph with { Id = $"ch-{chapterIndex}-{current.Count}" });
            length += paragraph.PlainText.Length;
            if (length >= targetCharacters)
            {
                chapters.Add(new BookChapter($"ch-{chapterIndex}", $"{title} {chapterIndex + 1}", current.ToArray()));
                current = [];
                length = 0;
                chapterIndex++;
            }
        }

        if (current.Count > 0)
        {
            chapters.Add(new BookChapter($"ch-{chapterIndex}", chapters.Count == 0 ? title : $"{title} {chapterIndex + 1}", current.ToArray()));
        }

        return chapters;
    }

    private static string GuessChapterTitle(string html, int index)
    {
        var match = HeadingRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, "<[^>]+>", string.Empty)).Trim() : $"第 {index + 1} 章";
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Gb18030.GetString(bytes);
        }
    }

    private static Encoding CreateGb18030()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    [GeneratedRegex("^\\s*(第[0-9零一二三四五六七八九十百千万两]+[^\\r\\n]{0,40}(?:章|节|卷|篇|回)|序章|楔子).*$", RegexOptions.Multiline)]
    private static partial Regex ChapterHeadingRegex();

    [GeneratedRegex("<h[1-6]\\b[^>]*>(.*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingRegex();
}
