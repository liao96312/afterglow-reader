namespace AfterglowReader.Books;

public sealed record BookDocument(
    string SourcePath,
    string Title,
    IReadOnlyList<BookChapter> Chapters)
{
    public int TotalCharacters => Chapters.Sum(chapter => chapter.CharacterCount);
}

public sealed record BookChapter(
    string Id,
    string Title,
    IReadOnlyList<BookParagraph> Paragraphs)
{
    public int CharacterCount => Paragraphs.Sum(paragraph => paragraph.PlainText.Length);
}

public sealed record BookParagraph(
    string Id,
    string Html,
    string PlainText);

public sealed class BookReaderException : Exception
{
    public BookReaderException(string message) : base(message)
    {
    }

    public BookReaderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
