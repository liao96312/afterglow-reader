using AfterglowReader.Persistence;

namespace AfterglowReader.Tests;

public sealed class ReaderSettingsTests
{
    [Fact]
    public void Normalize_RejectsInvalidTypographyValues()
    {
        var settings = new ReaderSettings(
            FontWeight: "900",
            TextColor: "red",
            LetterSpacing: 99);

        var normalized = settings.Normalize();

        Assert.Equal("400", normalized.FontWeight);
        Assert.Equal("#2a2521", normalized.TextColor);
        Assert.Equal(5, normalized.LetterSpacing);
    }
}
