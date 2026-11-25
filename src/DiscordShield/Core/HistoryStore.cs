using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordShield.Core;

[JsonConverter(typeof(JsonStringEnumConverter<HistoryEventType>))]
public enum HistoryEventType
{
    Screenshot,
    Guard,
    BlockAllow,
    Setting,
    Taskbar,
    Verify,
    Snooze,
    Update,
    System
}

public enum HistoryFilterCategory
{
    All,
    Screenshots,
    Guard,
    Changes
}

public enum HistorySortColumn
{
    Timestamp,
    Type,
    Target,
    Details
}

public sealed record HistoryEntry(
    DateTimeOffset Timestamp,
    HistoryEventType Type,
    string Target,
    string Details);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HistoryEntry))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>
/// Persistent, thread-safe, auto-capped JSONL history store (%LOCALAPPDATA%\ScreenShield\history.jsonl).
/// Records screenshots (with protection context), guard actions, block/allow changes, settings diffs,
/// taskbar auto-hide transitions, snooze events, and updates with zero UI-thread disk blocking.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int DefaultMaxEntries = 5000;

    private readonly string? _filePath;
    private readonly int _maxEntries;
    private readonly object _gate = new();
    private readonly List<HistoryEntry> _entries = [];
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _disposed;

    public event Action<HistoryEntry>? EntryRecorded;

    public static string DefaultHistoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenShield",
            "history.jsonl");

    public HistoryStore(string? filePath = null, int maxEntries = DefaultMaxEntries, bool loadExisting = true)
    {
        _filePath = filePath;
        _maxEntries = Math.Max(50, maxEntries);
        if (loadExisting && !string.IsNullOrWhiteSpace(_filePath))
        {
            LoadFromDisk();
        }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public HistoryEntry Record(HistoryEventType type, string target, string details, DateTimeOffset? timestamp = null)
    {
        var entry = new HistoryEntry(
            timestamp ?? DateTimeOffset.Now,
            type,
            target ?? string.Empty,
            details ?? string.Empty);

        bool needsCompaction = false;
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > _maxEntries)
            {
                int removeCount = _entries.Count - _maxEntries;
                _entries.RemoveRange(0, removeCount);
                needsCompaction = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(_filePath) && !_disposed)
        {
            if (needsCompaction)
            {
                _ = RewriteFileAsync();
            }
            else
            {
                _ = AppendLineAsync(entry);
            }
        }

        EntryRecorded?.Invoke(entry);
        return entry;
    }

    public IReadOnlyList<HistoryEntry> Query(
        HistoryFilterCategory category = HistoryFilterCategory.All,
        string? search = null,
        HistorySortColumn sortBy = HistorySortColumn.Timestamp,
        bool ascending = false)
    {
        List<HistoryEntry> snapshot;
        lock (_gate)
        {
            snapshot = new List<HistoryEntry>(_entries.Count);
            string q = (search ?? string.Empty).Trim();
            bool hasQuery = q.Length > 0;

            for (int i = 0; i < _entries.Count; i++)
            {
                HistoryEntry e = _entries[i];
                if (!MatchesCategory(e.Type, category)) continue;
                if (hasQuery
                    && !e.Target.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !e.Details.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !FormatTypeLabel(e.Type).Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                snapshot.Add(e);
            }
        }

        snapshot.Sort((a, b) =>
        {
            int cmp = sortBy switch
            {
                HistorySortColumn.Type => string.Compare(FormatTypeLabel(a.Type), FormatTypeLabel(b.Type), StringComparison.OrdinalIgnoreCase),
                HistorySortColumn.Target => string.Compare(a.Target, b.Target, StringComparison.OrdinalIgnoreCase),
                HistorySortColumn.Details => string.Compare(a.Details, b.Details, StringComparison.OrdinalIgnoreCase),
                _ => a.Timestamp.CompareTo(b.Timestamp)
            };

            if (cmp == 0)
            {
                cmp = a.Timestamp.CompareTo(b.Timestamp);
            }

            return ascending ? cmp : -cmp;
        });

        return snapshot;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        if (!string.IsNullOrWhiteSpace(_filePath) && !_disposed)
        {
            _ = RewriteFileAsync();
        }
    }

    public async Task FlushAsync()
    {
        if (_disposed) return;
        await _ioLock.WaitAsync().ConfigureAwait(false);
        _ioLock.Release();
    }

    public static string FormatTypeLabel(HistoryEventType type) => type switch
    {
        HistoryEventType.Screenshot => "Screenshot",
        HistoryEventType.Guard => "Guard",
        HistoryEventType.BlockAllow => "Block/Allow",
        HistoryEventType.Setting => "Setting",
        HistoryEventType.Taskbar => "Taskbar",
        HistoryEventType.Verify => "Verify",
        HistoryEventType.Snooze => "Snooze",
        HistoryEventType.Update => "Update",
        _ => "System"
    };

    public static string ExportCsv(IEnumerable<HistoryEntry> entries)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("Date,Time,Type,Target,Details");
        foreach (HistoryEntry e in entries)
        {
            DateTime local = e.Timestamp.LocalDateTime;
            sb.Append(local.ToString("yyyy-MM-dd")).Append(',')
              .Append(local.ToString("HH:mm:ss")).Append(',')
              .Append(EscapeCsv(FormatTypeLabel(e.Type))).Append(',')
              .Append(EscapeCsv(e.Target)).Append(',')
              .AppendLine(EscapeCsv(e.Details));
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        bool needsQuotes = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        if (!needsQuotes) return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static bool MatchesCategory(HistoryEventType type, HistoryFilterCategory category) => category switch
    {
        HistoryFilterCategory.Screenshots => type == HistoryEventType.Screenshot,
        HistoryFilterCategory.Guard => type is HistoryEventType.Guard or HistoryEventType.Taskbar or HistoryEventType.Verify,
        HistoryFilterCategory.Changes => type is HistoryEventType.BlockAllow or HistoryEventType.Setting or HistoryEventType.Snooze or HistoryEventType.Update,
        _ => true
    };

    private void LoadFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath)) return;

        try
        {
            var loaded = new List<HistoryEntry>();
            foreach (string line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    HistoryEntry? entry = JsonSerializer.Deserialize(line, HistoryJsonContext.Default.HistoryEntry);
                    if (entry is not null)
                    {
                        loaded.Add(entry);
                    }
                }
                catch
                {
                    // Skip any malformed line cleanly.
                }
            }

            if (loaded.Count > _maxEntries)
            {
                loaded.RemoveRange(0, loaded.Count - _maxEntries);
            }

            lock (_gate)
            {
                _entries.Clear();
                _entries.AddRange(loaded);
            }
        }
        catch
        {
            // Ignore unreadable history file so startup never fails.
        }
    }

    private async Task AppendLineAsync(HistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_filePath)) return;
        try
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                string line = JsonSerializer.Serialize(entry, HistoryJsonContext.Default.HistoryEntry) + Environment.NewLine;
                await File.AppendAllTextAsync(_filePath, line, Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch
        {
            // Best-effort persistence; never crash UI on transient file lock.
        }
    }

    private async Task RewriteFileAsync()
    {
        if (string.IsNullOrWhiteSpace(_filePath)) return;
        try
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                List<HistoryEntry> snap;
                lock (_gate) snap = [.. _entries];

                var sb = new StringBuilder(snap.Count * 96);
                foreach (HistoryEntry e in snap)
                {
                    sb.AppendLine(JsonSerializer.Serialize(e, HistoryJsonContext.Default.HistoryEntry));
                }

                await File.WriteAllTextAsync(_filePath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch
        {
            // Best-effort persistence.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _ioLock.Dispose();
    }
}
