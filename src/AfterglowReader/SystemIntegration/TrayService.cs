using System.Drawing;
using System.Diagnostics;
using Forms = System.Windows.Forms;

namespace AfterglowReader.SystemIntegration;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Action<Action> _dispatch;

    internal TrayService(
        Action<Action> dispatch,
        Action showReader,
        Action toggleClickThrough,
        Action openFile,
        Action showSettings,
        Action exit)
    {
        _dispatch = dispatch;
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("显示/恢复阅读器", null, (_, _) => Post(showReader));
        _menu.Items.Add("打开书籍…", null, (_, _) => Post(openFile));
        _menu.Items.Add("鼠标穿透", null, (_, _) => Post(toggleClickThrough));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("设置…", null, (_, _) => Post(showSettings));
        _menu.Items.Add("退出", null, (_, _) => Post(exit));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "余光阅读器",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Post(showReader);
    }

    internal void SetEnabled(bool enabled)
    {
        foreach (Forms.ToolStripItem item in _menu.Items)
        {
            if (item is not Forms.ToolStripSeparator)
            {
                item.Enabled = enabled;
            }
        }
    }

    private void Post(Action action)
    {
        try
        {
            _dispatch(action);
        }
        catch (Exception exception)
        {
            AfterglowReader.App.LogDiagnostic("Tray", $"dispatch failed: {exception.Message}");
        }
    }

    private static Icon LoadApplicationIcon()
    {
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        return executablePath is not null
            ? Icon.ExtractAssociatedIcon(executablePath) ?? SystemIcons.Application
            : SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _menu.Dispose();
        _icon.Dispose();
    }
}
