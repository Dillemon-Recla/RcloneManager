# Security (Windows Credential Manager), Remotes (3 Modes), Wizard & Portable Export

## Context

To let the desktop UI act as a complete bridge between the user and Rclone without requiring terminal commands or leaking credentials/keys across machines, four integrated pillars were implemented:

1. **AES-256-GCM Vault backed by Windows Credential Manager** (`WindowsCredentialVault` + `SensitiveDataProtector`).
2. **Visual Remotes Manager (`rclone.conf`) with 3 authentication modes** (OAuth Link, Manual Token JSON, and Key/Credentials).
3. **Portable export without credentials or master key** (`ExportedConfigBundle`).
4. **First-Run Wizard (`FirstRunWizardWindow`) and Granular Reset (`ResetConfigWindow`)**.

---

## Changes Implemented

### 1. AES-256-GCM Vault (`src/RcloneManager.Core/Security/`)
- **`WindowsCredentialVault.cs`**:
  - Uses native P/Invoke on `advapi32.dll` (`CredReadW`, `CredWriteW`, `CredDeleteW`, `CredFree`) with `CRED_TYPE_GENERIC` and `CRED_PERSIST_LOCAL_MACHINE`.
  - On startup, `GetOrCreateMasterKey` automatically generates a 32-byte key (`RandomNumberGenerator.GetBytes(32)`) under `RcloneManager:MasterKey` if none exists.
  - `RegenerateMasterKey` and `DeleteMasterKey` support key rotation and deletion.
- **`SensitiveDataProtector.cs`**:
  - Authenticated encryption with **AES-256-GCM** (`new AesGcm(key, 16)`) using a random 12-byte nonce and 16-byte tag, stored with prefix `enc:v1:`.
- **`AppConfigStore.cs`**:
  - When saving `rclone_manager_config.json`, encrypts `RcloneConfigPath`, `ProtectedSecrets`, and `job.ExtraFlags` at rest, decrypting transparently in memory upon load.

### 2. Visual Remotes Manager (`rclone.conf`) & 3 Authentication Modes
- **Models (`Models/RcloneRemoteModels.cs`)**: `RemoteAuthMode` (`OAuthBrowserLink`, `ManualTokenJson`, `KeyOrCredentials`), `RcloneProviderInfo`, `RcloneRemoteDetail` (masks secrets as `"••••••••"`).
- **Hybrid Engine (`RcloneHybridEngine.cs` + `RcloneRcClient.cs`)**:
  - Dynamically loads all **71 providers** via `/config/providers` (with `rclone config providers` fallback).
  - **Via Link (`OAuthBrowserLink`)**: opens browser login automatically via `rclone config create` / `rclone config reconnect`; for Google Drive (`drive`), shows contextual guidance on configuring your own `client_id` + `client_secret`.
  - **Via Manual Token JSON (`ManualTokenJson`)**: validates JSON syntax before saving and includes a **Generate Token via `rclone authorize`** helper button.
  - **Via Key / Credentials (`KeyOrCredentials`)**: saves via RC `/config/create` or `/config/update` with `--obscure` without launching a browser; file options (`service_account_file`, `key_file`, certificates) automatically display a **Browse...** picker in the **Remotes (`rclone.conf`)** tab.

### 3. Portable Export + Wizard-Guided Import
- **`AppConfigStore.ExportPortableConfig`**:
  - Exports a `.json` file (`Format = "RcloneManager.PortableConfig"`) containing the pinned release, ports/settings, sanitized jobs (`SanitizeExtraFlags` strips flags containing `pass`, `secret`, `token`, `key`, `auth`, `header`), and `ExportedRemoteBlueprint` (only remote name, provider type, suggested auth mode, and non-sensitive options like `scope`, `host`, `port`, `endpoint`, `region`, `url`, `provider`).
  - **Never exports** `RcloneManager:MasterKey`, `ProtectedSecrets`, `RcloneConfigPath`, OAuth tokens, or passwords.
- **`FirstRunWizardWindow.xaml` / `.cs`** (4 steps):
  1. **Optional Import + New Key**: imports a portable `.json` bundle, generates/rotates a fresh AES-256 key in Windows Credential Manager, and previews jobs and required services.
  2. **Pinned Release**: configures and downloads/validates the locked Rclone release (SHA-256).
  3. **Authenticate Target Services**: lists imported remotes as `Pending authentication`, pre-fills name/provider/public options, and guides authentication + connection testing one by one.
  4. **Task Review**: lets you inspect and tweak job names, operations, source, destination, intervals, and enabled flags before saving and reloading the service (`POST /api/service/reload`).
- **`ResetConfigWindow.xaml` / `.cs`** (`POST /api/service/reset`):
  - Resets `rclone_manager_config.json` with granular toggles for clearing logs/runtime, deleting `RcloneManager:MasterKey` from Windows Credential Manager, and (with extra confirmation + `.bak` backup) deleting `rclone.conf`.

---

## Validation

1. **Full `.NET 10` Build (`Release`)**:
   ```powershell
   dotnet build c:\rclone\RcloneManager.sln -c Release
   ```
   Observed output: `Build succeeded. 0 Warning(s), 0 Error(s)`.
2. **Automated Vault & Portable Export tests (`.NET 10`)**:
   - `WindowsCredentialVault` + `SensitiveDataProtector`: `KeyCreated=True`, `Exists=True`, `PrefixOk=True`, `TamperDetected=True`, `KeyRotated=True`, `AfterDeleteExists=False`.
   - `AppConfigStore.ExportPortableConfig` + `ReadPortableBundle`: `NoSecretsLeaked=True` (confirmed tokens, `client_secret`, `ProtectedSecrets`, `--sftp-pass`, `enc:v1:`, `MasterKey`, and `rcloneConfigPath` are excluded from the exported JSON), `HasBlueprintAndSafeFlags=True`, `BundleLoadedOk=True`.
3. **E2E Service API tests (`RcloneManager.Service.exe`)**:
   - `GET /api/status`: `encryptionKeyActive: true`, `encryptionKeyTarget: "RcloneManager:MasterKey"`, `rcDaemonRunning: true`.
   - `GET /api/remotes/providers`: `71` providers via `RC API (port 5572)`.
   - `POST /api/remotes/save`: `InvalidManualTokenRejected=True` for malformed JSON in `ManualTokenJson` mode, plus clean create/test/delete via `KeyOrCredentials` and RC API.
   - `POST /api/service/reload`: `Reloaded: True`.

---

## Notes / Things to Watch

- `rclone.conf` continues to be managed natively by `rclone.exe` (which obscures passwords via `--obscure` and handles OAuth token refreshes). In the UI, existing secrets returned by `/config/dump` are masked as `"••••••••"` and are left untouched if kept blank during edits.
- When importing an exported config on another PC, re-authenticating the remotes listed in Wizard Step 3 is both expected and required, since zero secrets leave the source machine.
