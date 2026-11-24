using Microsoft.Win32;

namespace DiscordShield.Core;

public interface IStartupRegistrationService
{
    bool IsEnabled();
    bool SetEnabled(bool enable, string? executablePath = null);
}

/// <summary>
/// Manages per-user Windows logon auto-start via HKCU\...\Run (zero admin privileges required).
/// Launches with --minimized so ScreenShield starts quietly in the system tray at logon.
/// </summary>
public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    public StartupRegistrationService(string valueName = "ScreenShield")
    {
        _valueName = valueName;
    }

    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(_valueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public bool SetEnabled(bool enable, string? executablePath = null)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enable)
            {
                string? exe = executablePath ?? Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return false;
                key.SetValue(_valueName, $"\"{exe}\" --minimized", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(_valueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
