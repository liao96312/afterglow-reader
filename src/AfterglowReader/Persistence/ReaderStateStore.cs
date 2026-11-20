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
    string FontWeight = "400",
    string TextColor = "#2a2521",
    double LetterSpacing = 0,
    bool OpaquePage = false,
    double? WindowLeft = null,
    double? WindowTop = null,
    double? WindowWidth = null,
    double? WindowHeight = null,
    string? LastBookPath = null)
{
    public ReaderSettings Normalize()
    {
        var alpha = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 0.94, 0, 1);
        return this with
        {
            FontSize = Math.Clamp(FontSize, 12, 64),
            LineHeight = Math.Clamp(LineHeight, 1.1, 3.5),
            Opacity = alpha,
            // OpaquePage is derived from the alpha: 0 means "text only, no page fill".
            OpaquePage = alpha > 0,
            ScrollPixelsPerSecond = Math.Clamp(ScrollPixelsPerSecond, 20, 2_000),
            FontWeight = FontWeight is "400" or "500" or "600" or "700" ? FontWeight : "400",
            TextColor = IsHexColor(TextColor) ? TextColor.ToLowerInvariant() : "#2a2521",
            LetterSpacing = Math.Clamp(LetterSpacing, -1, 5),
            WindowWidth = WindowWidth is { } width ? Math.Clamp(width, 360, 2_400) : null,
            WindowHeight = WindowHeight is { } height ? Math.Clamp(height, 240, 2_000) : null,
            LastBookPath = string.IsNullOrWhiteSpace(LastBookPath) ? null : LastBookPath.Trim()
        };
    }

    private static bool IsHexColor(string? value)
        => value is { Length: 7 }
            && value[0] == '#'
            && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}

public sealed record BookProgress(string Path, string? ParagraphId, double ParagraphOffset, DateTimeOffset UpdatedAt);

public sealed class ReaderStateStore
{
    // v3 (2026-11-17): background alpha may reach 0, and OpaquePage is derived from it.
    private const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ReaderStateStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfterglowReader");
    }

    public Task SaveSettingsAsync(ReaderSettings settings, CancellationToken cancellationToken = default)
        => WriteJsonAtomicAsync("settings.json", new StateEnvelope<ReaderSettings>(CurrentSchemaVersion, settings.Normalize()), cancellationToken);

    public Task SaveProgressAsync(IEnumerable<BookProgress> progress, CancellationToken cancellationToken = default)
        => WriteJsonAtomicAsync("progress.json", new StateEnvelope<BookProgress[]>(CurrentSchemaVersion, progress.ToArray()), cancellationToken);

    public async Task<ReaderSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await ReadStateJsonAsync("settings.json", new ReaderSettings(), cancellationToken).ConfigureAwait(false);
        return MigrateSettings(loaded.Value, loaded.Version).Normalize();
    }

    public async Task<IReadOnlyList<BookProgress>> LoadProgressAsync(CancellationToken cancellationToken = default)
        => (await ReadStateJsonAsync<IReadOnlyList<BookProgress>>("progress.json", Array.Empty<BookProgress>(), cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Older builds clamped the background alpha to a 0.35 floor, so a saved 0.35 (or an unchecked
    /// "opaque page") is what a request for a fully transparent page was downgraded to. Restore that intent.
    /// </summary>
    private static ReaderSettings MigrateSettings(ReaderSettings settings, int version)
    {
        if (version >= CurrentSchemaVersion)
        {
            return settings;
        }

        var opacity = settings.OpaquePage && settings.Opacity > 0.35 ? settings.Opacity : 0;
        return settings with { Opacity = opacity, OpaquePage = opacity > 0 };
    }

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

                File.Move(temp, target, overwrite: true);
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

    private async Task<LoadedState<T>> ReadStateJsonAsync<T>(string fileName, T fallback, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, fileName);
        if (!File.Exists(path))
        {
            return new LoadedState<T>(fallback, CurrentSchemaVersion);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var versionElement))
            {
                // The original MVP shape is schema v1 and stores the value directly.
                return new LoadedState<T>(JsonSerializer.Deserialize<T>(root.GetRawText(), JsonOptions) ?? fallback, 1);
            }

            if (!versionElement.TryGetInt32(out var version) || version is < 1 or > CurrentSchemaVersion)
            {
                System.Diagnostics.Trace.TraceWarning($"AfterglowReader ignored unsupported state schema; file={fileName}; version={versionElement.GetRawText()}");
                return new LoadedState<T>(fallback, CurrentSchemaVersion);
            }

            if (!root.TryGetProperty("data", out var data))
            {
                throw new JsonException("State envelope is missing data.");
            }

            return new LoadedState<T>(JsonSerializer.Deserialize<T>(data.GetRawText(), JsonOptions) ?? fallback, version);
        }
        catch (JsonException)
        {
            var backup = path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, backup, overwrite: true);
            return new LoadedState<T>(fallback, CurrentSchemaVersion);
        }
    }

    private sealed record StateEnvelope<T>(int SchemaVersion, T Data);

    private readonly record struct LoadedState<T>(T Value, int Version);
}
