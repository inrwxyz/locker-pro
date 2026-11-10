# Locker Pro (Windows)

**Version 2.0.0** · Windows 10/11 (x64) · MIT License

Local-first folder and app locker for Windows, with optional fingerprint unlock from your Android phone.  
No cloud, no account, no telemetry: everything stays on your PC and local Wi-Fi.

Companion Android app: [**Locker Companion**](../android) (in this same repository).

---

## Features

### Vault Locker (Highlighted)
> **Portable In-Place Vaults:** Encrypt any folder right where it sits (`*.locked`, AES-256-GCM) using its own separate password. Metadata (`vault.inex`) lives inside the folder itself, so you can move it to a pendrive or another drive and unlock it on any PC with Locker Pro.

- **Folder Lock:** Lock folders on your PC with AES-256-GCM encryption (`*.aplk`), Windows ACL protection, and live progress feedback while locking or unlocking.
- **App Lock:** Prompts for authentication before launching protected `.exe` apps.
- **Tray App Awareness:** Apps that minimize to the system tray stay unlocked while open and relock once closed.
- **Master Password + Recovery Key:** One Master Password protects your locked items, with a Recovery Key shown at setup for resets.
- **Phone Fingerprint Unlock (Optional):** Pair your Android phone via QR code over local Wi-Fi to approve unlocks with your fingerprint. Master Password always works offline.
- **Clean Installer:** `Locker-Setup.exe` installs to `C:\Program Files\Locker` and keeps your settings intact during updates.

---

## Everyday Use Notes

- Locker Pro is intended for everyday personal and shared-PC privacy.
- Always keep a backup of your **Recovery Key** and remember your **Master Password** (and Vault passwords) — encrypted files cannot be opened without them.

---

## Install

1. Install the **.NET 10 Desktop Runtime** (x64) if not already installed.
2. Download `Locker-Setup.exe` from the [Releases](https://github.com/inrwxyz/locker-pro/releases) page and run it.
3. Open Locker Pro, set your Master Password, and save your **Recovery Key** somewhere safe.

---

## Pair your phone (optional)

1. Connect both your PC and phone to the same Wi-Fi network.
2. Open Locker Pro → **Phone Link** → **Pair New Phone** to display the QR code.
3. Open **Locker Companion** on Android, tap **Scan QR code**, and scan the code on your PC.
4. Enter your Master Password on the PC and confirm with your fingerprint on the phone.

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

Please open a GitHub issue if you run into a bug or have feedback.

## License

[MIT](../LICENSE). Third-party libraries: [QRCoder](https://github.com/codebude/QRCoder) (MIT), `System.Management` (MIT).
