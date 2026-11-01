# Locker Companion

**Version 1.0.0-beta** · Android 7.0+ (API 24) · MIT License

Android companion for [**Locker Pro**](../windows) (Windows). It turns your phone into a fingerprint key: when your PC asks to open a locked folder or app, the phone asks for your fingerprint and releases the key.
Everything runs over your local Wi-Fi. No cloud, no account, no analytics.

> **Beta software.** Not independently audited. Always keep your Master Password and Recovery Key from Locker Pro, the phone is only a shortcut.

---

## Features

- **Fingerprint key in hardware.** On setup the app creates an RSA key in the Android Keystore that can be used only after a live strong biometric (`BIOMETRIC_STRONG`). The screen lock PIN or pattern cannot approve an unlock.
- **Only the fingers you enrolled.** The key is destroyed by Android if a new fingerprint is added, so a newly added finger cannot unlock your PC. Re-link with your Master Password.
- **QR pairing.** Scan the QR code shown by Locker Pro. The pairing secret travels only in the QR code, never over the network.
- **Signed requests.** Every request from the PC is HMAC-signed with a single-use nonce, so replayed or forged requests are rejected.
- **Local only.** Talks directly to the PC over LAN (PC port 58820, phone port 58821). Works without internet.
- **Passkey-style prompt.** A notification (or the system fingerprint dialog) appears when the PC asks, approve and you are done.
- **Simple UI.** Shows the connected computer, a connection test and Disconnect. Disconnecting also deletes the fingerprint key from the phone.

## Install

Download the APK from the [Releases](https://github.com/inrwxyz/locker-pro/releases) page, or build it yourself (below). Android may ask you to allow installs from your browser or file manager.

Permissions used: camera (scan the pairing QR), network and Wi-Fi state (talk to your PC), biometric (fingerprint), notifications and foreground service (receive unlock requests), vibrate.

## Pair with your PC

1. Install and open **Locker Pro** on the PC (same Wi-Fi as the phone).
2. In Locker Pro open **Phone Link** and click **Pair New Phone**.
3. In this app tap **Scan** and scan the QR code.
4. Enter your Master Password on the PC, then touch the fingerprint sensor on the phone once to link your fingerprints.

## Build from source

Requirements: JDK 17+, Android SDK Platform 35 (Android Studio recommended).

```powershell
git clone https://github.com/inrwxyz/locker-pro.git
cd locker-pro\android
.\gradlew.bat assembleDebug          # APK: app\build\outputs\apk\debug\app-debug.apk
.\gradlew.bat installDebug           # build and install on a USB-debugging phone
```

`local.properties` (your SDK path) is created by Android Studio, or create it yourself with `sdk.dir=<path to Android SDK>`. It is git-ignored.

## Known limits

- The phone and the PC must be on the same network for fingerprint unlock. Otherwise use the Master Password on the PC.
- Fingerprint unlock needs a strong (Class 3) biometric enrolled on the phone. Devices without one cannot link.
- Changing the enrolled fingerprints on the phone invalidates the key on purpose.

## Reporting problems

Open a GitHub issue. For security problems please use GitHub's private vulnerability reporting (Security tab).

## License

[MIT](../LICENSE). Uses AndroidX, Material Components and [ZXing Android Embedded](https://github.com/journeyapps/zxing-android-embedded) (Apache-2.0).
