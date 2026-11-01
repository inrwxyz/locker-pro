# Locker

**Version 1.0.0-beta** · MIT License

Lock folders and apps on Windows, and unlock them with your fingerprint from your Android phone.
Everything is local: no cloud, no account, no telemetry. The phone and the PC talk directly over your local Wi-Fi.

> **Beta Software Notice:** Locker Pro is in active public beta testing. Test with non-critical data first, keep backups, and store your Master Password and **Recovery Key** safely.

| Component | What it is | Path |
|---|---|---|
| **Locker Pro** | Windows application: folder lock, app lock, AES-256 portable vault, watchdog guard, installer | [`windows/`](windows) |
| **Locker Companion** | Android application: turns your phone's hardware biometric sensor into a wireless fingerprint key | [`android/`](android) |

---

## Security Model & Realistic Expectations

Please read this section carefully before relying on Locker Pro. We believe in total transparency rather than making unrealistic marketing claims.

### 1. What Locker Pro Is Designed For (Target Use Case)
- **Everyday Privacy & Casual Access Control:** Protecting your personal apps (WhatsApp, Discord, Chrome, Telegram, Steam, photo viewers) and folders from roommates, siblings, family members, or curious coworkers when sharing a PC or stepping away.
- **Entry-Level Interception:** Catching shortcut launches, executable clicks, and renamed binaries with an instant biometric or password gate.
- **Mathematical File Protection (AES-256-GCM):** For sensitive files, Locker Pro provides **Folder Locking** and **Portable Vaults**. These encrypt files in-place using authenticated AES-256-GCM. Even an Administrator cannot view the contents without the password.

### 2. Technical Limitations on Windows (Honest Disclaimer)
- **UI & App Lock Reality:** Windows does not provide native, unbreakable kernel-level app sandboxing for standard Win32 applications. **There is no third-party app locker on Windows that cannot be bypassed by someone with Administrator privileges and sufficient technical effort.**
- **Admin & Safe Mode Tampering:** An advanced user with full Administrator rights who boots Windows into Safe Mode, uses kernel-level process killers, attaches live debuggers, or uses SYSTEM tokens can tamper with user-mode app guards.
- **App Gating vs. Data Encryption:**
  - **App Lock is an access gate:** It intercepts launches and blocks user interaction. It stops normal users and casual snooping.
  - **Vault / Folder Lock is cryptographic security:** It mathematically scrambles files on disk. Without the key, the data cannot be read even by an Administrator.
- **Physical Device Access:** If an adversary has unrestricted physical access to your hardware (e.g. booting into a Linux live USB or using hardware keyloggers), userland software cannot stop them. We strongly recommend pairing Locker Pro with BitLocker / full-disk encryption for physical theft protection.

---

## How it works

1. **Locker Pro (Windows)** encrypts folders in-place (AES-256-GCM) and gates locked applications. One Master Password (plus a random Recovery Key) protects everything.
2. **Optional Phone Companion:** Pair your Android phone by scanning the QR code displayed on the PC.
3. When you open a locked folder or app, your phone prompts for your fingerprint. The key is released directly from an Android Keystore hardware-backed key (`BIOMETRIC_STRONG`). The PC never stores the master key for this exchange.
4. **Offline Fallback:** The Master Password always works if your phone is offline or unreachable.

If a new fingerprint is enrolled on the phone, Android automatically destroys the Keystore key by design, preventing unauthorized fingers from unlocking your PC.

---

## Download

Get the Windows installer (`Locker-Setup.exe`) and the Android companion (`.apk`) from the
[Releases](https://github.com/inrwxyz/locker-pro/releases) page.

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

Full details: [windows/README.md](windows/README.md) and [android/README.md](android/README.md).

---

## Reporting Issues

Found a bug or have an idea? Please open a GitHub issue. For potential security vulnerabilities, please use GitHub's private vulnerability reporting tab.

## License

[MIT](LICENSE)
