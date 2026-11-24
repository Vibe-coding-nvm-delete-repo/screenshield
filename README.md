# DiscordShield — Prevent Discord from Appearing in Upwork Screenshots (Windows 10/11)

A native `.NET 8` Windows desktop utility that keeps Discord out of Upwork Time Tracker screenshots using **documented, non-invasive Windows APIs**—zero DLL injection, zero hooking of `Upwork.exe` or `Discord.exe`, and zero antivirus/anti-cheat flags.

---

## How It Works (First-Principles Architecture)

### Why You Can't Just "Block" `Discord.exe` Externally
Upwork Time Tracker captures your screen using standard Windows screen-capture APIs (GDI `BitBlt`, DXGI Desktop Duplication, or `Windows.Graphics.Capture`).

Windows 10 (build 19041 / 2004+) and Windows 11 provide a native Desktop Window Manager (DWM) API specifically built for privacy windows (used by 1Password, Signal, banking apps, etc.):

```csharp
SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); // 0x00000011
```

When a window has `WDA_EXCLUDEFROMCAPTURE` set:
- **You** see and interact with the window completely normally on your monitor.
- **Any screen-capture API** (`BitBlt`, `PrintWindow`, DXGI, `Windows.Graphics.Capture`, OBS, Upwork) receives the desktop **as if that window does not exist**—whatever is behind it (your IDE, browser, wallpaper) shows through seamlessly instead of a black box.

**Critical Windows OS constraint:** Microsoft strictly restricts `SetWindowDisplayAffinity` to the **owning process** of the window (`ERROR_ACCESS_DENIED` if called cross-process). Calling it on native `Discord.exe` would require DLL injection (fragile, breaks on updates, trips AV/anti-cheat). Hooking `Upwork.exe` to intercept or spoof screenshots violates Upwork's Terms of Service and risks permanent account suspension.

### How DiscordShield Solves Both Workflows Cleanly

1. **Mode 1 (Recommended — Works on Single-Monitor Setups): Screenshot-Excluded Discord Window**
   - Embeds `https://discord.com/app` inside a native Microsoft Edge **WebView2** window owned by `DiscordShield.exe` (with a persistent Chromium profile at `%LocalAppData%\DiscordShield\WebView2Profile`, so login/cookies/voice settings persist).
   - Intercepts `NewWindowRequested` so Discord popouts stay inside the shielded window (or open external links in your default browser) and never spawn an unshielded popup window; auto-grants `Microphone`, `Camera`, and `Notifications` permissions for verified `https://discord.com` origins so voice chat works smoothly.
   - Calls `SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE)` on its own HWND, verifies readback via `GetWindowDisplayAffinity`, and runs a 3-second watchdog to re-assert affinity if Windows resets it after display changes.
   - Includes a **1-click `Verify Now` Live Screenshot Proof** button that briefly paints a `#FF00FF` sentinel patch inside the window, captures that exact screen region via real Windows `Graphics.CopyFromScreen` (`BitBlt`), and confirms `0` sentinel pixels leaked.
   - Includes System Tray integration (`Minimize to tray on close`) and a global toggle hotkey (`Ctrl + Shift + D`).

2. **Mode 2 (Safety Net for Native `Discord.exe`): Native Discord Guard**
   - Monitors visible, non-minimized top-level windows belonging to `Discord`, `DiscordPTB`, or `DiscordCanary` (customizable) while `Upwork` is running.
   - Configurable actions:
     - **Alert Only** (default): System tray warning reminding you native `Discord.exe` IS visible to screenshots.
     - **Auto-Minimize**: Automatically minimizes native `Discord.exe` when Upwork is running.
     - **Move to Secondary Monitor**: Moves native `Discord.exe` to a non-primary monitor (automatically fails closed to `Alert Only` with a clear warning if only 1 monitor is connected).
     - **Pause Upwork + Alert**: Uses standard Microsoft UI Automation (`System.Windows.Automation`) to click Upwork Time Tracker's Stop/Pause button (non-invasive; depends on Upwork's current accessibility labels).

---

## Important Transparency & Limitations

- **Active Window Title & Activity**: `WDA_EXCLUDEFROMCAPTURE` hides the *visual pixels* of the window from screenshots, **not** the fact that you typed/clicked or the foreground window's title if Upwork logs active window titles. If you are taking a personal chat break on Discord, you should pause Upwork Time Tracker (or use Mode 2's Pause/Alert) so you are not billing client time.
- **OS Requirement**: Requires Windows 10 version 2004 (build 19041) or Windows 11, plus the Microsoft Edge WebView2 Runtime (pre-installed on Windows 11).
- **Single-Monitor Systems**: `Move to Secondary Monitor` requires 2+ displays; on a single-monitor setup, use Mode 1 (the built-in excluded Discord window) or Mode 2 (`Auto-Minimize` / `Alert Only`).

---

## Build, Run, and Verify

```powershell
# 1. Build the solution
dotnet build

# 2. Run all unit tests + real Windows GDI screenshot proof test
dotnet test

# 3. Run the standalone CLI live screenshot self-test (JSON output):
#    exit 0 = proven excluded on live desktop (0 leaked sentinel pixels)
#    exit 2 = affinity readback verified (0x11), but desktop capture unavailable (screen locked / non-interactive)
#    exit 1 = verification failed
dotnet run --project src/DiscordShield/DiscordShield.csproj -- --self-test

# 4. Launch the DiscordShield desktop app
dotnet run --project src/DiscordShield/DiscordShield.csproj
```
