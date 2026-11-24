namespace DiscordShield.Core;

public enum NewWindowDecision
{
    Block,
    NavigateInPlace,
    OpenExternalBrowser
}

/// <summary>
/// Pure policy helpers for WebView2 popup containment (so popups never spawn unshielded windows)
/// and trusted origin checks for Discord voice/video permissions.
/// </summary>
public static class WebViewPolicy
{
    public static bool IsDiscordOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.Host;
        return host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);
    }

    public static NewWindowDecision DecideNewWindow(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return NewWindowDecision.Block;
        }

        if (IsDiscordOrigin(url))
        {
            return NewWindowDecision.NavigateInPlace;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return NewWindowDecision.OpenExternalBrowser;
        }

        return NewWindowDecision.Block;
    }
}
