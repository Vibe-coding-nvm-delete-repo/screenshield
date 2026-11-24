using System.IO;

namespace DiscordShield.Core;

public sealed record UpworkScreenshotEvent(
    DateTimeOffset DetectedAt,
    string FileName,
    int SessionCount);

public interface IUpworkScreenshotWatcher : IDisposable
{
    DateTimeOffset? LastScreenshotAt { get; }
    int SessionScreenshotCount { get; }
    bool IsWatchingAnyFolder { get; }
    event Action<UpworkScreenshotEvent>? ScreenshotDetected;
    string FormatLastScreenshot(DateTimeOffset now);
}

/// <summary>
/// Read-only FileSystemWatcher that detects when Upwork Time Tracker writes a new screenshot
/// image into its local cache/temp directory, recording exact timestamps for the Activity Log
/// and header status bar. Zero hooking, zero CPU polling, safe if Upwork is not installed.
/// </summary>
public sealed class UpworkScreenshotWatcher : IUpworkScreenshotWatcher
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp"
    };

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _gate = new();
    private readonly TimeSpan _debounce;
    private DateTimeOffset? _lastScreenshotAt;
    private int _sessionCount;
    private bool _disposed;

    public DateTimeOffset? LastScreenshotAt
    {
        get { lock (_gate) return _lastScreenshotAt; }
    }

    public int SessionScreenshotCount
    {
        get { lock (_gate) return _sessionCount; }
    }

    public bool IsWatchingAnyFolder => _watchers.Count > 0;

    public event Action<UpworkScreenshotEvent>? ScreenshotDetected;

    public UpworkScreenshotWatcher(IEnumerable<string>? watchDirectories = null, TimeSpan? debounce = null)
    {
        _debounce = debounce ?? TimeSpan.FromSeconds(3);
        IEnumerable<string> dirs = watchDirectories ?? GetDefaultUpworkDirectories();

        foreach (string dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                var fsw = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                fsw.Created += OnFileActivity;
                fsw.Changed += OnFileActivity;
                fsw.Renamed += OnFileRenamed;
                _watchers.Add(fsw);
            }
            catch
            {
                // Ignore directories that deny watcher creation.
            }
        }
    }

    public static IReadOnlyList<string> GetDefaultUpworkDirectories()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            Path.Combine(local, "Upwork", "Upwork"),
            Path.Combine(local, "Upwork"),
            Path.Combine(roaming, "Upwork")
        ];
    }

    public string FormatLastScreenshot(DateTimeOffset now)
    {
        DateTimeOffset? last = LastScreenshotAt;
        if (!last.HasValue)
        {
            return "Last SS: none yet";
        }

        TimeSpan elapsed = now - last.Value;
        if (elapsed.TotalSeconds < 45)
        {
            return $"Last SS: just now ({last.Value.LocalDateTime:HH:mm:ss})";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"Last SS: {(int)elapsed.TotalMinutes}m ago ({last.Value.LocalDateTime:HH:mm})";
        }

        return $"Last SS: {last.Value.LocalDateTime:HH:mm}";
    }

    private void OnFileActivity(object sender, FileSystemEventArgs e) => HandlePath(e.FullPath);
    private void OnFileRenamed(object sender, RenamedEventArgs e) => HandlePath(e.FullPath);

    private void HandlePath(string fullPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        string ext = Path.GetExtension(fullPath);
        if (!ImageExtensions.Contains(ext))
        {
            return;
        }

        UpworkScreenshotEvent? evt = null;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_lastScreenshotAt.HasValue && (now - _lastScreenshotAt.Value) < _debounce)
            {
                return;
            }

            _lastScreenshotAt = now;
            _sessionCount++;
            evt = new UpworkScreenshotEvent(now, Path.GetFileName(fullPath), _sessionCount);
        }

        ScreenshotDetected?.Invoke(evt);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (FileSystemWatcher fsw in _watchers)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Created -= OnFileActivity;
            fsw.Changed -= OnFileActivity;
            fsw.Renamed -= OnFileRenamed;
            fsw.Dispose();
        }

        _watchers.Clear();
    }
}
