using System.Windows;
using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Input;
using System.Text.Json;
using System.IO;
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
    private enum ReaderInteractionState
    {
        Hidden,
        VisiblePassive,
        VisibleInteractive
    }

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
    private ReaderInteractionState _readerInteractionState = ReaderInteractionState.Hidden;
    private bool _pendingReaderInteractionFocus;
    private System.Windows.Threading.DispatcherTimer? _statusHideTimer;
    private readonly ReaderStateStore _stateStore = new();
    private ReaderSettings _settings = new();
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
    private long _lastProgressSequence;

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
        _settings = (await _stateStore.LoadSettingsAsync()).Normalize();
        RestoreWindowSettings(_settings);
        _readerInteractionState = ReaderInteractionState.VisiblePassive;
        LoadProgress(await _stateStore.LoadProgressAsync());
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
            await RestoreLastBookAsync();
            TryFocusReaderIfRequested();
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

            return HitTestWindow(hwnd, lParam, ref handled);
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
        _ = SaveProgressForLifecycleAsync("hide");
        _isHidden = true;
        _readerInteractionState = ReaderInteractionState.Hidden;
        _pendingReaderInteractionFocus = false;
        Hide();
    }

    private void ShowWithoutActivation()
    {
        if (_shutdownRequested || _shutdownCompleted)
        {
            return;
        }

        _isHidden = false;
        _readerInteractionState = ReaderInteractionState.VisiblePassive;
        if (!IsVisible)
        {
            Show();
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        PlatformNativeWindow.ShowWithoutActivation(hwnd, (int)Left, (int)Top, (int)Width, (int)Height);
        _pendingReaderInteractionFocus = PlatformNativeWindow.IsCursorInsideWindow(hwnd);
        ResetReaderInteractionGate();
        TryFocusReaderIfRequested();
    }

    private void ResetReaderInteractionGate()
    {
        if (ReaderView.CoreWebView2 is not null)
        {
            _ = ReaderView.CoreWebView2.ExecuteScriptAsync("window.resetReaderInteraction?.();");
        }
    }

    private void RequestReaderInteractionFocus()
    {
        _pendingReaderInteractionFocus = true;
        TryFocusReaderIfRequested();
    }

    private void TryFocusReaderIfRequested()
    {
        if (!_pendingReaderInteractionFocus
            || _isHidden
            || _clickThrough
            || _shutdownRequested
            || _readerInteractionState is ReaderInteractionState.Hidden or ReaderInteractionState.VisibleInteractive
            || !_readerReady.Task.IsCompleted
            || ReaderView.CoreWebView2 is null)
        {
            return;
        }

        _pendingReaderInteractionFocus = false;
        _readerInteractionState = ReaderInteractionState.VisibleInteractive;
        var hwnd = new WindowInteropHelper(this).Handle;
        PlatformNativeWindow.ActivateWindow(hwnd);
        Activate();
        ReaderView.Focus();
        Keyboard.Focus(ReaderView);
        App.LogDiagnostic("Focus", "Reader interaction focus acquired after pointer entered window");
    }

    private static IntPtr HitTestWindow(IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
        var point = GetScreenPoint(lParam);
        if (!PlatformNativeWindow.TryGetWindowRect(hwnd, out var bounds))
        {
            return IntPtr.Zero;
        }

        var dpi = PlatformNativeWindow.GetWindowDpi(hwnd);
        var result = WindowHitTest.Resolve(
            point.X,
            point.Y,
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom,
            WindowHitTest.ScaleDip(8, dpi),
            WindowHitTest.ScaleDip(110, dpi),
            WindowHitTest.ScaleDip(44, dpi));
        handled = true;
        return new IntPtr(result);
    }

    private static System.Windows.Point GetScreenPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new System.Windows.Point(x, y);
    }

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        var hwnd = new WindowInteropHelper(this).Handle;
        PlatformNativeWindow.SetClickThrough(hwnd, _clickThrough);

        if (_clickThrough)
        {
            _readerInteractionState = _isHidden
                ? ReaderInteractionState.Hidden
                : ReaderInteractionState.VisiblePassive;
            _pendingReaderInteractionFocus = false;
        }
        else if (!_isHidden)
        {
            _readerInteractionState = ReaderInteractionState.VisiblePassive;
            _pendingReaderInteractionFocus = PlatformNativeWindow.IsCursorInsideWindow(hwnd);
            ResetReaderInteractionGate();
            TryFocusReaderIfRequested();
        }

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
            await OpenBookAsync(dialog.FileName, restoringLastBook: false);
        }
    }

    private async Task RestoreLastBookAsync()
    {
        var path = NormalizeBookPath(_settings.LastBookPath);
        if (path is null)
        {
            return;
        }

        if (!File.Exists(path))
        {
            ShowStatus("上次阅读的书籍已移动或不存在。", hideAfterMilliseconds: 4_000);
            return;
        }

        await OpenBookAsync(path, restoringLastBook: true);
    }

    private async Task<bool> OpenBookAsync(string path, bool restoringLastBook)
    {
        var normalizedPath = NormalizeBookPath(path);
        if (normalizedPath is null)
        {
            ShowStatus("书籍路径无效。", hideAfterMilliseconds: 4_000);
            return false;
        }

        var loadCts = new CancellationTokenSource();
        var previousLoadCts = Interlocked.Exchange(ref _bookLoadCts, loadCts);
        previousLoadCts?.Cancel();
        try
        {
            if (_currentBookPath is not null)
            {
                await SaveProgressNowAsync();
            }

            ShowStatus(restoringLastBook
                ? $"正在恢复 {Path.GetFileName(normalizedPath)}…"
                : $"正在读取 {Path.GetFileName(normalizedPath)}…");
            var book = await BookLoader.LoadAsync(normalizedPath, loadCts.Token);
            loadCts.Token.ThrowIfCancellationRequested();
            _currentBookPath = normalizedPath;
            _session = new ReaderSession(book);
            var saved = _progress.FirstOrDefault(item => string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
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

            _settings = _settings with { LastBookPath = normalizedPath };
            await SaveWindowSettingsAsync();
            StatusPanel.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            return false;
        }
        catch (BookReaderException exception)
        {
            ShowStatus(restoringLastBook ? "无法恢复上次阅读的书籍。" : exception.Message, hideAfterMilliseconds: 4_000);
            App.LogDiagnostic("Books", $"open failed: {exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            App.LogDiagnostic("Books", $"open failed: {exception}");
            ShowStatus(restoringLastBook ? "无法恢复上次阅读的书籍。" : "打开书籍失败，请查看诊断日志。", hideAfterMilliseconds: 4_000);
            return false;
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

    private static string? NormalizeBookPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void LoadProgress(IEnumerable<BookProgress> progress)
    {
        foreach (var item in progress)
        {
            var path = NormalizeBookPath(item.Path);
            if (path is null || string.IsNullOrWhiteSpace(item.ParagraphId) || !double.IsFinite(item.ParagraphOffset))
            {
                continue;
            }

            var normalized = item with { Path = path };
            var existing = _progress.FindIndex(value => string.Equals(value.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing < 0)
            {
                _progress.Add(normalized);
            }
            else if (_progress[existing].UpdatedAt <= normalized.UpdatedAt)
            {
                _progress[existing] = normalized;
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
            App.LogDiagnostic("Shutdown", $"save failed; path={_currentBookPath ?? "<none>"}; {exception}");
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
        _settings = (_settings with
        {
            Opacity = Opacity,
            WindowLeft = double.IsFinite(Left) ? Left : null,
            WindowTop = double.IsFinite(Top) ? Top : null,
            WindowWidth = double.IsFinite(Width) ? Width : null,
            WindowHeight = double.IsFinite(Height) ? Height : null,
            LastBookPath = NormalizeBookPath(_settings.LastBookPath)
        }).Normalize();
        await _stateStore.SaveSettingsAsync(_settings);
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
                    RecordProgress(progress.ParagraphId, progress.Offset, progress.Sequence);
                    break;
                case ChapterSelectionMessage chapter:
                    _ = HandleChapterSelectionAsync(chapter.ChapterId);
                    break;
                case OpenFileMessage:
                    OpenFilePlaceholder();
                    break;
                case WindowDragMessage:
                    BeginWindowMoveOrResize(WindowHitTest.Caption);
                    break;
                case WindowResizeMessage resize when WindowHitTest.TryGetResizeRegion(resize.Edge, out var region):
                    BeginWindowMoveOrResize(region);
                    break;
                case ReaderPointerEnteredMessage:
                    RequestReaderInteractionFocus();
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Ignore messages that are not part of the small reader bridge.
        }
    }

    private void BeginWindowMoveOrResize(int hitTest)
    {
        if (_clickThrough || _shutdownRequested)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        PlatformNativeWindow.BeginWindowMoveOrResize(hwnd, hitTest);
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

    private void RecordProgress(string? paragraphId, double offset, long sequence = 0)
    {
        if (sequence > 0 && sequence <= _lastProgressSequence)
        {
            return;
        }

        if (sequence > 0)
        {
            _lastProgressSequence = sequence;
        }

        UpdateProgress(paragraphId, offset, scheduleDelayedSave: true, allowDuringShutdown: false);
    }

    private void UpdateProgress(string? paragraphId, double offset, bool scheduleDelayedSave, bool allowDuringShutdown)
    {
        if ((!allowDuringShutdown && _shutdownRequested)
            || string.IsNullOrWhiteSpace(_currentBookPath)
            || string.IsNullOrWhiteSpace(paragraphId)
            || !double.IsFinite(offset))
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

        if (scheduleDelayedSave)
        {
            _progressSaveCts?.Cancel();
            _progressSaveCts?.Dispose();
            _progressSaveCts = new CancellationTokenSource();
            _progressSaveTask = SaveProgressAfterDelayAsync(_progressSaveCts.Token);
        }
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
            App.LogDiagnostic("Progress", $"delayed save failed; path={_currentBookPath ?? "<none>"}; {exception}");
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

        await CaptureCurrentProgressAsync();

        if (_currentBookPath is not null && !string.IsNullOrWhiteSpace(_lastAnchorId))
        {
            UpdateProgress(_lastAnchorId, _lastAnchorOffset, scheduleDelayedSave: false, allowDuringShutdown: true);
        }

        await _stateStore.SaveProgressAsync(_progress);
    }

    private async Task SaveProgressForLifecycleAsync(string reason)
    {
        try
        {
            await SaveProgressNowAsync();
            App.LogDiagnostic("Progress", $"saved for {reason}");
        }
        catch (Exception exception)
        {
            App.LogDiagnostic("Progress", $"save for {reason} failed; path={_currentBookPath ?? "<none>"}; {exception}");
        }
    }

    private async Task CaptureCurrentProgressAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentBookPath)
            || ReaderView.CoreWebView2 is null
            || !_readerReady.Task.IsCompleted)
        {
            return;
        }

        try
        {
            var script = ReaderView.CoreWebView2.ExecuteScriptAsync("window.captureCurrentAnchor?.() ?? null;");
            if (await Task.WhenAny(script, Task.Delay(TimeSpan.FromMilliseconds(750))) != script)
            {
                _ = script.ContinueWith(completed => _ = completed.Exception,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                App.LogDiagnostic("Progress", "capture current anchor timed out");
                return;
            }

            var result = await script;
            if (TryParseCapturedAnchor(result, out var paragraphId, out var offset, out var sequence))
            {
                _lastProgressSequence = Math.Max(_lastProgressSequence, sequence);
                UpdateProgress(paragraphId, offset, scheduleDelayedSave: false, allowDuringShutdown: true);
            }
        }
        catch (Exception exception)
        {
            App.LogDiagnostic("Progress", $"capture current anchor failed: {exception.Message}");
        }
    }

    private static bool TryParseCapturedAnchor(string json, out string? paragraphId, out double offset, out long sequence)
    {
        paragraphId = null;
        offset = 0;
        sequence = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || string.IsNullOrWhiteSpace(id.GetString())
                || !root.TryGetProperty("offset", out var offsetElement)
                || !offsetElement.TryGetDouble(out offset)
                || !double.IsFinite(offset))
            {
                return false;
            }

            paragraphId = id.GetString();
            if (root.TryGetProperty("sequence", out var sequenceElement) && sequenceElement.TryGetInt64(out var parsedSequence))
            {
                sequence = Math.Max(0, parsedSequence);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
