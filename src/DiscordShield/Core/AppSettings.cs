using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordShield.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NativeGuardAction
{
    AlertOnly,
    AutoMinimize,
    MoveToSecondaryMonitor,
    PauseUpworkAndAlert
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShieldItemKind
{
    App,
    Site
}

public sealed record ShieldItem(
    string Key,
    string DisplayName,
    ShieldItemKind Kind,
    string Detail = "");

public sealed class AppSettings
{
    public const string DefaultDiscordUrl = "https://discord.com/app";

    public bool ExcludeWrapperFromCapture { get; set; } = true;
    public string DiscordUrl { get; set; } = DefaultDiscordUrl;
    public bool NativeGuardEnabled { get; set; } = true;
    public NativeGuardAction NativeGuardAction { get; set; } = NativeGuardAction.AlertOnly;
    public bool OnlyActWhenUpworkRunning { get; set; } = true;
    public bool AlwaysHideTaskbarWhenBlockedOpen { get; set; } = true;

    /// <summary>
    /// Blocked desktop app process names (without .exe). Any app NOT in this list is automatically Allowed.
    /// </summary>
    public List<string> ProtectedProcessNames { get; set; } = ["Discord", "DiscordPTB", "DiscordCanary"];

    /// <summary>
    /// Blocked website domains / keywords (e.g. "discord.com", "whatsapp.com"). Any site NOT in this list is automatically Allowed.
    /// </summary>
    public List<string> BlockedDomains { get; set; } = ["discord.com"];

    public List<string> UpworkProcessNames { get; set; } = ["Upwork"];
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool EnableGlobalHotkey { get; set; } = true;
    public int GuardCooldownSeconds { get; set; } = 15;

    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenShield",
            "settings.json");

    public static string DefaultWebViewProfilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenShield",
            "WebView2Profile");

    public AppSettings Normalize()
    {
        DiscordUrl = IsValidHttpsUrl(DiscordUrl) ? DiscordUrl.Trim() : DefaultDiscordUrl;
        ProtectedProcessNames = NormalizeProcessList(ProtectedProcessNames, allowEmpty: true);
        BlockedDomains = NormalizeDomainList(BlockedDomains);
        UpworkProcessNames = NormalizeProcessList(UpworkProcessNames, allowEmpty: false, fallback: ["Upwork"]);

        if (GuardCooldownSeconds < 1 || GuardCooldownSeconds > 300)
        {
            GuardCooldownSeconds = 15;
        }

        return this;
    }

    public static bool IsValidHttpsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeDomainKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string trimmed = raw.Trim().ToLowerInvariant();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? abs)
            && !string.IsNullOrWhiteSpace(abs.Host))
        {
            trimmed = abs.Host.ToLowerInvariant();
        }
        else if (Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out Uri? rel)
            && !string.IsNullOrWhiteSpace(rel.Host)
            && trimmed.Contains('.'))
        {
            trimmed = rel.Host.ToLowerInvariant();
        }

        if (trimmed.StartsWith("www.", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        return trimmed;
    }

    private static List<string> NormalizeProcessList(List<string>? input, bool allowEmpty, string[]? fallback = null)
    {
        if (input is null)
        {
            return fallback is not null ? [.. fallback] : [];
        }

        var cleaned = input
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? s.Trim()[..^4]
                : s.Trim())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cleaned.Count == 0 && !allowEmpty && fallback is not null)
        {
            return [.. fallback];
        }

        return cleaned;
    }

    private static List<string> NormalizeDomainList(List<string>? input)
    {
        if (input is null)
        {
            return [];
        }

        return input
            .Select(NormalizeDomainKey)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static (AppSettings Settings, string? Warning) Load(string? path = null)
    {
        string targetPath = path ?? DefaultSettingsPath;
        if (!File.Exists(targetPath))
        {
            return (new AppSettings().Normalize(), null);
        }

        try
        {
            string json = File.ReadAllText(targetPath);
            AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (parsed is null)
            {
                return (new AppSettings().Normalize(), "Settings file was empty; restored defaults.");
            }

            return (parsed.Normalize(), null);
        }
        catch (Exception ex)
        {
            return (new AppSettings().Normalize(), $"Settings file could not be read ({ex.Message}); restored defaults.");
        }
    }

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public void Save(string? path = null)
    {
        Normalize();
        string targetPath = path ?? DefaultSettingsPath;
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(targetPath, json);
    }

    public async Task SaveAsync(string? path = null)
    {
        Normalize();
        string targetPath = path ?? DefaultSettingsPath;
        string json = JsonSerializer.Serialize(this, JsonOptions);

        await SaveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(targetPath, json).ConfigureAwait(false);
        }
        finally
        {
            SaveLock.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
