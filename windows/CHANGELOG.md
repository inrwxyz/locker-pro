# Changelog

## 2.1.1

- Closed the File Picker (`#32770` Open/Save/Browse dialog) guard bypass by monitoring dialog address bars via `SetWinEventHook` + UI Automation and automatically navigating unauthorized dialogs out of locked folders.
- Eliminated Explorer guard flash/lag by switching `ExplorerWatcher` to real-time `SetWinEventHook` + `45 ms` foreground responsiveness and fixing the root-parent early-return bug in `NavigateWindowAway`.
- Hardened Phone Link biometric unlock prompts: added fast TCP retry, immediate `auth_cancel` cancellation when closing/canceling PC prompts, accurate status + **Retry phone prompt** recovery on `GuardPopup`, and `reprompt` / `pendingTarget` dashboard recovery inside Android Companion.
- Security, leak, and dead-code cleanup: removed legacy `AppLocker.UI`, `AppLocker.Launch`, `AppLocker.Shell` projects and `[Obsolete]` insecure vault methods; cleared derived keys/cipher buffers in RAM (`CryptographicOperations.ZeroMemory` / `Array.Clear`); bounded `guard.log` at 256 KB; disposed leaked `Process` handles; and guarded partial-encryption rollbacks in `FolderLocker.Lock`.

## 2.1.0

- Restored original clean and minimal application logos and `.ico` files across Windows Manager, Installer, and Uninstaller.
- Added bidirectional PC presence notifications to companion phone: sends online handshake on listener start/pairing and offline signal on exit/shutdown.
- Modernized and refreshed UI layout across the Manager, Installer, Uninstaller, Guard popup, and toast chips.
- Hardened the build script pipeline with automated cache purges to ensure instant reflection of compiled XAML modifications.

## 2.0.0

- Upgraded Folder Lock encryption/decryption throughput (1 MB chunked streaming, zero-allocation nonce handling, parallel file processing, and backward compatibility with legacy 64 KB `.aplk` chunks).
- Added live progress reporting across Explorer unlock/lock popups, the floating Lock Chip, and the Manager window.
- Added multi-folder simultaneous unlock with per-folder stacked **Lock now** chips, same-tab Explorer auto-open on unlock, and Windows 11 multi-tab Explorer detection (UI Automation) to prevent false auto-relocks when switching tabs.
- Hardened IPC and uninstall security: restricted `\\.\pipe\AppLockerGuard` and `AppLockerWatchdogStop` event ACLs to `SYSTEM` + `Administrators`, replaced `--pw` CLI passing with a one-time hardened `--handoff` token, and improved `Process` handle disposal.
- Fixed Recovery Key rotation (`New Key / Copy`), enforced `Require Same Wi-Fi Connection` subnet checks, marked Bluetooth proximity as coming soon, added two-way Phone Link unpair sync, prevented duplicate startup Master Password prompts on reinstall/double-launch, and standardized all UI copy in English.
- Updated UI theme and application icons.

## 0.9.0-beta

- Fingerprint unlock rebuilt: the master key is no longer stored on the PC. The PC keeps only a blob that the phone's Keystore key (live fingerprint required) can open. The old `biometric.vault` is removed automatically.
- Fingerprint is linked right after pairing and again after a Master Password entry when the phone's fingerprints changed.
- Installer adds a Windows Firewall rule for the phone link (TCP 58820) and the uninstaller removes it.
- Installer no longer grants the Users group access to the Locker config folder.
- New app and installer icon.
- `build_package.ps1` no longer uses machine specific paths; output goes to `release\`.
- Vault, password policy, lockout, app/folder guard and installer changes from earlier internal versions (see git history).
