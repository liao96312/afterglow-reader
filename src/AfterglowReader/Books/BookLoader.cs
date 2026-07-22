using System.Net;
using System.IO;
using System.IO.Compression;
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
        var book = await Task.Run(() => ReadEpub(bytes), cancellationToken);
        var navigationTitles = GetUniqueNavigationTitles(book.Navigation ?? []);
        var chapters = new List<BookChapter>();
        for (var chapterIndex = 0; chapterIndex < book.ReadingOrder.Count; chapterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = book.ReadingOrder[chapterIndex];
            if (InjectedAdFileRegex().IsMatch(Path.GetFileName(content.FilePath)))
            {
                continue;
            }

            var paragraphs = HtmlContent.ToParagraphs(content.Content, $"ch-{chapterIndex}");
            if (paragraphs.Count > 0)
            {
                chapters.Add(new BookChapter(
                    $"ch-{chapterIndex}",
                    GuessChapterTitle(
                        content.Content,
                        paragraphs,
                        navigationTitles.GetValueOrDefault(NormalizeEpubPath(content.FilePath)),
                        chapters.Count),
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

    private static EpubBook ReadEpub(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return EpubReader.ReadBook(stream, EpubReaderOptionsPreset.RELAXED);
        }
        catch (Epub3NavException)
        {
            var fallback = TreatEpub3PackageAsEpub2(bytes);
            if (fallback is null)
            {
                throw;
            }

            using var stream = new MemoryStream(fallback, writable: false);
            return EpubReader.ReadBook(stream, EpubReaderOptionsPreset.RELAXED);
        }
    }

    private static byte[]? TreatEpub3PackageAsEpub2(byte[] bytes)
    {
        using var sourceStream = new MemoryStream(bytes, writable: false);
        using var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        var changed = false;
        using (var targetArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var sourceEntry in sourceArchive.Entries)
            {
                var compression = sourceEntry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var targetEntry = targetArchive.CreateEntry(sourceEntry.FullName, compression);
                using var source = sourceEntry.Open();
                using var target = targetEntry.Open();
                if (!sourceEntry.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                {
                    source.CopyTo(target);
                    continue;
                }

                using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var package = reader.ReadToEnd();
                var compatiblePackage = Epub3VersionRegex().Replace(package, "${1}2.0${2}", 1);
                changed |= compatiblePackage != package;
                target.Write(Encoding.UTF8.GetBytes(compatiblePackage));
            }
        }

        return changed ? output.ToArray() : null;
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

    private static string GuessChapterTitle(
        string html,
        IReadOnlyList<BookParagraph> paragraphs,
        string? navigationTitle,
        int fallbackIndex)
    {
        var match = HeadingRegex().Match(html);
        if (match.Success)
        {
            var heading = WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, "<[^>]+>", string.Empty)).Trim();
            if (heading.Length > 0)
            {
                return heading;
            }
        }

        var firstParagraph = paragraphs[0].PlainText.Trim();
        if (firstParagraph.Length <= 40)
        {
            return firstParagraph;
        }

        return string.IsNullOrWhiteSpace(navigationTitle) ? $"未命名内容 {fallbackIndex + 1}" : navigationTitle.Trim();
    }

    private static IReadOnlyDictionary<string, string> GetUniqueNavigationTitles(IEnumerable<EpubNavigationItem> navigation)
        => FlattenNavigation(navigation)
            .Where(item => item.Link is not null && !string.IsNullOrWhiteSpace(item.Title))
            .GroupBy(item => NormalizeEpubPath(item.Link!.ContentFilePath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Title, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<EpubNavigationItem> FlattenNavigation(IEnumerable<EpubNavigationItem> navigation)
    {
        foreach (var item in navigation)
        {
            yield return item;
            foreach (var nestedItem in FlattenNavigation(item.NestedItems))
            {
                yield return nestedItem;
            }
        }
    }

    private static string NormalizeEpubPath(string path)
        => path.Replace('\\', '/').TrimStart('/');

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

    [GeneratedRegex("(\\bversion\\s*=\\s*[\"'])3\\.0([\"'])", RegexOptions.IgnoreCase)]
    private static partial Regex Epub3VersionRegex();

    [GeneratedRegex("^ad_chapter\\d*\\.xhtml$", RegexOptions.IgnoreCase)]
    private static partial Regex InjectedAdFileRegex();
}
