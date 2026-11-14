using Microsoft.Win32;

namespace MattEland.HopeLab.Updates;

/// <summary>
/// The Apps &amp; features entry a per-user Inno Setup installer (installers/windows/app.iss) registers. An in-place
/// update replaces the files without running setup again, so it brings the shown version along itself.
/// </summary>
public static class WindowsSetupEntry
{
    /// <summary>Inno Setup's uninstall key for an AppId like <c>{8B7C…}</c>.</summary>
    public static string UninstallKey(string appId) => $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{appId}_is1";

    /// <summary>Updates DisplayVersion if setup installed the copy in <paramref name="installDirectory"/>; otherwise does nothing.</summary>
    public static void SetVersion(string appId, string installDirectory, string version)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey(appId), writable: true);
            if (key?.GetValue("InstallLocation") is not string location) return;
            if (!string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(installDirectory).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue("DisplayVersion", version);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
}
