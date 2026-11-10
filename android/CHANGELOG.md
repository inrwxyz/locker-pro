# Changelog

## 2.0.0

- Updated companion UI theme, typography (`Inter`), and launcher icons.
- Added Android Keystore (`AES/GCM/NoPadding`) local secret sealing, `android:allowBackup="false"`, and purged plaintext secrets from WebView `localStorage`.
- Fixed post-QR scan UI state transition so paired status displays immediately, plus two-way PC unpair synchronization (both live push and startup ping sync).
- Promoted release version out of beta (`2.0.0`).

## 0.9.0-beta

- Fingerprint approval is bound to a hardware Keystore key (`BIOMETRIC_STRONG`, per-use, invalidated when a new fingerprint is added). PIN/pattern can no longer approve an unlock.
- The key is created after you confirm your fingerprint during linking, and deleted on Disconnect.
- Simplified main screen: only the connected computer card.
- New launcher icon (adaptive, themed and legacy variants).
- Local protocol additions: the unlock key travels encrypted with a key derived from the pairing secret.
