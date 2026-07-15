using System.IO;

namespace AfterglowReader.Reader;

internal static class ReaderAssetLoader
{
    public static string LoadHtml()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "Reader");
        var html = File.ReadAllText(Path.Combine(root, "reader.html"));
        var css = File.ReadAllText(Path.Combine(root, "reader.css"));
        var js = File.ReadAllText(Path.Combine(root, "reader.js"));
        return html
            .Replace("/*__READER_CSS__*/", css, StringComparison.Ordinal)
            .Replace("/*__READER_JS__*/", js, StringComparison.Ordinal);
    }
}
