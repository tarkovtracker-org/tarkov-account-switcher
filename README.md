# Tarkov Account Switcher

A Windows tray utility for switching between your own standalone Battlestate Games launcher accounts.

## Use

1. Open the switcher as a standard Windows user and choose your installed `BsgLauncher.exe`.
2. Add an account by name and email. Sign in directly in the BSG launcher.
3. Click an account in the switcher's tray menu to switch. In the main window, double-click an account or use **Switch account**.
4. Use **Sign in again** if the launcher rejects a saved session. If the switcher asks you to close the launcher, exit it from its tray menu and retry.

The launcher must finish shutting down before the switcher replaces its settings. Close a running game or a different launcher installation before switching. The utility targets the standalone launcher. Use Steam's own account flow for Steam-linked play.

The switcher preserves unrelated launcher settings and leaves game caches in place. It keeps an encrypted recovery record before making changes. If a switch is interrupted, choose **Restore previous settings** with the launcher closed. If the launcher has since changed its settings, choose **Keep current settings** to discard the older recovery record.

## Local data

Account details, saved sessions, and pending recovery data are protected by Windows DPAPI for the current user and stored under `%LOCALAPPDATA%\TarkovTracker\AccountSwitcher`. The BSG launcher still owns its original settings file. The switcher briefly watches that file during sign-in and can save newer sessions for known accounts before a switch. Keep account passwords and authentication prompts inside the launcher.

Session restoration depends on the launcher's local settings format and whether BSG accepts the saved session. The switcher's status reports process launch. The launcher shows whether sign-in succeeded.

## Build and verify

Use Windows with the .NET SDK version in `global.json`.

```powershell
dotnet run --project src/TarkovAccountSwitcher
dotnet run --project tests/TarkovAccountSwitcher.Tests -c Release
dotnet publish src/TarkovAccountSwitcher -c Release -r win-x64 --self-contained false
```

The framework-dependent publish requires the .NET 10 Windows Desktop Runtime. The regression tests use synthetic sessions in temporary folders.
