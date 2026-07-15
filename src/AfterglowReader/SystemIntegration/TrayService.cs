using System.Drawing;
using Forms = System.Windows.Forms;

namespace AfterglowReader.SystemIntegration;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    internal TrayService(
        Action showReader,
        Action toggleClickThrough,
        Action openFile,
        Action showSettings,
        Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示/恢复阅读器", null, (_, _) => showReader());
        menu.Items.Add("打开书籍…", null, (_, _) => openFile());
        menu.Items.Add("鼠标穿透", null, (_, _) => toggleClickThrough());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => showSettings());
        menu.Items.Add("退出", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "余光阅读器",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => showReader();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
