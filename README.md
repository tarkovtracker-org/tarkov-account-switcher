# Tarkov Relay — Account Switcher

<img src="docs/brand/tarkov-relay-wordmark.svg" alt="Tarkov Relay Account Switcher" width="480">

Tarkov Relay is a Windows tray utility for switching between your own saved standalone Battlestate Games launcher accounts. Save each account once, then switch between sessions from the tray.

## Use

The switcher follows the Windows app color preference and respects Windows contrast themes.

1. Open the switcher as a standard Windows user. It detects the standalone BSG launcher from its Windows installation entry. If it cannot find one, choose your installed `BsgLauncher.exe`.
2. Sign in directly in the BSG Launcher and leave it open. In the switcher, select **Save current BSG account**, enter a nickname, and save. For another account, sign in to it in the BSG Launcher first, then repeat.
3. Click an account in the switcher's tray menu to switch. In the main window, double-click an account or use **Switch account**.
4. Use **Sign in again** if the launcher rejects a saved session. If the switcher asks you to close the launcher, exit it from its tray menu and retry.

The launcher must finish shutting down before the switcher replaces its settings. Close a running game or a different launcher installation before switching. The utility targets the standalone launcher. Use Steam's own account flow for Steam-linked play.

The switcher preserves unrelated launcher settings and leaves game caches in place. It keeps an encrypted recovery record before making changes. If a switch is interrupted, choose **Restore previous settings** with the launcher closed. If the launcher has since changed its settings, choose **Keep current settings** to discard the older recovery record.

## Local data

The switcher shows nicknames. When you select **Save account**, it reads the BSG Launcher's saved account identifier, usually your email address, and its reusable sign-in session. Account identifiers, sessions, and pending recovery data stay on this PC, protected by Windows DPAPI for the current user under `%LOCALAPPDATA%\TarkovTracker\AccountSwitcher`. The switcher briefly watches the launcher's settings during sign-in and saves newer sessions for known accounts before a switch. Enter your email, password, and verification codes in the BSG Launcher.

Session restoration depends on the launcher's local settings format and whether BSG accepts the saved session. The switcher's status reports process launch. The launcher shows whether sign-in succeeded.

## Updates

The Windows x64 switcher checks GitHub on startup. You can also select **Check for updates** in the window or tray menu. When a new version appears, select **Update to v…** to download, verify, and restart. Builds released before the updater need one final manual installation.

Updates install under the switcher's local app-data directory. The new build becomes the default once its window opens and the account vault loads. Continue opening your original switcher shortcut or executable. To start a specific older copy directly for recovery, run its executable with `--skip-update`.

## Build and verify

Use Windows with the .NET SDK version in `global.json`.

```powershell
dotnet run --project src/TarkovAccountSwitcher
dotnet run --project tests/TarkovAccountSwitcher.Tests -c Release
dotnet publish src/TarkovAccountSwitcher -c Release -r win-x64 --self-contained false
```

The framework-dependent publish requires the .NET 10 Windows Desktop Runtime. The regression tests use synthetic sessions in temporary folders.

For releases, publish a self-contained `win-x64` build with `-p:Version=X.Y.Z` matching the tag `vX.Y.Z`. Package the publish folder as the top-level `TarkovAccountSwitcher` folder in `TarkovAccountSwitcher-vX.Y.Z-win-x64.zip`. The updater expects that asset name and GitHub's SHA-256 digest.
