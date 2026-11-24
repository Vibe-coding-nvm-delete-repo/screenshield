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

public sealed class AppSettings
{
    public const string DefaultDiscordUrl = "https://discord.com/app";

    public bool ExcludeWrapperFromCapture { get; set; } = true;
    public string DiscordUrl { get; set; } = DefaultDiscordUrl;
    public bool NativeGuardEnabled { get; set; } = true;
    public NativeGuardAction NativeGuardAction { get; set; } = NativeGuardAction.AlertOnly;
    public bool OnlyActWhenUpworkRunning { get; set; } = true;
    public List<string> ProtectedProcessNames { get; set; } = ["Discord", "DiscordPTB", "DiscordCanary"];
    public List<string> UpworkProcessNames { get; set; } = ["Upwork"];
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool EnableGlobalHotkey { get; set; } = true;
    public int GuardCooldownSeconds { get; set; } = 15;

    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscordShield",
            "settings.json");

    public static string DefaultWebViewProfilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscordShield",
            "WebView2Profile");

    public AppSettings Normalize()
    {
        DiscordUrl = IsValidHttpsUrl(DiscordUrl) ? DiscordUrl.Trim() : DefaultDiscordUrl;

        ProtectedProcessNames = NormalizeProcessList(ProtectedProcessNames, ["Discord", "DiscordPTB", "DiscordCanary"]);
        UpworkProcessNames = NormalizeProcessList(UpworkProcessNames, ["Upwork"]);

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

    private static List<string> NormalizeProcessList(List<string>? input, string[] fallback)
    {
        if (input is null)
        {
            return [.. fallback];
        }

        var cleaned = input
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? s.Trim()[..^4]
                : s.Trim())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return cleaned.Count > 0 ? cleaned : [.. fallback];
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
