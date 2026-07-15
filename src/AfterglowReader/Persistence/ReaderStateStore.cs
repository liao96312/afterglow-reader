using System;
using System.IO;
using System.Text.Json;

namespace AfterglowReader.Persistence;

public sealed record ReaderSettings(
    string FontFamily = "Microsoft YaHei",
    double FontSize = 20,
    double LineHeight = 1.9,
    double Opacity = 0.94,
    double ScrollPixelsPerSecond = 220,
    double? WindowLeft = null,
    double? WindowTop = null,
    double? WindowWidth = null,
    double? WindowHeight = null)
{
    public ReaderSettings Normalize()
        => this with
        {
            FontSize = Math.Clamp(FontSize, 12, 64),
            LineHeight = Math.Clamp(LineHeight, 1.1, 3.5),
            Opacity = Math.Clamp(Opacity, 0.35, 1),
            ScrollPixelsPerSecond = Math.Clamp(ScrollPixelsPerSecond, 20, 2_000),
            WindowWidth = WindowWidth is { } width ? Math.Clamp(width, 360, 2_400) : null,
            WindowHeight = WindowHeight is { } height ? Math.Clamp(height, 240, 2_000) : null
        };
}

public sealed record BookProgress(string Path, string? ParagraphId, double ParagraphOffset, DateTimeOffset UpdatedAt);

public sealed class ReaderStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ReaderStateStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfterglowReader");
    }

    public Task SaveSettingsAsync(ReaderSettings settings, CancellationToken cancellationToken = default)
        => WriteJsonAtomicAsync("settings.json", settings.Normalize(), cancellationToken);

    public Task SaveProgressAsync(IEnumerable<BookProgress> progress, CancellationToken cancellationToken = default)
        => WriteJsonAtomicAsync("progress.json", progress.ToArray(), cancellationToken);

    public Task<ReaderSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        => ReadJsonAsync("settings.json", new ReaderSettings(), cancellationToken);

    public Task<IReadOnlyList<BookProgress>> LoadProgressAsync(CancellationToken cancellationToken = default)
        => ReadJsonAsync<IReadOnlyList<BookProgress>>("progress.json", Array.Empty<BookProgress>(), cancellationToken);

    private async Task WriteJsonAtomicAsync<T>(string fileName, T value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var target = Path.Combine(_root, fileName);
            var temp = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = File.Create(temp))
                {
                    await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(target))
                {
                    File.Replace(temp, target, null);
                }
                else
                {
                    File.Move(temp, target);
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<T> ReadJsonAsync<T>(string fileName, T fallback, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, fileName);
        if (!File.Exists(path))
        {
            return fallback;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
            return value ?? fallback;
        }
        catch (JsonException)
        {
            var backup = path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, backup, overwrite: true);
            return fallback;
        }
    }
}
