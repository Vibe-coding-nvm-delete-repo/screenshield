using DiscordShield.Native;
using Microsoft.Win32;

namespace DiscordShield.Core;

public interface ITaskbarShieldService : IDisposable
{
    bool IsTaskbarCurrentlyShielded { get; }
    bool ApplyTaskbarShield(bool shouldHide);
    void RestoreOriginalState();
}

/// <summary>
/// Uses the official Windows Shell API SHAppBarMessage(ABM_SETSTATE, ABS_AUTOHIDE) to hide
/// the Windows taskbar while a blocked app/site is open so Upwork screenshots cannot see
/// blocked app icons on the taskbar. Always restores the user's original taskbar setting on exit.
/// </summary>
public sealed class TaskbarShieldService : ITaskbarShieldService
{
    private readonly Func<bool> _getState;
    private readonly Func<bool, bool> _setState;
    private bool? _originalAutoHide;
    private bool _currentlyShielded;
    private bool _disposed;

    public TaskbarShieldService(
        Func<bool>? getState = null,
        Func<bool, bool>? setState = null)
    {
        _getState = getState ?? Win32Interop.GetTaskbarAutoHideState;
        _setState = setState ?? Win32Interop.SetTaskbarAutoHide;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExitOrSessionEnding;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void OnProcessExitOrSessionEnding(object? sender, EventArgs e) => RestoreOriginalState();
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => RestoreOriginalState();

    public bool IsTaskbarCurrentlyShielded => _currentlyShielded;

    public bool ApplyTaskbarShield(bool shouldHide)
    {
        if (_disposed)
        {
            return false;
        }

        if (shouldHide == _currentlyShielded)
        {
            return true; // Idempotent: 0 ms no-op
        }

        if (shouldHide)
        {
            _originalAutoHide ??= _getState();
            if (_originalAutoHide.Value)
            {
                _currentlyShielded = true;
                return true;
            }

            bool ok = _setState(true);
            if (ok)
            {
                _currentlyShielded = true;
            }

            return ok;
        }

        RestoreOriginalState();
        return true;
    }

    public void RestoreOriginalState()
    {
        if (!_currentlyShielded)
        {
            return;
        }

        if (_originalAutoHide.HasValue && !_originalAutoHide.Value)
        {
            _ = _setState(false);
        }

        _currentlyShielded = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExitOrSessionEnding;
        SystemEvents.SessionEnding -= OnSessionEnding;
        RestoreOriginalState();
        _disposed = true;
    }
}
