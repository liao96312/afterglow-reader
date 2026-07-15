using System.Windows;
using System.Windows.Interop;
using System.Text.Json;
using AfterglowReader.Books;
using AfterglowReader.Persistence;
using AfterglowReader.Reader;
using Microsoft.Web.WebView2.Core;
using PlatformNativeWindow = AfterglowReader.SystemIntegration.NativeWindow;
using AfterglowReader.SystemIntegration;

namespace AfterglowReader;

/// <summary>
/// P0 probe for the non-activating WebView2 window.
/// </summary>
public partial class MainWindow : Window
{
    private const int BossHotKeyId = 1001;
    private const int AutoScrollHotKeyId = 1002;
    private HwndSource? _hwndSource;
    private TrayService? _tray;
    private bool _bossHotKeyRegistered;
    private bool _autoScrollHotKeyRegistered;
    private bool _isHidden;
    private bool _clickThrough;
    private System.Windows.Threading.DispatcherTimer? _statusHideTimer;
    private readonly ReaderStateStore _stateStore = new();
    private readonly List<BookProgress> _progress = [];
    private readonly TaskCompletionSource<bool> _readerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private CancellationTokenSource? _progressSaveCts;
    private ReaderSession? _session;
    private string? _currentBookPath;
    private string? _selectedChapterId;
    private string? _lastAnchorId;
    private double _lastAnchorOffset;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        PlatformNativeWindow.ApplyToolWindow(hwnd);
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource.AddHook(WindowMessageHook);
        _bossHotKeyRegistered = PlatformNativeWindow.RegisterBossHotKey(hwnd, BossHotKeyId);
        _autoScrollHotKeyRegistered = PlatformNativeWindow.RegisterAutoScrollHotKey(hwnd, AutoScrollHotKeyId);
        StatusText.Text = _bossHotKeyRegistered
            ? "F8 隐藏/恢复 · 正在初始化 WebView2…"
            : "F8 注册失败 · 正在初始化 WebView2…";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
        _progress.AddRange(await _stateStore.LoadProgressAsync());
        _tray = new TrayService(
            ShowWithoutActivation,
            ToggleClickThrough,
            OpenFilePlaceholder,
            ShowSettingsPlaceholder,
            () => System.Windows.Application.Current.Shutdown());

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync();
            await ReaderView.EnsureCoreWebView2Async(environment);
            ReaderView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 247, 243, 234);
            ReaderView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ReaderView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ReaderView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ReaderView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            ReaderView.AllowExternalDrop = false;
            ReaderView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            ReaderView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            ReaderView.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
            ReaderView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ReaderView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    StatusPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ShowStatus($"WebView2 导航失败：{args.WebErrorStatus}");
                }
            };
            ReaderView.NavigateToString(ReaderAssetLoader.LoadHtml());
            await WaitForReaderReadyAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"WebView2 初始化失败：{exception.Message}";
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == PlatformNativeWindow.WmHotKey && wParam.ToInt32() == BossHotKeyId)
        {
            ToggleHidden();
            handled = true;
        }
        else if (message == PlatformNativeWindow.WmHotKey && wParam.ToInt32() == AutoScrollHotKeyId)
        {
            ToggleAutoScroll();
            handled = true;
        }
        else if (message == PlatformNativeWindow.WmNcHitTest && _clickThrough)
        {
            handled = true;
            return new IntPtr(PlatformNativeWindow.HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 12, workArea.Right - Width - 24);
        Top = Math.Max(workArea.Top + 12, workArea.Bottom - Height - 24);
    }

    private void ToggleHidden()
    {
        if (_isHidden)
        {
            ShowWithoutActivation();
        }
        else
        {
            HideImmediately();
        }
    }

    private void HideImmediately()
    {
        _isHidden = true;
        Hide();
    }

    private void ShowWithoutActivation()
    {
        _isHidden = false;
        if (!IsVisible)
        {
            Show();
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        PlatformNativeWindow.ShowWithoutActivation(hwnd, (int)Left, (int)Top, (int)Width, (int)Height);
    }

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        var hwnd = new WindowInteropHelper(this).Handle;
        PlatformNativeWindow.SetClickThrough(hwnd, _clickThrough);
        StatusText.Text = _clickThrough ? "鼠标穿透已开启 · F8 隐藏/恢复" : "鼠标穿透已关闭 · F8 隐藏/恢复";
    }

    private void OpenFilePlaceholder(object? sender, RoutedEventArgs e)
        => OpenFilePlaceholder();

    private async void OpenFilePlaceholder()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "电子书|*.txt;*.epub;*.mobi|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await SaveProgressNowAsync();
                StatusText.Text = $"正在读取 {System.IO.Path.GetFileName(dialog.FileName)}…";
                var book = await BookLoader.LoadAsync(dialog.FileName);
                _currentBookPath = dialog.FileName;
                _session = new ReaderSession(book);
                var saved = _progress.FirstOrDefault(item => string.Equals(item.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase));
                if (_session.RestoreToParagraph(saved?.ParagraphId))
                {
                    _lastAnchorId = saved?.ParagraphId;
                    _lastAnchorOffset = saved?.ParagraphOffset ?? 0;
                }
                else
                {
                    _lastAnchorId = null;
                    _lastAnchorOffset = 0;
                }
                _selectedChapterId = _session.GetChapterIdForParagraph(saved?.ParagraphId)
                    ?? _session.CurrentWindow.Chapters.FirstOrDefault()?.Id;
                await RenderChapterIndexAsync(book);
                await RenderWindowAsync(_session.CurrentWindow, _lastAnchorId, _lastAnchorOffset, _selectedChapterId);
            }
            catch (BookReaderException exception)
            {
                StatusText.Text = exception.Message;
            }
        }
    }

    private void ShowSettingsPlaceholder()
    {
        System.Windows.MessageBox.Show(this, "设置页将在 P5 接入。", "余光阅读器", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowStatus(string message, int hideAfterMilliseconds = 0)
    {
        _statusHideTimer?.Stop();
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
        if (hideAfterMilliseconds <= 0)
        {
            return;
        }

        _statusHideTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(hideAfterMilliseconds)
        };
        _statusHideTimer.Tick += (_, _) =>
        {
            _statusHideTimer?.Stop();
            StatusPanel.Visibility = Visibility.Collapsed;
        };
        _statusHideTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SaveProgressNowAsync().GetAwaiter().GetResult();
        _stateStore.SaveSettingsAsync(new ReaderSettings(Opacity: Opacity)).GetAwaiter().GetResult();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (_bossHotKeyRegistered)
        {
            PlatformNativeWindow.UnregisterBossHotKey(hwnd, BossHotKeyId);
        }
        if (_autoScrollHotKeyRegistered)
        {
            PlatformNativeWindow.UnregisterBossHotKey(hwnd, AutoScrollHotKeyId);
        }

        _hwndSource?.RemoveHook(WindowMessageHook);
        _tray?.Dispose();
        _statusHideTimer?.Stop();
        ReaderView.Dispose();
        _progressSaveCts?.Dispose();
        _renderGate.Dispose();
    }

    private async Task RenderChapterIndexAsync(BookDocument book)
    {
        if (ReaderView.CoreWebView2 is null)
        {
            return;
        }

        await WaitForReaderReadyAsync();
        var chapters = book.Chapters.Select(chapter => new { id = chapter.Id, title = chapter.Title }).ToArray();
        var payload = JsonSerializer.Serialize(chapters);
        await ReaderView.CoreWebView2.ExecuteScriptAsync($"window.setChapterIndex({payload});");
    }

    private async Task RenderWindowAsync(
        BookDocument book,
        string? anchorId = null,
        double anchorOffset = 0,
        string? selectedChapterId = null)
    {
        if (ReaderView.CoreWebView2 is null)
        {
            return;
        }

        await WaitForReaderReadyAsync();
        var payload = JsonSerializer.Serialize(new
        {
            book,
            anchorId,
            anchorOffset,
            selectedChapterId
        });
        var script = $"window.renderWindow({payload});";
        await ReaderView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void ToggleAutoScroll()
    {
        if (ReaderView.CoreWebView2 is not null)
        {
            _ = ReaderView.CoreWebView2.ExecuteScriptAsync("window.toggleAutoScroll?.();");
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = !IsReaderNavigation(args.Uri);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            switch (ReaderBridge.Parse(args.TryGetWebMessageAsString()))
            {
                case ReaderReadyMessage:
                    _readerReady.TrySetResult(true);
                    App.LogDiagnostic("Reader", "readerReady");
                    StatusPanel.Visibility = Visibility.Collapsed;
                    break;
                case WindowRequestMessage request:
                    _ = HandleWindowRequestAsync(request.Direction, request.AnchorId, request.AnchorOffset);
                    break;
                case ProgressChangedMessage progress:
                    RecordProgress(progress.ParagraphId, progress.Offset);
                    break;
                case ChapterSelectionMessage chapter:
                    _ = HandleChapterSelectionAsync(chapter.ChapterId);
                    break;
                case OpenFileMessage:
                    OpenFilePlaceholder();
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Ignore messages that are not part of the small reader bridge.
        }
    }

    private async Task HandleWindowRequestAsync(int direction, string? anchorId, double anchorOffset)
    {
        if (_session is null || direction == 0)
        {
            return;
        }

        await _renderGate.WaitAsync();
        try
        {
            if (_session.MoveWindow(direction))
            {
                await RenderWindowAsync(_session.CurrentWindow, anchorId, anchorOffset, _selectedChapterId);
            }
            else if (ReaderView.CoreWebView2 is not null)
            {
                await ReaderView.CoreWebView2.ExecuteScriptAsync("window.windowRequestHandled?.();");
            }
        }
        catch (Exception exception)
        {
            App.LogDiagnostic("Reader", $"window request failed: {exception.Message}");
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private async Task HandleChapterSelectionAsync(string? chapterId)
    {
        if (_session is null || !_session.JumpToChapter(chapterId))
        {
            return;
        }

        await _renderGate.WaitAsync();
        try
        {
            await SaveProgressNowAsync();
            _selectedChapterId = chapterId;
            _lastAnchorId = _session.GetChapterAnchor(chapterId);
            _lastAnchorOffset = 0;
            RecordProgress(_lastAnchorId, 0);
            await RenderWindowAsync(_session.CurrentWindow, _lastAnchorId, 0, _selectedChapterId);
        }
        catch (Exception exception)
        {
            App.LogDiagnostic("Reader", $"chapter selection failed: {exception.Message}");
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private async Task WaitForReaderReadyAsync()
    {
        if (_readerReady.Task.IsCompleted)
        {
            return;
        }

        var completed = await Task.WhenAny(_readerReady.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != _readerReady.Task)
        {
            throw new BookReaderException("阅读页未在 5 秒内完成初始化，请重试。");
        }
    }

    private void RecordProgress(string? paragraphId, double offset)
    {
        if (string.IsNullOrWhiteSpace(_currentBookPath) || string.IsNullOrWhiteSpace(paragraphId))
        {
            return;
        }

        _lastAnchorId = paragraphId;
        _lastAnchorOffset = offset;
        var value = new BookProgress(_currentBookPath, paragraphId, offset, DateTimeOffset.UtcNow);
        var existing = _progress.FindIndex(item => string.Equals(item.Path, _currentBookPath, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _progress[existing] = value;
        }
        else
        {
            _progress.Add(value);
        }

        _progressSaveCts?.Cancel();
        _progressSaveCts?.Dispose();
        _progressSaveCts = new CancellationTokenSource();
        _ = SaveProgressAfterDelayAsync(_progressSaveCts.Token);
    }

    private async Task SaveProgressAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            await _stateStore.SaveProgressAsync(_progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer scroll position superseded this pending save.
        }
    }

    private async Task SaveProgressNowAsync()
    {
        _progressSaveCts?.Cancel();
        if (_currentBookPath is not null)
        {
            var value = new BookProgress(_currentBookPath, _lastAnchorId, _lastAnchorOffset, DateTimeOffset.UtcNow);
            var existing = _progress.FindIndex(item => string.Equals(item.Path, _currentBookPath, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) _progress[existing] = value; else _progress.Add(value);
        }

        await _stateStore.SaveProgressAsync(_progress);
    }

    private static bool IsReaderNavigation(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return true;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && (string.Equals(parsed.Scheme, "about", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parsed.Scheme, "data", StringComparison.OrdinalIgnoreCase));
    }

}
