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

    [Theory]
    [InlineData(0)]
    [InlineData(0.01)]
    [InlineData(0.35)]
    [InlineData(1)]
    public void Normalize_KeepsFullTransparencyRange(double opacity)
    {
        var normalized = new ReaderSettings(Opacity: opacity).Normalize();

        Assert.Equal(opacity, normalized.Opacity);
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(1.5, 1)]
    [InlineData(double.NaN, 0.94)]
    public void Normalize_ClampsOpacityIntoSafeRange(double opacity, double expected)
    {
        var normalized = new ReaderSettings(Opacity: opacity).Normalize();

        Assert.Equal(expected, normalized.Opacity);
    }
}
