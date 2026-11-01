# Locker Pro (Windows)

**Version 1.0.0-beta** · Windows 10/11 (x64) · MIT License

Local-first folder and app locker for Windows, with optional fingerprint unlock from your Android phone.
No cloud, no account, no telemetry: everything stays on your PC and your local Wi-Fi.

> **Beta Software Notice:** Locker Pro is in active public beta testing. Test with non-critical data first, keep backups, and store your **Recovery Key** safely (on paper or a secondary device, not on this PC). If you lose both the Master Password and the Recovery Key, encrypted data cannot be recovered.

Companion Android app: [**Locker Companion**](../android) (in this same repository).

---

## Security Model & Honest Disclaimer (Must Read)

Before deploying Locker Pro, please understand its threat model and what it is—and is not—designed to protect against:

### 1. What Locker Pro Is Designed For:
- **Everyday Privacy & Casual Protection:** Designed for shared family PCs, office workstations, college dorms, and roommates. It prevents unauthorized opening of sensitive apps (Discord, Telegram, Chrome, WhatsApp, photo/video apps) and folders.
- **Entry-Level & Routine Access Prevention:** Intercepts clicks, taskbar shortcuts, start menu items, and renamed `.exe` files using signature matching (size and SHA-256).
- **True Mathematical Cryptography (In-Place AES-256-GCM):** For high-value personal files, use **Folder Lock** or **Portable Vaults**. Every file is encrypted using authenticated AES-256-GCM. Without the password or master key, even an Administrator cannot view plaintext contents.

### 2. Windows Architectural Limitations:
- **No Unbreakable Userland App Lock Exists:** Windows does not have native kernel-level sandbox containment for regular Win32 desktop apps. Any third-party software that locks apps operates in user mode with watchdog processes.
- **Administrator & Forensic Access:** If a user possesses Administrator privileges and is determined to bypass the lock (e.g. by booting Windows into Safe Mode, attaching a live kernel debugger, modifying memory, or using SYSTEM tokens to kill watchdog processes), software app gates can be tampered with.
- **App Lock vs. Data Encryption:**
  - **App Lock is an access interceptor:** It suspends or terminates unauthorized processes. It stops standard users and casual snooping, but it does not encrypt the executable itself.
  - **Vault / Folder Lock is cryptographic protection:** It writes ciphertext directly to disk. Tampering with the app does not decrypt the files.
- **Physical Access:** If an adversary has physical access to the device and can boot into an external Linux live USB or inspect the raw disk, userland locks cannot protect unencrypted data. Pair Locker Pro with BitLocker / full-disk encryption for physical theft protection.

---

## Features

- **Folder lock.** Files inside a locked folder are encrypted in place with AES-256-GCM (`*.aplk`). The folder is also protected with a Windows ACL while locked.
- **App lock.** Launching a protected `.exe` is intercepted and prompts for authentication first. Renamed or copied executables are recognized by size and SHA-256.
- **Portable Vault.** Encrypt any folder in place (`*.locked`, AES-256-GCM) with its own password. The vault can be moved via pendrive and opened on any PC with Locker Pro.
- **Tray App Awareness.** Applications that minimize to the System Tray on close (Discord, Spotify, etc.) stay unlocked while running and only relock upon complete quit.
- **Master Password + Recovery Key.** One Master Password protects everything. A cryptographically random Recovery Key (shown once) can reset it if forgotten.
- **Password policy and lockout.** At least 10 characters with upper case, lower case, a number, and a special character. Repeated wrong attempts trigger progressive lockouts (30 s up to 24 h).
- **Phone fingerprint unlock (optional).** Pair your Android phone via QR code and approve unlocks with your fingerprint instead of typing the password.
- **Background guard.** A watchdog (`LockerGuard`) ensures continuous protection and starts at logon.
- **Clean Installer.** One single executable `Locker-Setup.exe` installs to `C:\Program Files\Locker` and preserves your configuration during updates.

---

## How the security works

| Component | Technical Implementation |
|---|---|
| Master key | Random AES-256 key. Stored only wrapped: once with a key derived from your Master Password (PBKDF2 with HMAC-SHA256, 600,000 iterations) and once with your Recovery Key |
| Folder and vault files | AES-256-GCM, authenticated symmetric encryption with 12-byte nonces and 16-byte authentication tags |
| Password attempts | Local lockout counter with progressive exponential backoff |
| Phone link | QR pairing, then every request is HMAC-signed with a single-use nonce; traffic stays strictly on your local LAN (TCP 58820 on PC, 58821 on phone) |
| Fingerprint unlock | The phone holds an RSA key inside the Android Keystore that can only be used after a live biometric check (`BIOMETRIC_STRONG`). The PC never stores the master key. If a new fingerprint is enrolled on the phone, the hardware key is invalidated and you re-link using your Master Password |

The master key is never written to disk in plaintext.

---

## Install

1. Install the **.NET 10 Desktop Runtime** (x64) if not already installed.
2. Download `Locker-Setup.exe` from the [Releases](https://github.com/inrwxyz/locker-pro/releases) page and run it. Windows will prompt for elevation.
3. Open Locker Pro, create your Master Password, and **write down the Recovery Key** in a secure location.

---

## Pair your phone (optional)

1. Ensure the PC and your phone are connected to the same Wi-Fi network.
2. Open Locker Pro, navigate to **Phone Link**, and click **Pair New Phone** to display the QR code.
3. In Locker Companion on Android, tap **Scan** and point the camera at the QR code.
4. Enter your Master Password on the PC, then touch the fingerprint sensor on your phone.
5. From now on, unlocking any folder or app triggers a prompt on your phone. The Master Password remains available as an offline fallback.

---

## Build from source

Requirements: Windows 10/11, **.NET 10 SDK**, PowerShell 5.1+.

```powershell
git clone https://github.com/inrwxyz/locker-pro.git
cd locker-pro\windows
.\build_package.ps1
```

The installer will be generated at `release\Locker-Setup.exe`.

---

## Reporting Issues

Please open a GitHub issue. For security concerns, use GitHub's private vulnerability reporting tab.

## License

[MIT](../LICENSE). Third-party libraries: [QRCoder](https://github.com/codebude/QRCoder) (MIT), `System.Management` (MIT).
