namespace DiscordShield.Core;

/// <summary>
/// Ensures only one interactive ScreenShield instance runs per Windows user session.
/// If a second instance is launched (e.g. clicking the Desktop shortcut while minimized to tray),
/// it signals the existing instance to pop up immediately and exits cleanly.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _wakeEvent;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public bool IsPrimaryInstance { get; }

    public SingleInstanceGuard(string? nameSuffix = null)
    {
        string suffix = string.IsNullOrWhiteSpace(nameSuffix) ? "v2" : nameSuffix;
        _mutex = new Mutex(initiallyOwned: true, name: $"Local\\ScreenShield_SingleInstance_{suffix}", out bool createdNew);
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\ScreenShield_WakeEvent_{suffix}");
        IsPrimaryInstance = createdNew;
    }

    public void SignalExistingInstance()
    {
        try
        {
            _wakeEvent.Set();
        }
        catch
        {
            // Ignore if existing instance exited concurrently.
        }
    }

    public void StartListeningForWake(Action onWake)
    {
        if (!IsPrimaryInstance) return;

        _ = Task.Run(() =>
        {
            WaitHandle[] handles = [_wakeEvent, _cts.Token.WaitHandle];
            while (!_cts.IsCancellationRequested)
            {
                int signaled = WaitHandle.WaitAny(handles);
                if (signaled == 0 && !_cts.IsCancellationRequested)
                {
                    try { onWake(); }
                    catch { /* Never crash wake listener thread */ }
                }
                else
                {
                    break;
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        _wakeEvent.Dispose();
        if (IsPrimaryInstance)
        {
            try { _mutex.ReleaseMutex(); }
            catch { /* Ignore cross-thread release edge case on shutdown */ }
        }

        _mutex.Dispose();
    }
}
