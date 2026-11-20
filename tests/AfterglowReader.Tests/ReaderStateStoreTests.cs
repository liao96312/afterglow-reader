using System.IO;
using System.Text.Json;
using AfterglowReader.Persistence;

namespace AfterglowReader.Tests;

public sealed class ReaderStateStoreTests
{
    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.35, true)]
    [InlineData(0.94, true)]
    [InlineData(1.0, true)]
    public void Normalize_DerivesOpaquePageFromOpacity(double opacity, bool expectedOpaquePage)
    {
        var normalized = new ReaderSettings(Opacity: opacity).Normalize();

        Assert.Equal(opacity, normalized.Opacity);
        Assert.Equal(expectedOpaquePage, normalized.OpaquePage);
    }

    [Fact]
    public async Task LoadSettingsAsync_MigratesLegacyFloorAlphaToFullTransparency()
    {
        using var folder = new TempFolder();
        // v2 stored the alpha floor (0.35) whenever the user asked for "as transparent as possible".
        WriteLegacySettings(folder.Path, schemaVersion: 2, opacity: 0.35, opaquePage: true);
        var store = new ReaderStateStore(folder.Path);

        var settings = await store.LoadSettingsAsync();

        Assert.Equal(0, settings.Opacity);
        Assert.False(settings.OpaquePage);
    }

    [Fact]
    public async Task LoadSettingsAsync_KeepsDeliberateAlpha()
    {
        using var folder = new TempFolder();
        WriteLegacySettings(folder.Path, schemaVersion: 2, opacity: 0.8, opaquePage: true);
        var store = new ReaderStateStore(folder.Path);

        var settings = await store.LoadSettingsAsync();

        Assert.Equal(0.8, settings.Opacity, 3);
    }

    [Fact]
    public async Task RoundTrip_KeepsTransparentSettingsAndProgress()
    {
        using var folder = new TempFolder();
        var settings = new ReaderSettings(Opacity: 0, OpaquePage: false, LastBookPath: @"D:\ban\book.txt").Normalize();
        var store = new ReaderStateStore(folder.Path);

        await store.SaveSettingsAsync(settings);
        await store.SaveProgressAsync(new[]
        {
            new BookProgress(@"D:\ban\book.txt", "ch-325-29", -99.40625, DateTimeOffset.UnixEpoch)
        });

        var reloadedStore = new ReaderStateStore(folder.Path);
        var reloaded = await reloadedStore.LoadSettingsAsync();
        var progress = await reloadedStore.LoadProgressAsync();

        Assert.Equal(0, reloaded.Opacity);
        Assert.False(reloaded.OpaquePage);
        Assert.Equal(@"D:\ban\book.txt", reloaded.LastBookPath);
        var entry = Assert.Single(progress);
        Assert.Equal("ch-325-29", entry.ParagraphId);
        Assert.Equal(-99.40625, entry.ParagraphOffset, 5);
    }

    private static void WriteLegacySettings(string root, int schemaVersion, double opacity, bool opaquePage)
    {
        Directory.CreateDirectory(root);
        var payload = new
        {
            schemaVersion,
            data = new
            {
                fontFamily = "Microsoft YaHei",
                fontSize = 20,
                lineHeight = 1.9,
                opacity,
                scrollPixelsPerSecond = 340,
                fontWeight = "400",
                textColor = "#f2f2f2",
                letterSpacing = 0,
                opaquePage,
                lastBookPath = @"D:\ban\book.txt"
            }
        };
        File.WriteAllText(
            Path.Combine(root, "settings.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "afterglow-tests-" + Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
