using DiscordShield.Native;

namespace DiscordShield.Core;

public enum ShieldFilterTab
{
    All,
    Allowed,
    Blocked,
    History,
    Settings
}

public sealed record CatalogEntry(
    string Key,
    string DisplayName,
    ShieldItemKind Kind,
    string Detail,
    bool IsActiveNow,
    bool IsBlocked);

/// <summary>
/// Discovers all active desktop apps and active browser tabs/sites on Windows,
/// and computes instant O(1) All / Allowed / Blocked views.
/// Any app or site NOT in Blocked is automatically Allowed.
/// </summary>
public static class ActiveCatalogService
{
    public static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "brave",
        "firefox",
        "opera",
        "vivaldi",
        "arc"
    };

    private static readonly HashSet<string> IgnoredShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "TextInputHost",
        "ShellExperienceHost",
        "SearchHost",
        "StartMenuExperienceHost",
        "LockApp",
        "ScreenShield",
        "DiscordShield",
        "ApplicationFrameHost",
        "SystemSettings",
        "NVIDIA Overlay"
    };

    private static readonly string[] BrowserTitleSuffixes =
    [
        " - Google Chrome",
        " — Google Chrome",
        " - Microsoft​ Edge",
        " - Microsoft Edge",
        " — Microsoft Edge",
        " - Brave",
        " — Mozilla Firefox",
        " - Mozilla Firefox",
        " - Opera",
        " - Vivaldi"
    ];

    public static IReadOnlyList<CatalogEntry> BuildCatalog(
        IReadOnlyList<WindowSnapshot> visibleWindows,
        AppSettings settings,
        int selfProcessId,
        bool includeBrowserUiaTabs = false)
    {
        var blockedApps = new HashSet<string>(settings.ProtectedProcessNames, StringComparer.OrdinalIgnoreCase);
        var blockedSites = new HashSet<string>(settings.BlockedDomains, StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);

        // 1. Discover active desktop apps & active browser sites from visible windows
        foreach (WindowSnapshot win in visibleWindows)
        {
            if (win.ProcessId == selfProcessId
                || !win.IsVisible
                || win.Bounds.Width <= 50
                || win.Bounds.Height <= 50
                || IgnoredShellProcesses.Contains(win.ProcessName))
            {
                continue;
            }

            if (BrowserProcesses.Contains(win.ProcessName))
            {
                // Extract active site from browser window title
                AddSiteFromTabTitle(win.Title, win.ProcessName, blockedSites, entries);

                // Optionally inspect all open tabs via UI Automation (off UI thread)
                if (includeBrowserUiaTabs)
                {
                    foreach (string tabLabel in ReadBrowserTabsSafe(win.Handle))
                    {
                        AddSiteFromTabTitle(tabLabel, win.ProcessName, blockedSites, entries);
                    }
                }
            }

            string appKey = $"app:{win.ProcessName.ToLowerInvariant()}";
            if (!entries.ContainsKey(appKey))
            {
                string detail = string.IsNullOrWhiteSpace(win.Title) ? $"{win.ProcessName}.exe" : win.Title;
                entries[appKey] = new CatalogEntry(
                    Key: win.ProcessName,
                    DisplayName: win.ProcessName,
                    Kind: ShieldItemKind.App,
                    Detail: detail,
                    IsActiveNow: true,
                    IsBlocked: blockedApps.Contains(win.ProcessName));
            }
        }

        // 2. Include blocked apps that aren't currently open so user can always manage/unblock them
        foreach (string blockedApp in settings.ProtectedProcessNames)
        {
            string appKey = $"app:{blockedApp.ToLowerInvariant()}";
            if (!entries.ContainsKey(appKey))
            {
                entries[appKey] = new CatalogEntry(
                    Key: blockedApp,
                    DisplayName: blockedApp,
                    Kind: ShieldItemKind.App,
                    Detail: "Not currently open",
                    IsActiveNow: false,
                    IsBlocked: true);
            }
        }

        // 3. Include blocked sites/domains that aren't currently open
        foreach (string domain in settings.BlockedDomains)
        {
            string siteKey = $"site:{domain.ToLowerInvariant()}";
            if (!entries.ContainsKey(siteKey))
            {
                entries[siteKey] = new CatalogEntry(
                    Key: domain,
                    DisplayName: domain,
                    Kind: ShieldItemKind.Site,
                    Detail: "Blocked domain",
                    IsActiveNow: false,
                    IsBlocked: true);
            }
        }

        return entries.Values
            .OrderByDescending(static e => e.IsActiveNow)
            .ThenBy(static e => e.Kind)
            .ThenBy(static e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<CatalogEntry> Filter(
        IReadOnlyList<CatalogEntry> allEntries,
        ShieldFilterTab tab,
        string? searchQuery)
    {
        bool hasQuery = !string.IsNullOrWhiteSpace(searchQuery);
        string q = hasQuery ? searchQuery!.Trim() : string.Empty;

        if (tab == ShieldFilterTab.All && !hasQuery)
        {
            return allEntries; // Zero-allocation fast path!
        }

        var result = new List<CatalogEntry>(allEntries.Count);
        for (int i = 0; i < allEntries.Count; i++)
        {
            CatalogEntry e = allEntries[i];
            if (tab == ShieldFilterTab.Allowed && e.IsBlocked) continue;
            if (tab == ShieldFilterTab.Blocked && !e.IsBlocked) continue;

            if (hasQuery
                && !e.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.Detail.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(e);
        }

        return result;
    }

    public static bool SetBlocked(AppSettings settings, ShieldItemKind kind, string key, bool block)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (kind == ShieldItemKind.App)
        {
            string normalized = key.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? key.Trim()[..^4]
                : key.Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            if (block)
            {
                if (settings.ProtectedProcessNames.Any(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                settings.ProtectedProcessNames.Add(normalized);
                return true;
            }

            return settings.ProtectedProcessNames.RemoveAll(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        else
        {
            string normalized = AppSettings.NormalizeDomainKey(key);
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            if (block)
            {
                if (settings.BlockedDomains.Any(d => d.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                settings.BlockedDomains.Add(normalized);
                return true;
            }

            return settings.BlockedDomains.RemoveAll(d => d.Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    public static int BulkSetBlocked(AppSettings settings, IEnumerable<(ShieldItemKind Kind, string Key)> items, bool block)
    {
        int changed = 0;
        foreach (var (kind, key) in items)
        {
            if (SetBlocked(settings, kind, key, block))
            {
                changed++;
            }
        }

        return changed;
    }

    public static string ExtractSiteKeyFromTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        string cleaned = rawTitle.Trim();
        foreach (string suffix in BrowserTitleSuffixes)
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^suffix.Length].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(cleaned)
            || cleaned.Equals("New Tab", StringComparison.OrdinalIgnoreCase)
            || cleaned.Equals("Settings", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Check for explicit domain in title (e.g. "discord.com" or "github.com")
        foreach (string token in cleaned.Split([' ', '|', '-', '—', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Contains('.') && token.Length >= 4 && !token.StartsWith('.') && !token.EndsWith('.'))
            {
                string dom = AppSettings.NormalizeDomainKey(token);
                if (!string.IsNullOrWhiteSpace(dom) && dom.Contains('.'))
                {
                    return dom;
                }
            }
        }

        // Map well-known web app titles cleanly to canonical domains
        string lower = cleaned.ToLowerInvariant();
        if (lower.Contains("discord")) return "discord.com";
        if (lower.Contains("whatsapp")) return "whatsapp.com";
        if (lower.Contains("gmail") || lower.Contains("google mail")) return "mail.google.com";
        if (lower.Contains("slack")) return "slack.com";
        if (lower.Contains("instagram")) return "instagram.com";
        if (lower.Contains("facebook") || lower.Contains("messenger")) return "facebook.com";
        if (lower.Contains("youtube")) return "youtube.com";
        if (lower.Contains("chatgpt")) return "chatgpt.com";
        if (lower.Contains("reddit")) return "reddit.com";
        if (lower.Contains("spotify")) return "spotify.com";

        // Fall back to concise site label (last segment after " - " or " | ")
        string[] parts = cleaned.Split([" - ", " | ", " — "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string label = parts.Length > 0 ? parts[^1] : cleaned;
        return label.Length > 42 ? label[..42] : label;
    }

    public static bool MatchesBlockedSite(string windowTitle, IReadOnlyCollection<string> blockedDomains)
    {
        if (string.IsNullOrWhiteSpace(windowTitle) || blockedDomains.Count == 0)
        {
            return false;
        }

        string extracted = ExtractSiteKeyFromTitle(windowTitle);
        foreach (string domain in blockedDomains)
        {
            if (string.IsNullOrWhiteSpace(domain)) continue;

            if (extracted.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || extracted.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
                || windowTitle.Contains(domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Also match root brand name (e.g. "discord" from "discord.com")
            int dot = domain.IndexOf('.');
            if (dot >= 4)
            {
                string stem = domain[..dot];
                if (windowTitle.Contains(stem, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddSiteFromTabTitle(
        string tabTitle,
        string browserName,
        HashSet<string> blockedSites,
        Dictionary<string, CatalogEntry> entries)
    {
        string siteKey = ExtractSiteKeyFromTitle(tabTitle);
        if (string.IsNullOrWhiteSpace(siteKey))
        {
            return;
        }

        string dictKey = $"site:{siteKey.ToLowerInvariant()}";
        if (entries.ContainsKey(dictKey))
        {
            return;
        }

        bool isBlocked = blockedSites.Contains(siteKey) || MatchesBlockedSite(tabTitle, blockedSites);
        entries[dictKey] = new CatalogEntry(
            Key: siteKey,
            DisplayName: siteKey,
            Kind: ShieldItemKind.Site,
            Detail: $"{browserName} · {tabTitle}",
            IsActiveNow: true,
            IsBlocked: isBlocked);
    }

    private static List<string> ReadBrowserTabsSafe(IntPtr hwnd)
    {
        return ComUiAutomation.ReadTabItemNames(hwnd, maxTabs: 40);
    }
}
