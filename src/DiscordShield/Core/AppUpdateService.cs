using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordShield.Core;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentHashShort,
    string? RemoteHashShort,
    string? DownloadUrl,
    string AssetName,
    string Message);

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAssetDto>? Assets { get; set; }
}

internal sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class UpdateReleaseJsonContext : JsonSerializerContext;

public interface IAppUpdateService
{
    string CurrentVersion { get; }
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default);
    Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, CancellationToken ct = default);
}

/// <summary>
/// Lightweight, zero-overhead GitHub Releases update checker & hot-swapper.
/// Auto-detects whether the user is running the Lite (< 5 MB) or Full self-contained binary
/// and compares SHA-256 against the live v2.1.0 release asset digest.
/// </summary>
public sealed class AppUpdateService : IAppUpdateService
{
    public const string AppVersion = "v2.1.0";
    public const string DefaultReleaseApiUrl = "https://api.github.com/repos/Vibe-coding-nvm-delete-repo/screenshield/releases/tags/v2.1.0";

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ScreenShield/{AppVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    });

    private readonly Func<CancellationToken, Task<string>> _fetchReleaseJson;
    private readonly Func<string?> _getLocalExePath;

    public string CurrentVersion => AppVersion;

    public AppUpdateService(
        Func<CancellationToken, Task<string>>? fetchReleaseJson = null,
        Func<string?>? getLocalExePath = null)
    {
        _fetchReleaseJson = fetchReleaseJson ?? (ct => SharedHttp.Value.GetStringAsync(DefaultReleaseApiUrl, ct));
        _getLocalExePath = getLocalExePath ?? (() => Environment.ProcessPath);
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        string? exePath = _getLocalExePath();
        bool isLite = IsLiteBinary(exePath);
        string assetName = isLite ? "ScreenShield-Lite.exe" : "ScreenShield.exe";
        string localHash = ComputeSha256Hex(exePath);
        string localShort = ShortHash(localHash);

        try
        {
            string json = await _fetchReleaseJson(ct).ConfigureAwait(false);
            GitHubReleaseDto? release = JsonSerializer.Deserialize(json, UpdateReleaseJsonContext.Default.GitHubReleaseDto);
            GitHubReleaseAssetDto? asset = release?.Assets?.FirstOrDefault(
                a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));

            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                return new UpdateCheckResult(false, localShort, null, null, assetName, "No matching release asset found on GitHub.");
            }

            string remoteHash = ParseSha256Digest(asset.Digest);
            string? remoteShort = string.IsNullOrEmpty(remoteHash) ? null : ShortHash(remoteHash);

            if (!string.IsNullOrEmpty(remoteHash) && !string.IsNullOrEmpty(localHash))
            {
                bool differs = !string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase);
                return differs
                    ? new UpdateCheckResult(true, localShort, remoteShort, asset.BrowserDownloadUrl, assetName, $"New build available ({remoteShort}, current {localShort}).")
                    : new UpdateCheckResult(false, localShort, remoteShort, asset.BrowserDownloadUrl, assetName, $"You're on the latest build ({localShort}).");
            }

            if (asset.UpdatedAt.HasValue && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                DateTimeOffset localUtc = File.GetLastWriteTimeUtc(exePath);
                bool newer = asset.UpdatedAt.Value > localUtc.AddMinutes(2);
                return newer
                    ? new UpdateCheckResult(true, localShort, remoteShort, asset.BrowserDownloadUrl, assetName, "A newer ScreenShield build is available.")
                    : new UpdateCheckResult(false, localShort, remoteShort, asset.BrowserDownloadUrl, assetName, $"You're on the latest build ({AppVersion}).");
            }

            return new UpdateCheckResult(false, localShort, remoteShort, asset.BrowserDownloadUrl, assetName, $"Up to date ({AppVersion}).");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, localShort, null, null, assetName, $"Update check failed: {ex.Message}");
        }
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, CancellationToken ct = default)
    {
        string? exePath = _getLocalExePath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;

        string stagedPath = exePath + ".new";
        using (HttpResponseMessage resp = await SharedHttp.Value.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        // Verify valid Windows PE header ("MZ") before staging swap
        byte[] header = new byte[2];
        await using (var verify = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (verify.Length < 65536 || await verify.ReadAsync(header.AsMemory(0, 2), ct).ConfigureAwait(false) < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                try { File.Delete(stagedPath); } catch { }
                return false;
            }
        }

        string swapScript = Path.Combine(Path.GetTempPath(), $"screenshield-swap-{Environment.ProcessId}.cmd");
        int pid = Environment.ProcessId;
        string script = $"""
            @echo off
            :wait
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )
            move /y "{stagedPath}" "{exePath}" >nul
            start "" "{exePath}"
            del "%~f0"
            """;
        await File.WriteAllTextAsync(swapScript, script, ct).ConfigureAwait(false);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{swapScript}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        return true;
    }

    public static bool IsLiteBinary(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
        try
        {
            return new FileInfo(exePath).Length < 5 * 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    public static string ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return string.Empty;
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..].Trim()
            : digest.Trim();
    }

    public static string ComputeSha256Hex(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return string.Empty;
        try
        {
            using FileStream fs = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ShortHash(string hex) =>
        string.IsNullOrEmpty(hex) ? "dev" : hex[..Math.Min(10, hex.Length)].ToUpperInvariant();
}
