# Changelog

## 0.9.0-beta

- Fingerprint unlock rebuilt: the master key is no longer stored on the PC. The PC keeps only a blob that the phone's Keystore key (live fingerprint required) can open. The old `biometric.vault` is removed automatically.
- Fingerprint is linked right after pairing and again after a Master Password entry when the phone's fingerprints changed.
- Installer adds a Windows Firewall rule for the phone link (TCP 58820) and the uninstaller removes it.
- Installer no longer grants the Users group access to the Locker config folder.
- New app and installer icon.
- `build_package.ps1` no longer uses machine specific paths; output goes to `release\`.
- Vault, password policy, lockout, app/folder guard and installer changes from earlier internal versions (see git history).
