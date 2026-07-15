using AfterglowReader.SystemIntegration;

namespace AfterglowReader.Tests;

public sealed class WindowHitTestTests
{
    [Theory]
    [InlineData(100, 100, WindowHitTest.TopLeft)]
    [InlineData(899, 100, WindowHitTest.TopRight)]
    [InlineData(100, 699, WindowHitTest.BottomLeft)]
    [InlineData(899, 699, WindowHitTest.BottomRight)]
    [InlineData(100, 400, WindowHitTest.Left)]
    [InlineData(899, 400, WindowHitTest.Right)]
    [InlineData(500, 100, WindowHitTest.Top)]
    [InlineData(500, 699, WindowHitTest.Bottom)]
    [InlineData(140, 120, WindowHitTest.Caption)]
    [InlineData(500, 400, WindowHitTest.Client)]
    public void Resolve_ReturnsExpectedRegion(int x, int y, int expected)
    {
        var actual = WindowHitTest.Resolve(x, y, 100, 100, 900, 700, 8, 110, 44);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(8, 96, 8)]
    [InlineData(8, 144, 12)]
    [InlineData(8, 192, 16)]
    public void ScaleDip_UsesWindowDpi(int value, uint dpi, int expected)
        => Assert.Equal(expected, WindowHitTest.ScaleDip(value, dpi));

    [Theory]
    [InlineData("left", WindowHitTest.Left)]
    [InlineData("topRight", WindowHitTest.TopRight)]
    [InlineData("bottom", WindowHitTest.Bottom)]
    public void TryGetResizeRegion_MapsBrowserHandle(string edge, int expected)
    {
        Assert.True(WindowHitTest.TryGetResizeRegion(edge, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryGetResizeRegion_RejectsUnknownHandle()
        => Assert.False(WindowHitTest.TryGetResizeRegion("middle", out _));
}
