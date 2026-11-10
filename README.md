# Locker Pro

**Version 2.0.0** · MIT License

A simple, local-first folder and app locker for Windows, with an optional Android companion app for quick fingerprint unlocking over local Wi-Fi.  
Everything stays on your PC and local network — no cloud, no account, and no telemetry.

| Component | What it is | Path |
|---|---|---|
| **Locker Pro** | Windows app: Vault Locker, Folder Lock, App Lock, background guard, installer | [`windows/`](windows) |
| **Locker Companion** | Android app: unlock prompts using your phone's fingerprint sensor | [`android/`](android) |

---

## Key Features

### Vault Locker (Highlighted)
> **Portable Password-Protected Vaults:** Lock any folder in place (`*.locked` using AES-256-GCM) with its own independent vault password. Because everything needed to unlock stays inside the folder (`vault.inex`), you can copy or carry a locked vault on a USB drive and open it on any PC running Locker Pro — just with the vault password.

- **Folder Lock:** Lock personal folders on your PC with AES-256-GCM encryption (`*.aplk`) and automatic unlock prompts with live progress when you open them in File Explorer.
- **App Lock:** Protect desktop applications (`.exe`) behind your Master Password or phone fingerprint before they launch.
- **Phone Fingerprint Unlock (Optional):** Pair your Android phone via QR code over local Wi-Fi to approve unlock prompts with a fingerprint tap. Your Master Password (and Recovery Key) always works as an offline fallback.

---

## Notes for Everyday Use

- Locker Pro is designed for everyday desktop privacy on personal and shared Windows PCs.
- Keep your **Master Password** and **Recovery Key** saved somewhere safe (and remember each Vault's independent password). If you lose your password and recovery key, encrypted files cannot be recovered.

---

## Download

Get the Windows installer (`Locker-Setup.exe`) and the Android companion (`.apk`) from the [Releases](https://github.com/inrwxyz/locker-pro/releases) page.

---

## Build from source

```powershell
git clone https://github.com/inrwxyz/locker-pro.git
cd locker-pro

# Windows app (requires .NET 10 SDK)
cd windows
.\build_package.ps1            # -> release\Locker-Setup.exe

# Android app (requires JDK 17+ and Android SDK 35)
cd ..\android
.\gradlew.bat assembleDebug    # -> app\build\outputs\apk\debug\app-debug.apk
```

More details: [windows/README.md](windows/README.md) and [android/README.md](android/README.md).

---

## Reporting Issues

Found a bug or have an idea? Please open a GitHub issue.

## License

[MIT](LICENSE)
