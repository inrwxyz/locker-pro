# Changelog

## 2.1.1

- Added short wake-lock acquisition on incoming unlock prompts and clean cancellation (`auth_cancel`) syncing so missed or PC-canceled notifications clear immediately and rapid retries always alert cleanly.
- Fixed `AuthPromptActivity` `onNewIntent` nonce reset so fresh prompt requests are never silently ignored while a previous instance is closing.
- Added active prompt recovery on app resume (`onResume` ping sync + signed `reprompt` handshake): opening Locker Companion after dismissing or missing a heads-up prompt immediately surfaces the pending PC unlock banner for fingerprint approval.

## 2.1.0

- Restored original minimal and clean logo/icon branding across all mipmaps and web assets.
- Fixed Android 14/15/16 background crash by safely wrapping `startForeground(...)` in try-catch to prevent `ForegroundServiceStartNotAllowedException`.
- Migrated foreground service type from `dataSync` to `connectedDevice` (`FOREGROUND_SERVICE_CONNECTED_DEVICE`).
- Added dynamic Phone Link-style connection presence: "Connected to Locker PC" notification only appears when PC is active, and automatically dismisses when PC shuts down or disconnects.
- Added battery optimization ignore flow (`REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`) to maintain reliable local Wi-Fi socket listening in Doze mode.

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
