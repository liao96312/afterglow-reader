using System.Reflection;

namespace AfterglowReader.Setup;

/// <summary>
/// Command line shape of the setup executable.
/// Running the same executable from the install directory (Uninstall.exe) triggers removal.
/// </summary>
internal sealed class SetupOptions
{
    public const string ProductName = "余光阅读器";
    public const string ProductId = "AfterglowReader";
    public const string PayloadResource = "AfterglowReader.Setup.payload.zip";

    public string InstallDirectory { get; set; } = DefaultInstallDirectory();

    public bool Silent { get; private set; }

    public bool Uninstall { get; private set; }

    public bool CreateDesktopShortcut { get; set; } = true;

    public bool LaunchAfterInstall { get; set; } = true;

    public static string DefaultInstallDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductId);

    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions();
        var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);

        if (executableName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
        {
            options.Uninstall = true;
        }

        foreach (var argument in args)
        {
            var trimmed = argument.Trim();
            if (trimmed.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                options.Uninstall = true;
            }
            else if (trimmed.Equals("--silent", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("/S", StringComparison.OrdinalIgnoreCase))
            {
                options.Silent = true;
            }
            else if (trimmed.Equals("--no-launch", StringComparison.OrdinalIgnoreCase))
            {
                options.LaunchAfterInstall = false;
            }
            else if (trimmed.Equals("--no-desktop", StringComparison.OrdinalIgnoreCase))
            {
                options.CreateDesktopShortcut = false;
            }
            else if (trimmed.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["--dir=".Length..].Trim().Trim('"');
                if (value.Length > 0)
                {
                    options.InstallDirectory = Path.GetFullPath(value);
                }
            }
        }

        if (options.Uninstall && !args.Any(argument => argument.Trim().StartsWith("--dir=", StringComparison.OrdinalIgnoreCase)))
        {
            options.InstallDirectory = ResolveInstalledDirectory();
        }

        return options;
    }

    /// <summary>
    /// The uninstaller must never guess: prefer the directory recorded at install time,
    /// then the directory the uninstaller itself lives in, then the per-user default.
    /// </summary>
    private static string ResolveInstalledDirectory()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AfterglowReader");
            if (key?.GetValue("InstallLocation") is string recorded && Directory.Exists(recorded))
            {
                return Path.GetFullPath(recorded);
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Fall through to the executable directory.
        }

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(baseDirectory, "AfterglowReader.exe"))
            || string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "Uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return baseDirectory;
        }

        return DefaultInstallDirectory();
    }

    public static string Version
        => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "1.0.0";

    public string ApplicationPath => Path.Combine(InstallDirectory, "AfterglowReader.exe");

    public string UninstallerPath => Path.Combine(InstallDirectory, "Uninstall.exe");

    public string IconPath => Path.Combine(InstallDirectory, "Assets", "afterglow.ico");

    public static string StartMenuShortcutPath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "余光阅读器.lnk");

    public static string DesktopShortcutPath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "余光阅读器.lnk");
}
