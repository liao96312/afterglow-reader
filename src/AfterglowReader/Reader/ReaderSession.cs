using AfterglowReader.Books;

namespace AfterglowReader.Reader;

/// <summary>
/// Keeps the full parsed document in the application and exposes only a small
/// chapter window to the WebView2 renderer.
/// </summary>
public sealed class ReaderSession
{
    public const int WindowSize = 3;

    public ReaderSession(BookDocument book)
    {
        Book = book;
    }

    public BookDocument Book { get; }

    public int WindowStart { get; private set; }

    public BookDocument CurrentWindow
        // ponytail: full-book DOM favors reliable reading; restore paging only after profiling proves it necessary.
        => Book;

    public bool MoveWindow(int direction)
        => false;

    public bool RestoreToParagraph(string? paragraphId)
    {
        if (string.IsNullOrWhiteSpace(paragraphId))
        {
            return false;
        }

        var chapterIndex = -1;
        for (var index = 0; index < Book.Chapters.Count; index++)
        {
            if (Book.Chapters[index].Paragraphs.Any(paragraph => string.Equals(paragraph.Id, paragraphId, StringComparison.Ordinal)))
            {
                chapterIndex = index;
                break;
            }
        }
        if (chapterIndex < 0)
        {
            return false;
        }

        var maxStart = Math.Max(0, Book.Chapters.Count - WindowSize);
        WindowStart = Math.Clamp(chapterIndex - 1, 0, maxStart);
        return true;
    }

    public bool JumpToChapter(string? chapterId)
    {
        if (string.IsNullOrWhiteSpace(chapterId))
        {
            return false;
        }

        var chapterIndex = -1;
        for (var index = 0; index < Book.Chapters.Count; index++)
        {
            if (string.Equals(Book.Chapters[index].Id, chapterId, StringComparison.Ordinal))
            {
                chapterIndex = index;
                break;
            }
        }

        if (chapterIndex < 0)
        {
            return false;
        }

        var maxStart = Math.Max(0, Book.Chapters.Count - WindowSize);
        WindowStart = Math.Clamp(chapterIndex - 1, 0, maxStart);
        return true;
    }

    public string? GetChapterAnchor(string? chapterId)
        => Book.Chapters.FirstOrDefault(chapter => string.Equals(chapter.Id, chapterId, StringComparison.Ordinal))
            ?.Paragraphs.FirstOrDefault()?.Id;

    public string? GetChapterIdForParagraph(string? paragraphId)
        => Book.Chapters.FirstOrDefault(chapter => chapter.Paragraphs.Any(paragraph => string.Equals(paragraph.Id, paragraphId, StringComparison.Ordinal)))?.Id;
}
