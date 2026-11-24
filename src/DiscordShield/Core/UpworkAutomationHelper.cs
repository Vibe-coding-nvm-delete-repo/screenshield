using System.Windows.Automation;

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
/// Uses standard Microsoft UI Automation (accessibility tree) to click Upwork Time Tracker's
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
        var candidates = topLevelWindows
            .Where(w => w.IsVisible && upworkSet.Contains(w.ProcessName))
            .ToList();

        if (candidates.Count == 0)
        {
            return new UpworkPauseResult(false, false, "No visible Upwork Time Tracker window found.");
        }

        foreach (WindowSnapshot win in candidates)
        {
            try
            {
                AutomationElement? root = AutomationElement.FromHandle(win.Handle);
                if (root is null)
                {
                    continue;
                }

                var condition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));

                AutomationElementCollection controls = root.FindAll(TreeScope.Descendants, condition);
                foreach (AutomationElement control in controls)
                {
                    string name = control.Current.Name ?? string.Empty;
                    if (!MatchesPauseControl(name))
                    {
                        continue;
                    }

                    if (control.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggleObj)
                        && toggleObj is TogglePattern toggle
                        && toggle.Current.ToggleState == ToggleState.On)
                    {
                        toggle.Toggle();
                        return new UpworkPauseResult(true, true, $"Toggled Upwork control '{name}' off via UI Automation.");
                    }

                    if (control.TryGetCurrentPattern(InvokePattern.Pattern, out object? invokeObj)
                        && invokeObj is InvokePattern invoke)
                    {
                        invoke.Invoke();
                        return new UpworkPauseResult(true, true, $"Invoked Upwork control '{name}' via UI Automation.");
                    }
                }
            }
            catch (Exception ex)
            {
                return new UpworkPauseResult(true, false, $"UI Automation could not inspect Upwork window: {ex.Message}");
            }
        }

        return new UpworkPauseResult(
            true,
            false,
            "Upwork window found, but no active Stop/Pause button matched (tracker may already be paused or UI labels differ).");
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
