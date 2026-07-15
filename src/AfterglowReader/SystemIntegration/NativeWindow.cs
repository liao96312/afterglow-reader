using System.Runtime.InteropServices;

namespace AfterglowReader.SystemIntegration;

internal static class NativeWindow
{
    internal const int WmHotKey = 0x0312;
    internal const int WmShowReader = 0x8001;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmNcLeftButtonDown = 0x00A1;
    internal const int HtTransparent = -1;
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x00000080;
    internal const long WsExAppWindow = 0x00040000;
    internal const long WsExTransparent = 0x00000020;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint ModControl = 0x0002;
    internal const uint VkF7 = 0x76;
    internal const uint VkF8 = 0x77;
    internal const uint VkTab = 0x09;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SwpNoSendChanging = 0x0400;
    internal static readonly IntPtr HwndTopmost = new(-1);

    internal static void ApplyToolWindow(IntPtr hwnd)
    {
        var styles = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        styles = (styles | WsExToolWindow) & ~WsExAppWindow;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(styles));
    }

    internal static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        var styles = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        styles = enabled ? styles | WsExTransparent : styles & ~WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(styles));
    }

    internal static void ShowWithoutActivation(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, HwndTopmost, x, y, width, height,
            SwpNoActivate | SwpShowWindow | SwpNoSendChanging);
    }

    internal static bool RegisterBossHotKey(IntPtr hwnd, int id)
        => RegisterHotKey(hwnd, id, ModNoRepeat, VkF8);

    internal static bool RegisterCtrlTabBossHotKey(IntPtr hwnd, int id)
        => RegisterHotKey(hwnd, id, ModNoRepeat | ModControl, VkTab);

    internal static bool RegisterAutoScrollHotKey(IntPtr hwnd, int id)
        => RegisterHotKey(hwnd, id, ModNoRepeat, VkF7);

    internal static void UnregisterBossHotKey(IntPtr hwnd, int id)
        => UnregisterHotKey(hwnd, id);

    internal static bool NotifyExistingInstance()
    {
        var hwnd = FindWindow(null, "余光阅读器");
        return hwnd != IntPtr.Zero && PostMessage(hwnd, WmShowReader, IntPtr.Zero, IntPtr.Zero);
    }

    internal static bool TryGetWindowRect(IntPtr hwnd, out WindowBounds bounds)
        => GetWindowRect(hwnd, out bounds);

    internal static bool IsCursorInsideWindow(IntPtr hwnd)
    {
        if (!GetCursorPos(out var point) || !GetWindowRect(hwnd, out var bounds))
        {
            return false;
        }

        return point.X >= bounds.Left
            && point.X < bounds.Right
            && point.Y >= bounds.Top
            && point.Y < bounds.Bottom;
    }

    internal static bool ActivateWindow(IntPtr hwnd)
        => SetForegroundWindow(hwnd);

    internal static uint GetWindowDpi(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }

    internal static void BeginWindowMoveOrResize(IntPtr hwnd, int hitTest)
    {
        ReleaseCapture();
        PostMessage(hwnd, WmNcLeftButtonDown, new IntPtr(hitTest), IntPtr.Zero);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowBounds lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowBounds
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ScreenPoint
{
    internal int X;
    internal int Y;
}
