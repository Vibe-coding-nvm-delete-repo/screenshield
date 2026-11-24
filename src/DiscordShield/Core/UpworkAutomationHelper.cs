using DiscordShield.Native;

namespace DiscordShield.Core;

public sealed record UpworkPauseResult(
    bool UpworkWindowFound,
    bool PauseInvoked,
    string Message);

public interface IUpworkAutomationHelper
{
    UpworkPauseResult TryPauseUpworkTracker(IReadOnlyList<WindowSnapshot> topLevelWindows, IReadOnlyList<string> upworkProcessNames);
}

/// <summary>
/// Uses standard Microsoft COM UI Automation (UIAutomationCore.dll) to click Upwork Time Tracker's
/// own Stop/Pause button when configured. Never patches, injects into, or spoofs Upwork.
/// </summary>
public sealed class UpworkAutomationHelper : IUpworkAutomationHelper
{
    private static readonly string[] PauseKeywords = ["stop", "pause", "turn off"];

    public UpworkPauseResult TryPauseUpworkTracker(
        IReadOnlyList<WindowSnapshot> topLevelWindows,
        IReadOnlyList<string> upworkProcessNames)
    {
        var upworkSet = new HashSet<string>(upworkProcessNames, StringComparer.OrdinalIgnoreCase);
        bool foundAny = false;
        string lastMsg = "No visible Upwork Time Tracker window found.";

        for (int i = 0; i < topLevelWindows.Count; i++)
        {
            WindowSnapshot win = topLevelWindows[i];
            if (!win.IsVisible || !upworkSet.Contains(win.ProcessName))
            {
                continue;
            }

            foundAny = true;
            var (invoked, msg) = ComUiAutomation.TryInvokeMatchingControl(win.Handle, MatchesPauseControl);
            lastMsg = msg;
            if (invoked)
            {
                return new UpworkPauseResult(true, true, msg);
            }
        }

        return new UpworkPauseResult(foundAny, false, lastMsg);
    }

    public static bool MatchesPauseControl(string controlName)
    {
        if (string.IsNullOrWhiteSpace(controlName))
        {
            return false;
        }

        string lower = controlName.Trim().ToLowerInvariant();
        if (lower.Contains("close") || lower.Contains("minimize") || lower.Contains("maximize"))
        {
            return false;
        }

        return PauseKeywords.Any(lower.Contains);
    }
}
