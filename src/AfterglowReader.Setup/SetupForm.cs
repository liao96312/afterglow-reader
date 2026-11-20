using System.Diagnostics;

namespace AfterglowReader.Setup;

internal sealed class SetupForm : Form
{
    private readonly SetupOptions _options;
    private readonly TextBox _directoryBox;
    private readonly CheckBox _desktopShortcut;
    private readonly CheckBox _launchAfterInstall;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly Button _installButton;
    private readonly Button _cancelButton;
    private readonly Button _browseButton;
    private bool _busy;

    public int ExitCode { get; private set; }

    public SetupForm(SetupOptions options)
    {
        _options = options;

        Text = $"{SetupOptions.ProductName} 安装程序";
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 320);

        var title = new Label
        {
            Text = $"安装 {SetupOptions.ProductName}  {SetupOptions.Version}",
            Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 22)
        };

        var subtitle = new Label
        {
            Text = "轻量本地阅读器：小窗、透明、可自由拖动缩放，支持 TXT / EPUB / MOBI。\n只安装到当前用户，不需要管理员权限。",
            ForeColor = Color.FromArgb(96, 88, 78),
            AutoSize = true,
            Location = new Point(26, 62)
        };

        var directoryLabel = new Label
        {
            Text = "安装位置",
            AutoSize = true,
            Location = new Point(26, 124)
        };

        _directoryBox = new TextBox
        {
            Text = options.InstallDirectory,
            Location = new Point(26, 148),
            Width = 480
        };

        _browseButton = new Button
        {
            Text = "浏览…",
            Location = new Point(514, 146),
            Size = new Size(80, 27)
        };
        _browseButton.Click += OnBrowse;

        _desktopShortcut = new CheckBox
        {
            Text = "创建桌面快捷方式",
            Checked = options.CreateDesktopShortcut,
            AutoSize = true,
            Location = new Point(26, 186)
        };

        _launchAfterInstall = new CheckBox
        {
            Text = "安装完成后启动阅读器",
            Checked = options.LaunchAfterInstall,
            AutoSize = true,
            Location = new Point(196, 186)
        };

        _progress = new ProgressBar
        {
            Location = new Point(26, 218),
            Size = new Size(568, 10),
            Style = ProgressBarStyle.Blocks,
            Value = 0
        };

        _status = new Label
        {
            Text = "准备就绪。",
            ForeColor = Color.FromArgb(96, 88, 78),
            AutoEllipsis = true,
            Location = new Point(26, 236),
            Size = new Size(568, 20)
        };

        _installButton = new Button
        {
            Text = "安装",
            Location = new Point(422, 268),
            Size = new Size(84, 30)
        };
        _installButton.Click += OnInstall;

        _cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(512, 268),
            Size = new Size(82, 30)
        };

        Controls.AddRange(new Control[]
        {
            title, subtitle, directoryLabel, _directoryBox, _browseButton,
            _desktopShortcut, _launchAfterInstall, _progress, _status,
            _installButton, _cancelButton
        });

        AcceptButton = _installButton;
        CancelButton = _cancelButton;
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择安装位置",
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_directoryBox.Text.Trim())
                ? _directoryBox.Text.Trim()
                : Path.GetDirectoryName(_directoryBox.Text.Trim()) ?? string.Empty
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _directoryBox.Text = Path.Combine(dialog.SelectedPath, SetupOptions.ProductId);
        }
    }

    private async void OnInstall(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var directory = _directoryBox.Text.Trim();
        if (directory.Length == 0)
        {
            MessageBox.Show(this, "请填写安装位置。", SetupOptions.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            directory = Path.GetFullPath(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, "安装位置不是有效的路径。", SetupOptions.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (Installer.IsReaderRunning()
            && MessageBox.Show(
                this,
                "阅读器正在运行，安装前需要先关闭它。是否继续？",
                SetupOptions.ProductName,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _options.InstallDirectory = directory;
        _options.CreateDesktopShortcut = _desktopShortcut.Checked;
        _options.LaunchAfterInstall = _launchAfterInstall.Checked;

        SetBusy(true);
        var progress = new Progress<string>(message => _status.Text = message);

        try
        {
            await Task.Run(() => Installer.Install(_options, progress));
            _progress.Value = 100;
            _status.Text = "安装完成。";
            SetBusy(false);

            if (_options.LaunchAfterInstall)
            {
                Installer.LaunchReader(_options);
            }

            ExitCode = 0;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            AppLog.Write("install failed", exception);
            _progress.Value = 0;
            _status.Text = "安装失败。";
            SetBusy(false);
            MessageBox.Show(
                this,
                $"安装失败：{exception.Message}",
                SetupOptions.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _installButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _directoryBox.Enabled = !busy;
        _desktopShortcut.Enabled = !busy;
        _launchAfterInstall.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}

internal static class AppLog
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AfterglowReader",
        "setup.log");

    public static void Write(string source, Exception exception)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"[{DateTimeOffset.Now:O}] {source}: {exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            Debug.WriteLine(exception);
        }
    }
}
