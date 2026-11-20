using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace AfterglowReader.Setup;

internal static class Installer
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AfterglowReader";

    public static void Install(SetupOptions options, IProgress<string>? progress)
    {
        var target = options.InstallDirectory;
        progress?.Report("检查运行中的阅读器…");
        CloseRunningReader();

        progress?.Report("准备安装目录…");
        Directory.CreateDirectory(target);

        progress?.Report("解压程序文件…");
        ExtractPayload(target);

        progress?.Report("写入卸载程序…");
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位安装程序自身。");
        var uninstaller = options.UninstallerPath;
        if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(current, uninstaller, overwrite: true);
        }

        progress?.Report("创建快捷方式…");
        CreateShortcut(SetupOptions.StartMenuShortcutPath, options);
        if (options.CreateDesktopShortcut)
        {
            CreateShortcut(SetupOptions.DesktopShortcutPath, options);
        }

        progress?.Report("登记到“应用和功能”…");
        WriteUninstallEntry(options);
    }

    public static void Uninstall(SetupOptions options, IProgress<string>? progress)
    {
        progress?.Report("关闭运行中的阅读器…");
        CloseRunningReader();

        progress?.Report("删除快捷方式…");
        DeleteFile(SetupOptions.StartMenuShortcutPath);
        DeleteFile(SetupOptions.DesktopShortcutPath);
        DeleteFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "AfterglowReader.lnk"));

        progress?.Report("清理注册表…");
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        progress?.Report("删除程序文件…");
        RemoveProgramFiles(options.InstallDirectory);
    }

    private static readonly string[] PreservedStateNames =
    {
        "settings.json",
        "progress.json",
        "startup.log",
        "AfterglowReader.exe.WebView2",
        "Uninstall.exe"
    };

    /// <summary>
    /// Removes every installed file but keeps the reader state (settings, progress, log, WebView2 profile)
    /// so a later reinstall resumes where the user stopped.
    /// </summary>
    private static void RemoveProgramFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(entry);
            if (PreservedStateNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Locked leftovers are removed by the delayed cleanup below.
            }
        }

        ScheduleSelfCleanup(directory);
    }

    private static void ExtractPayload(string target)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(SetupOptions.PayloadResource)
            ?? throw new InvalidOperationException("安装包缺少程序负载，请重新下载完整的安装程序。");

        var temp = Path.Combine(Path.GetTempPath(), $"afterglow-payload-{Guid.NewGuid():N}.zip");
        try
        {
            using (var file = File.Create(temp))
            {
                resource.CopyTo(file);
            }

            ZipFile.ExtractToDirectory(temp, target, overwriteFiles: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, SetupOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            throw new InvalidOperationException("当前系统缺少 WScript.Shell，无法创建快捷方式。");
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = options.ApplicationPath;
            shortcut.WorkingDirectory = options.InstallDirectory;
            shortcut.Description = SetupOptions.ProductName;
            shortcut.IconLocation = File.Exists(options.IconPath) ? options.IconPath : options.ApplicationPath;
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void WriteUninstallEntry(SetupOptions options)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        var quotedUninstaller = $"\"{options.UninstallerPath}\"";
        key.SetValue("DisplayName", $"{SetupOptions.ProductName} Afterglow Reader");
        key.SetValue("DisplayVersion", SetupOptions.Version);
        key.SetValue("Publisher", "afterglow-reader");
        key.SetValue("DisplayIcon", File.Exists(options.IconPath) ? options.IconPath : options.ApplicationPath);
        key.SetValue("InstallLocation", options.InstallDirectory);
        key.SetValue("UninstallString", $"{quotedUninstaller} --uninstall");
        key.SetValue("QuietUninstallString", $"{quotedUninstaller} --uninstall --silent");
        key.SetValue("InstallDate", DateTimeOffset.Now.ToString("yyyyMMdd"));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeInKilobytes(options.InstallDirectory), RegistryValueKind.DWord);
    }

    private static int EstimateSizeInKilobytes(string directory)
    {
        try
        {
            var bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length;
                    }
                    catch (IOException)
                    {
                        return 0L;
                    }
                });
            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void CloseRunningReader()
    {
        foreach (var process in Process.GetProcessesByName("AfterglowReader"))
        {
            using (process)
            {
                try
                {
                    if (process.CloseMainWindow())
                    {
                        process.WaitForExit(6000);
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }
    }

    public static bool IsReaderRunning()
        => Process.GetProcessesByName("AfterglowReader").Length > 0;

    public static void LaunchReader(SetupOptions options)
    {
        if (!File.Exists(options.ApplicationPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(options.ApplicationPath)
        {
            WorkingDirectory = options.InstallDirectory,
            UseShellExecute = true
        });
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked shortcut must not block uninstall.
        }
    }

    private static void ScheduleSelfCleanup(string directory)
    {
        try
        {
            var info = new ProcessStartInfo(
                "cmd.exe",
                $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{Path.Combine(directory, "Uninstall.exe")}\" >nul 2>nul & rd \"{directory}\" >nul 2>nul")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            };
            Process.Start(info);
        }
        catch (Exception)
        {
            // Best effort; the user can delete the folder manually.
        }
    }
}
