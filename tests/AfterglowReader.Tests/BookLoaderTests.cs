using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AfterglowReader.Books;
using AfterglowReader.Persistence;
using AfterglowReader.Reader;

namespace AfterglowReader.Tests;

public sealed class BookLoaderTests
{
    [Fact]
    public void ImportedHtmlIsRenderedAsEncodedText()
    {
        var paragraphs = HtmlContent.ToParagraphs(
            "<p onclick=\"alert(1)\">正文<script>alert(2)</script><img src=\"x\"></p>",
            "ch-0");

        var paragraph = Assert.Single(paragraphs);
        Assert.Equal("正文", paragraph.PlainText);
        Assert.DoesNotContain("<script", paragraph.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", paragraph.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", paragraph.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadsUtf8TextAndCreatesStableParagraphIds()
    {
        var path = CreateTempFile("小说.txt", "第一章\n\n第一段\n第二段\n\n第二章\n第三段");
        try
        {
            var book = await BookLoader.LoadAsync(path);

            Assert.Equal(2, book.Chapters.Count);
            Assert.Equal("第一章", book.Chapters[0].Title);
            Assert.Equal("ch-0-0", book.Chapters[0].Paragraphs[0].Id);
            Assert.Contains("第一段", book.Chapters[0].Paragraphs.Select(paragraph => paragraph.PlainText));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FallsBackToGb18030ForChineseText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = CreateTempFile("gb.txt", Encoding.GetEncoding("GB18030").GetBytes("第一章\n中文内容"));
        try
        {
            var book = await BookLoader.LoadTextAsync(path);

            Assert.Contains("中文内容", book.Chapters.SelectMany(chapter => chapter.Paragraphs).Select(paragraph => paragraph.PlainText));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public async Task LoadsMinimalEpubReadingOrder(string packageVersion)
    {
        var path = Path.Combine(Path.GetTempPath(), $"afterglow-{Guid.NewGuid():N}.epub");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip");
            WriteEntry(archive, "META-INF/container.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            WriteEntry(archive, "OEBPS/content.opf", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <package version="{{packageVersion}}" xmlns="http://www.idpf.org/2007/opf" unique-identifier="bookid">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>测试 EPUB</dc:title><dc:language>zh-CN</dc:language><dc:identifier id="bookid">test</dc:identifier></metadata>
                  <manifest>
                    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                    <item id="cover" href="cover.xhtml" media-type="application/xhtml+xml"/>
                    <item id="intro" href="intro.xhtml" media-type="application/xhtml+xml"/>
                    <item id="ad" href="ad_chapter12.xhtml" media-type="application/xhtml+xml"/>
                    <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine toc="ncx"><itemref idref="cover" linear="no"/><itemref idref="intro"/><itemref idref="ad"/><itemref idref="chapter"/></spine>
                </package>
                """);
            WriteEntry(archive, "OEBPS/toc.ncx", """
                <?xml version="1.0" encoding="UTF-8"?>
                <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head/><docTitle><text>测试 EPUB</text></docTitle><navMap><navPoint id="chapter" playOrder="1"><navLabel><text>第一章</text></navLabel><content src="chapter.xhtml"/></navPoint></navMap></ncx>
                """);
            WriteEntry(archive, "OEBPS/cover.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><img src=\"cover.jpg\"/></body></html>");
            WriteEntry(archive, "OEBPS/intro.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>作者介绍</p><p>作者生平。</p></body></html>");
            WriteEntry(archive, "OEBPS/ad_chapter12.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>广告内容</p></body></html>");
            WriteEntry(archive, "OEBPS/chapter.xhtml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <html xmlns="http://www.w3.org/1999/xhtml"><body><p><b>1</b></p><p>EPUB 正文</p></body></html>
                """);
        }

        try
        {
            var book = await BookLoader.LoadEpubAsync(path);

            Assert.Equal("测试 EPUB", book.Title);
            Assert.Equal(["作者介绍", "1"], book.Chapters.Select(chapter => chapter.Title));
            Assert.Equal(["ch-1", "ch-3"], book.Chapters.Select(chapter => chapter.Id));
            Assert.Equal("ch-1-0", book.Chapters[0].Paragraphs[0].Id);
            Assert.Contains("EPUB 正文", book.Chapters[1].Paragraphs.Select(paragraph => paragraph.PlainText));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsUnsupportedExtension()
    {
        var path = CreateTempFile("book.pdf", "not supported");
        try
        {
            var error = await Assert.ThrowsAsync<BookReaderException>(() => BookLoader.LoadAsync(path));
            Assert.Contains("TXT、EPUB 和 MOBI", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReportsInvalidMobiInsteadOfCrashing()
    {
        var path = CreateTempFile("broken.mobi", "not a mobi");
        try
        {
            var error = await Assert.ThrowsAsync<BookReaderException>(() => BookLoader.LoadMobiAsync(path));
            Assert.Contains("未加密", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HonorsCancellationBeforeTextParsing()
    {
        var path = CreateTempFile("cancel.txt", "第一行\n第二行");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => BookLoader.LoadTextAsync(path, cancellation.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PersistsSettingsAndProgressAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"afterglow-state-{Guid.NewGuid():N}");
        try
        {
            var store = new ReaderStateStore(root);
            await store.SaveSettingsAsync(new ReaderSettings(
                FontSize: 100,
                WindowLeft: -120,
                WindowTop: 48,
                WindowWidth: 860,
                WindowHeight: 460,
                LastBookPath: @"C:\Books\last-book.txt"));
            await store.SaveProgressAsync([new BookProgress("book.txt", "ch-0-1", 12.5, DateTimeOffset.UtcNow)]);

            var settings = await store.LoadSettingsAsync();
            var progress = await store.LoadProgressAsync();
            Assert.Equal(64, settings.FontSize);
            Assert.Equal(-120, settings.WindowLeft);
            Assert.Equal(48, settings.WindowTop);
            Assert.Equal(860, settings.WindowWidth);
            Assert.Equal(460, settings.WindowHeight);
            Assert.Equal(@"C:\Books\last-book.txt", settings.LastBookPath);
            Assert.Single(progress);
            Assert.Equal("ch-0-1", progress[0].ParagraphId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SerializesConcurrentStateWritesWithoutLeavingTempFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"afterglow-state-race-{Guid.NewGuid():N}");
        try
        {
            var store = new ReaderStateStore(root);
            var writes = Enumerable.Range(0, 20)
                .Select(index => store.SaveProgressAsync([
                    new BookProgress($"book-{index}.txt", $"ch-{index}", index, DateTimeOffset.UtcNow)
                ]));

            await Task.WhenAll(writes);

            var progress = await store.LoadProgressAsync();
            Assert.Single(progress);
            Assert.DoesNotContain(Directory.EnumerateFiles(root), path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task KeepsProgressIndependentForMultipleBooks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"afterglow-state-books-{Guid.NewGuid():N}");
        try
        {
            var store = new ReaderStateStore(root);
            await store.SaveProgressAsync([
                new BookProgress("first.txt", "ch-1", 10, DateTimeOffset.UtcNow),
                new BookProgress("second.txt", "ch-8", 80, DateTimeOffset.UtcNow)
            ]);

            var progress = await store.LoadProgressAsync();
            Assert.Equal("ch-1", progress.Single(item => item.Path == "first.txt").ParagraphId);
            Assert.Equal("ch-8", progress.Single(item => item.Path == "second.txt").ParagraphId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadsLegacyStateAndWritesVersionedEnvelope()
    {
        var root = Path.Combine(Path.GetTempPath(), $"afterglow-state-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"fontSize\":28,\"lastBookPath\":\"C:\\\\Books\\\\legacy.txt\"}");
            await File.WriteAllTextAsync(Path.Combine(root, "progress.json"), "[{\"path\":\"C:\\\\Books\\\\legacy.txt\",\"paragraphId\":\"ch-0-3\",\"paragraphOffset\":8,\"updatedAt\":\"2026-07-15T00:00:00+00:00\"}]");

            var store = new ReaderStateStore(root);
            var settings = await store.LoadSettingsAsync();
            var progress = await store.LoadProgressAsync();
            Assert.Equal(28, settings.FontSize);
            Assert.Equal(@"C:\Books\legacy.txt", settings.LastBookPath);
            Assert.Equal("ch-0-3", Assert.Single(progress).ParagraphId);

            await store.SaveSettingsAsync(settings);
            await store.SaveProgressAsync(progress);
            using var settingsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "settings.json")));
            using var progressDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "progress.json")));
            Assert.Equal(2, settingsDocument.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(2, progressDocument.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(settingsDocument.RootElement.TryGetProperty("data", out _));
            Assert.True(progressDocument.RootElement.TryGetProperty("data", out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BacksUpCorruptProgressAndReturnsEmptyState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"afterglow-state-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "progress.json"), "{not-json");
            var progress = await new ReaderStateStore(root).LoadProgressAsync();

            Assert.Empty(progress);
            Assert.Single(Directory.EnumerateFiles(root, "progress.json.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LeavesUnknownFutureSchemaUntouchedAndFallsBack()
    {
        var root = Path.Combine(Path.GetTempPath(), $"afterglow-state-future-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        const string futureState = "{\"schemaVersion\":99,\"data\":{\"fontSize\":36}}";
        try
        {
            await File.WriteAllTextAsync(path, futureState);

            var settings = await new ReaderStateStore(root).LoadSettingsAsync();

            Assert.Equal(20, settings.FontSize);
            Assert.Equal(futureState, await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotPersistTransientReaderInteractionState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"afterglow-state-transient-{Guid.NewGuid():N}");
        try
        {
            var store = new ReaderStateStore(root);
            await store.SaveSettingsAsync(new ReaderSettings(LastBookPath: @"C:\Books\current.txt"));

            var persisted = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
            Assert.DoesNotContain("hidden", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("clickThrough", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("autoScroll", persisted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReaderSessionRendersTheFullBookForContinuousReading()
    {
        var chapters = Enumerable.Range(0, 5)
            .Select(index => new BookChapter($"ch-{index}", $"Chapter {index}", [HtmlContentForTest(index)]))
            .ToArray();
        var session = new ReaderSession(new BookDocument("book.txt", "Book", chapters));

        Assert.Equal(5, session.CurrentWindow.Chapters.Count);
        Assert.False(session.MoveWindow(1));
        Assert.False(session.MoveWindow(-1));
        Assert.True(session.RestoreToParagraph("p-4"));
        Assert.Equal(2, session.WindowStart);
        Assert.False(session.RestoreToParagraph("missing"));
        Assert.True(session.JumpToChapter("ch-1"));
        Assert.Equal(0, session.WindowStart);
        Assert.Equal("p-1", session.GetChapterAnchor("ch-1"));
        Assert.Equal("ch-1", session.GetChapterIdForParagraph("p-1"));
        Assert.False(session.JumpToChapter("missing"));
    }

    private static BookParagraph HtmlContentForTest(int index)
        => new($"p-{index}", $"<p>Paragraph {index}</p>", $"Paragraph {index}");

    private static string CreateTempFile(string fileName, string text)
        => CreateTempFile(fileName, Encoding.UTF8.GetBytes(text));

    private static string CreateTempFile(string fileName, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"afterglow-{Guid.NewGuid():N}-{fileName}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
