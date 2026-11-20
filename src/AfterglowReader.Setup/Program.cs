namespace AfterglowReader.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = SetupOptions.Parse(args);

        if (options.Uninstall)
        {
            return RunUninstall(options);
        }

        if (options.Silent)
        {
            try
            {
                Installer.Install(options, null);
                if (options.LaunchAfterInstall)
                {
                    Installer.LaunchReader(options);
                }

                return 0;
            }
            catch (Exception exception)
            {
                AppLog.Write("silent install failed", exception);
                return 1;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        using var form = new SetupForm(options);
        Application.Run(form);
        return form.ExitCode;
    }

    private static int RunUninstall(SetupOptions options)
    {
        if (!options.Silent)
        {
            var running = Installer.IsReaderRunning();
            var message = running
                ? "卸载前会先关闭正在运行的阅读器。\n\n确定要卸载余光阅读器吗？"
                : "确定要卸载余光阅读器吗？\n\n阅读进度与设置会保留，下次安装可继续阅读。";

            var answer = MessageBox.Show(
                message,
                "卸载 余光阅读器",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK)
            {
                return 1;
            }
        }

        try
        {
            Installer.Uninstall(options, null);
            if (!options.Silent)
            {
                MessageBox.Show(
                    "已卸载余光阅读器。",
                    "卸载 余光阅读器",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return 0;
        }
        catch (Exception exception)
        {
            AppLog.Write("uninstall failed", exception);
            if (!options.Silent)
            {
                MessageBox.Show(
                    $"卸载失败：{exception.Message}",
                    "卸载 余光阅读器",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }
    }
}
