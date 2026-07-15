using System.Runtime.InteropServices;

namespace AfterglowReader.SystemIntegration;

internal static class NativeWindow
{
    internal const int WmHotKey = 0x0312;
    internal const int WmNcHitTest = 0x0084;
    internal const int HtTransparent = -1;
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x00000080;
    internal const long WsExAppWindow = 0x00040000;
    internal const long WsExTransparent = 0x00000020;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint VkF7 = 0x76;
    internal const uint VkF8 = 0x77;
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

    internal static bool RegisterAutoScrollHotKey(IntPtr hwnd, int id)
        => RegisterHotKey(hwnd, id, ModNoRepeat, VkF7);

    internal static void UnregisterBossHotKey(IntPtr hwnd, int id)
        => UnregisterHotKey(hwnd, id);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
