# Disposable-Key Security, SOLID Architecture, Remotes (3 Modes) & Wizard

## Context

Originally, Step 1 of `FirstRunWizardWindow` exposed a technical AES-256 key card (`Target: RcloneManager:MasterKey`) with a *"Generate New Key"* button, and `ResetConfigWindow` showed a manual checkbox to delete the key from Windows Credential Manager. In addition, `AppConfigStore` combined multiple responsibilities (JSON persistence, field encryption, default config creation, portable export/import, and destructive reset), while `FirstRunWizardWindow.xaml.cs` and `MainWindow.xaml.cs` coupled directly to static cryptographic helpers and engine internals.

Beyond UX noise, rotating the key as soon as the Wizard opened (`Loaded`) risked orphaning existing `enc:v1:...` fields on disk if the user closed the dialog without saving.

---

## Changes Implemented

### 1. Decoupled Security Module & Disposable Key (`src/RcloneManager.Core/Security/`)
- **`IMasterKeyStore.cs` & `WindowsCredentialVault.cs` (`WindowsCredentialMasterKeyStore`)**:
  - 32-byte key-store abstraction backed by native Win32 `advapi32.dll` (`CredReadW`, `CredWriteW`, `CredDeleteW`, `CredFree`) plus an `InMemoryMasterKeyStore` implementation for OS-independent automated tests.
- **`ISensitiveDataProtector.cs` & `SensitiveDataProtector.cs` (`AesGcmDataProtector`)**:
  - Injectable **AES-256-GCM** (`enc:v1:`) authenticated encryption service while preserving backwards-compatible static facades.
- **`Models/ConfigCloningExtensions.cs`**:
  - Centralizes `.Clone()` for `JobSchedule`, `SyncJob`, and `AppManagerConfig`, removing duplicated cloning code across scheduler, storage, and exporter.

### 2. SOLID Storage Decomposition (`src/RcloneManager.Core/Storage/`)
- **`IAppConfigServices.cs`**: defines segregated contracts (`IConfigCryptoTransformer`, `IDefaultConfigFactory`, `IPortableConfigService`, `IConfigResetService`, and `IAppConfigRepository`).
- **`ConfigCryptoTransformer.cs`**:
  - Detects sensitive fields (`HasSensitiveData`), initializes the key silently on demand, and performs **atomic disposable-key rotation** (`ProtectForStorage(config, rotateDisposableKey: true)`) at `Save()` time so cancelling intermediate dialogs never orphans ciphertext on disk.
- **`DefaultConfigFactory.cs`**: isolates default configuration creation, workspace path resolution, and `rclone.conf` detection.
- **`PortableConfigService.cs`**: isolates `ExportedConfigBundle` export/import, CLI flag sanitization (`pass`, `secret`, `token`, `key`, `auth`, `header`), and `ExportedRemoteBlueprint` inference, guaranteeing zero secrets or keys leave the machine.
- **`ConfigResetService.cs`**: isolates workspace reset and automatically discards/regenerates the disposable key by default (`ResetConfigRequest.DeleteCredentialKey = true`).
- **`AppConfigStore.cs`**: streamlined into a focused atomic JSON repository (`rclone_manager_config.json`) composing the services above.

### 3. Wizard Decoupling, Remote Builder & Dependency Injection (`Onboarding`, `Service`, and `UI`)
- **`Onboarding/RemoteAuthRequestBuilder.cs`**: pure domain builder mapping `WizardRemoteAuthInput` + `ExportedRemoteBlueprint` into `CreateOrUpdateRemoteRequest` (including provider-specific parameter names for `s3`, `drive`, and `webdav`).
- **`UI/Services/WizardOnboardingService.cs` (`IWizardOnboardingService`)**:
  - Orchestrates onboarding state, in-memory bundle import, release verification, remote listing/authentication (preferring `ServiceApiClient` with local `RcloneHybridEngine` fallback), and completion via `Save(CurrentConfig, rotateDisposableKey: true)`.
- **`FirstRunWizardWindow.xaml` / `.cs` & `ResetConfigWindow.xaml` / `.cs`**:
  - Removed the AES-256 key card and *"Generate New Key"* button from Wizard Step 1 (now **"1. Start & Import"**) and removed the technical `MasterKey` checkbox from Reset.
  - Eliminated direct UI coupling (`MainWindow` and `FirstRunWizardWindow`) to `RcloneManager.Core.Security`.
- **`Service/Program.cs` & `Scheduling/JobScheduler.cs`**:
  - Registered all segregated interfaces in ASP.NET Core DI (`IMasterKeyStore`, `ISensitiveDataProtector`, `IConfigCryptoTransformer`, `IDefaultConfigFactory`, `IPortableConfigService`, `IAppConfigRepository`, `IConfigResetService`).

---

## Validation

1. **Full `.NET 10` Build (`Release`)**:
   ```powershell
   dotnet build c:\rclone\RcloneManager.sln -c Release
   ```
   Observed output: `Compilação com êxito. 0 Aviso(s), 0 Erro(s)`.
2. **Automated decoupled module verification (`InMemoryMasterKeyStore` + `AesGcmDataProtector` + `AppConfigStore` + `WizardOnboardingService` + `RemoteAuthRequestBuilder`)**:
   - Automatic key creation on persisting sensitive data and `enc:v1:` at-rest encryption.
   - Wizard cancel safety: opening `WizardOnboardingService` and importing a bundle in memory does **not** rotate the key or invalidate `rclone_manager_config.json` before `CompleteAsync`.
   - Atomic disposable-key rotation on `CompleteAsync` and automatic discard/regeneration on `ResetConfig()`, preserving clean decryption with the rotated key.
   - Portable export (`PortableConfigService.ExportPortableBundle`) verified free of `ProtectedSecrets`, CLI password flags, and `enc:v1:` ciphertext.
   - `RemoteAuthRequestBuilder.Build` verified for `s3` (`endpoint`, `access_key_id`, `secret_access_key` + public blueprint options).
   - Observed output: `ALL_SOLID_TESTS_PASSED=True`.

---

## Notes / Things to Watch

- `rclone.conf` remains natively managed by `rclone.exe` (`--obscure` and OAuth token refresh), while `rclone_manager_config.json` protects `RcloneConfigPath`, `ProtectedSecrets`, and `ExtraFlags` using the local disposable key.
- If `RcloneManager:MasterKey` is deleted externally in Windows, `ConfigCryptoTransformer.UnprotectInPlace` clears only unreadable `enc:v1:` fields without breaking service or UI startup, generating a fresh key automatically on the next save.
