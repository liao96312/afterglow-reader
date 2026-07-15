using System.Windows;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private const int CtrlTabBossHotKeyId = 1003;
    private HwndSource? _hwndSource;
    private TrayService? _tray;
    private bool _bossHotKeyRegistered;
    private bool _autoScrollHotKeyRegistered;
    private bool _ctrlTabBossHotKeyRegistered;
    private bool _isHidden;
    private bool _clickThrough;
    private System.Windows.Threading.DispatcherTimer? _statusHideTimer;
    private readonly ReaderStateStore _stateStore = new();
    private readonly List<BookProgress> _progress = [];
    private readonly TaskCompletionSource<bool> _readerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private CancellationTokenSource? _progressSaveCts;
    private Task? _progressSaveTask;
    private CancellationTokenSource? _bookLoadCts;
    private Task? _shutdownTask;
    private bool _shutdownRequested;
    private bool _shutdownCompleted;
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
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource.AddHook(WindowMessageHook);
        _bossHotKeyRegistered = PlatformNativeWindow.RegisterBossHotKey(hwnd, BossHotKeyId);
        _autoScrollHotKeyRegistered = PlatformNativeWindow.RegisterAutoScrollHotKey(hwnd, AutoScrollHotKeyId);
        _ctrlTabBossHotKeyRegistered = PlatformNativeWindow.RegisterCtrlTabBossHotKey(hwnd, CtrlTabBossHotKeyId);
        App.LogDiagnostic("HotKey", $"CtrlTab={_ctrlTabBossHotKeyRegistered}; F8={_bossHotKeyRegistered}; F7={_autoScrollHotKeyRegistered}");
        StatusText.Text = _bossHotKeyRegistered || _ctrlTabBossHotKeyRegistered
            ? "F8 隐藏/恢复 · 正在初始化 WebView2…"
            : "F8 注册失败 · 正在初始化 WebView2…";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowSettings((await _stateStore.LoadSettingsAsync()).Normalize());
        _progress.AddRange(await _stateStore.LoadProgressAsync());
        _tray = new TrayService(
            DispatchTrayAction,
            ShowWithoutActivation,
            ToggleClickThrough,
            OpenFilePlaceholder,
            ShowSettingsPlaceholder,
            RequestShutdown);

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync();
            await ReaderView.EnsureCoreWebView2Async(environment);
            ReaderView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 247, 243, 234);
            ReaderView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
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
        if (_shutdownRequested || _shutdownCompleted)
        {
            if (message == PlatformNativeWindow.WmShowReader
                || message == PlatformNativeWindow.WmHotKey)
            {
                handled = true;
            }

            return IntPtr.Zero;
        }

        if (message == PlatformNativeWindow.WmHotKey && wParam.ToInt32() == BossHotKeyId)
        {
            ToggleHidden();
            handled = true;
        }
        else if (message == PlatformNativeWindow.WmHotKey && wParam.ToInt32() == CtrlTabBossHotKeyId)
        {
            ToggleHidden();
            handled = true;
        }
        else if (message == PlatformNativeWindow.WmHotKey && wParam.ToInt32() == AutoScrollHotKeyId)
        {
            ToggleAutoScroll();
            handled = true;
        }
        else if (message == PlatformNativeWindow.WmShowReader)
        {
            ShowWithoutActivation();
            handled = true;
        }
        else if (message == PlatformNativeWindow.WmNcHitTest)
        {
            if (_clickThrough)
            {
                handled = true;
                return new IntPtr(PlatformNativeWindow.HtTransparent);
            }

            return HitTestResizeBorder(lParam, ref handled);
        }

        return IntPtr.Zero;
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 12, workArea.Right - Width - 24);
        Top = Math.Max(workArea.Top + 12, workArea.Bottom - Height - 24);
    }

    private void RestoreWindowSettings(ReaderSettings settings)
    {
        Width = settings.WindowWidth ?? Width;
        Height = settings.WindowHeight ?? Height;
        Opacity = settings.Opacity;

        if (settings.WindowLeft is double left
            && settings.WindowTop is double top
            && IsFiniteWindowBounds(left, top, Width, Height)
            && IsVisibleOnAnyWorkArea(left, top, Width, Height))
        {
            Left = left;
            Top = top;
            return;
        }

        PositionBottomRight();
    }

    private static bool IsFiniteWindowBounds(double left, double top, double width, double height)
        => double.IsFinite(left)
            && double.IsFinite(top)
            && double.IsFinite(width)
            && double.IsFinite(height)
            && width >= 360
            && height >= 240;

    private static bool IsVisibleOnAnyWorkArea(double left, double top, double width, double height)
    {
        var right = left + width;
        var bottom = top + height;
        return System.Windows.Forms.Screen.AllScreens.Any(screen =>
        {
            var workArea = screen.WorkingArea;
            var visibleWidth = Math.Min(right, workArea.Right) - Math.Max(left, workArea.Left);
            var visibleHeight = Math.Min(bottom, workArea.Bottom) - Math.Max(top, workArea.Top);
            return visibleWidth >= 80 && visibleHeight >= 40;
        });
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

    private void DispatchTrayAction(Action action)
    {
        if (_shutdownRequested || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Normal);
    }

    private void RequestShutdown()
    {
        if (_shutdownTask is not null || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _shutdownRequested = true;
        _tray?.SetEnabled(false);
        _shutdownTask = CompleteShutdownAsync();
    }


    private void HideImmediately()
    {
        _isHidden = true;
        Hide();
    }

    private void ShowWithoutActivation()
    {
        if (_shutdownRequested || _shutdownCompleted)
        {
            return;
        }

        _isHidden = false;
        if (!IsVisible)
        {
            Show();
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        PlatformNativeWindow.ShowWithoutActivation(hwnd, (int)Left, (int)Top, (int)Width, (int)Height);
    }

    private IntPtr HitTestResizeBorder(IntPtr lParam, ref bool handled)
    {
        const int htClient = 1;
        const int htLeft = 10;
        const int htRight = 11;
        const int htTop = 12;
        const int htTopLeft = 13;
        const int htTopRight = 14;
        const int htBottom = 15;
        const int htBottomLeft = 16;
        const int htBottomRight = 17;

        var point = GetScreenPoint(lParam);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var border = Math.Max(8, (int)Math.Round(8 * dpi));
        var left = point.X < Left + border;
        var right = point.X >= Left + ActualWidth - border;
        var top = point.Y < Top + border;
        var bottom = point.Y >= Top + ActualHeight - border;

        if (top && left) { handled = true; return new IntPtr(htTopLeft); }
        if (top && right) { handled = true; return new IntPtr(htTopRight); }
        if (bottom && left) { handled = true; return new IntPtr(htBottomLeft); }
        if (bottom && right) { handled = true; return new IntPtr(htBottomRight); }
        if (left) { handled = true; return new IntPtr(htLeft); }
        if (right) { handled = true; return new IntPtr(htRight); }
        if (top) { handled = true; return new IntPtr(htTop); }
        if (bottom) { handled = true; return new IntPtr(htBottom); }

        handled = true;
        return new IntPtr(htClient);
    }

    private System.Windows.Point GetScreenPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new System.Windows.Point(x, y);
    }

    private void DragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_clickThrough)
        {
            return;
        }

        Left += e.HorizontalChange;
        Top += e.VerticalChange;
    }

    private void TopResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeFromTop(e.VerticalChange);

    private void LeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeFromLeft(e.HorizontalChange);

    private void RightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => Width = Math.Max(MinWidth, Width + e.HorizontalChange);

    private void BottomResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => Height = Math.Max(MinHeight, Height + e.VerticalChange);

    private void TopLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromTop(e.VerticalChange);
        ResizeFromLeft(e.HorizontalChange);
    }

    private void TopRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromTop(e.VerticalChange);
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
    }

    private void BottomLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromLeft(e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void BottomRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void ResizeFromTop(double delta)
    {
        var newHeight = Math.Max(MinHeight, Height - delta);
        Top += Height - newHeight;
        Height = newHeight;
    }

    private void ResizeFromLeft(double delta)
    {
        var newWidth = Math.Max(MinWidth, Width - delta);
        Left += Width - newWidth;
        Width = newWidth;
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
        if (dialog.ShowDialog(this) == true)
        {
            var loadCts = new CancellationTokenSource();
            var previousLoadCts = Interlocked.Exchange(ref _bookLoadCts, loadCts);
            previousLoadCts?.Cancel();
            try
            {
                await SaveProgressNowAsync();
                StatusText.Text = $"正在读取 {System.IO.Path.GetFileName(dialog.FileName)}…";
                var book = await BookLoader.LoadAsync(dialog.FileName, loadCts.Token);
                loadCts.Token.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
            {
                // A newer open request superseded this parse.
            }
            catch (BookReaderException exception)
            {
                StatusText.Text = exception.Message;
            }
            catch (Exception exception)
            {
                App.LogDiagnostic("Books", $"open failed: {exception}");
                StatusText.Text = "打开书籍失败，请查看诊断日志。";
            }
            finally
            {
                if (ReferenceEquals(_bookLoadCts, loadCts))
                {
                    _bookLoadCts = null;
                }

                loadCts.Dispose();
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        RequestShutdown();
    }

    private async Task CompleteShutdownAsync()
    {
        try
        {
            _bookLoadCts?.Cancel();
            await SaveProgressNowAsync();
            await SaveWindowSettingsAsync();
            App.LogDiagnostic("Shutdown", "state saved");
        }
        catch (Exception exception)
        {
            App.LogDiagnostic("Shutdown", $"save failed: {exception}");
        }
        finally
        {
            _shutdownCompleted = true;
            _tray?.Dispose();
            _tray = null;
            _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Normal);
        }
    }

    private async Task SaveWindowSettingsAsync()
    {
        await _stateStore.SaveSettingsAsync(new ReaderSettings(
            Opacity: Opacity,
            WindowLeft: double.IsFinite(Left) ? Left : null,
            WindowTop: double.IsFinite(Top) ? Top : null,
            WindowWidth: double.IsFinite(Width) ? Width : null,
            WindowHeight: double.IsFinite(Height) ? Height : null));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_bossHotKeyRegistered)
        {
            PlatformNativeWindow.UnregisterBossHotKey(hwnd, BossHotKeyId);
        }
        if (_autoScrollHotKeyRegistered)
        {
            PlatformNativeWindow.UnregisterBossHotKey(hwnd, AutoScrollHotKeyId);
        }
        if (_ctrlTabBossHotKeyRegistered)
        {
            PlatformNativeWindow.UnregisterBossHotKey(hwnd, CtrlTabBossHotKeyId);
        }

        _hwndSource?.RemoveHook(WindowMessageHook);
        _statusHideTimer?.Stop();
        ReaderView.Dispose();
        _progressSaveCts?.Dispose();
        _bookLoadCts?.Dispose();
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
        if (_shutdownRequested
            || string.IsNullOrWhiteSpace(_currentBookPath)
            || string.IsNullOrWhiteSpace(paragraphId))
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
        _progressSaveTask = SaveProgressAfterDelayAsync(_progressSaveCts.Token);
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
        catch (Exception exception)
        {
            App.LogDiagnostic("Progress", $"delayed save failed: {exception}");
        }
    }

    private async Task SaveProgressNowAsync()
    {
        _progressSaveCts?.Cancel();
        if (_progressSaveTask is not null)
        {
            try
            {
                await _progressSaveTask;
            }
            catch (Exception exception)
            {
                App.LogDiagnostic("Progress", $"pending save failed: {exception}");
            }
            finally
            {
                _progressSaveTask = null;
            }
        }

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
