# Operations, Windows Service, and Troubleshooting

## Context

This guide covers building the solution, background execution modes on Windows, configuration file layout, and runtime log files.

---

## Building & Launching

### Build via CLI (.NET 10 SDK)
```powershell
cd c:\rclone
dotnet build RcloneManager.sln -c Release
```
Generated outputs:
- Service: `src\RcloneManager.Service\bin\Release\net10.0\RcloneManager.Service.exe`
- Desktop UI: `src\RcloneManager.UI\bin\Release\net10.0-windows\RcloneManager.UI.exe`

### Quick Start
Run `Iniciar-RcloneManager.bat` at the repository root. The script builds `Release` automatically if binaries do not exist yet and launches `RcloneManager.UI.exe`.

---

## Background Execution Modes (Wizard Step 4 Selector)

In Step 4 of the **First-Run Wizard** (as well as the **Logs & Service** tab), users explicitly choose among three clearly explained modes:

### 1. Silent User-Session Mode (`UserStartup` / `--background`) — Recommended Default
- Launched automatically by the UI when `http://127.0.0.1:5580` does not respond and registered under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`RcloneManagerService`).
- **Advantage**: requires no Administrator elevation (UAC) and runs inside your user session with direct access to your profile and `%AppData%\rclone\rclone.conf`.

### 2. Native Windows Service (`WindowsService` / `--windows-service`)
- Selectable directly in **Wizard Step 4** or via **Install WinService** / **Remove WinService** in **Logs & Service** (prompts UAC once).
- The installer frees port `5580` from any local session process, creates/updates the service with `start= auto` + auto-recovery (`sc failure`), and immediately starts `RcloneManagerService` (`sc start`).
- **`LocalSystem` compatibility**: `AppConfigStore` persists `rcloneConfigPath` explicitly and `WindowsCredentialMasterKeyStore` maintains a machine-scoped DPAPI mirror in `%ProgramData%\RcloneManager\vault_*.dpapi`, allowing `LocalSystem` to decrypt `rclone_manager_config.json` cleanly.

### 3. Manual Only (`ManualOnly`)
- Runs the background service only when the application is opened manually, with no automatic Windows startup entry.

---

## Desktop Menu Bar (`File` and `Help`)

- **File**: `New Task` (`Ctrl+N`), `Save Current Tab Changes` (`Ctrl+S`), quick shortcuts to open the workspace / `rclone_manager_config.json` / logs folder, **Export Config** (portable without secrets), **Import Config** (via Wizard), **Run First-Run Wizard**, **Reset / Delete Configs**, minimize to tray, and exit.
- **Help**: open `docs/`, GitHub repository, official Rclone docs, **Verify Pinned Release**, **Copy Diagnostics Info**, and **About** window (running component versions, MIT license, and stack details).

---

## System Tray (`TrayIconHelper`)

When closing (`X`) the main window, you can choose:
- **Yes**: minimize to the System Tray (hides UI while keeping the service running).
- **No**: close only the UI (the background service and scheduler stay active).
- **Cancel**: stay in the window.

The tray context menu lets you restore the dashboard, run the primary backup, stop the active job, or shut down **UI + Service**.

---

## Local Config & Log Files

- **`rclone_manager_config.json`**:
  - Created on first run in the workspace root (auto-imports `script.bat` and `exclude_user_folder.txt` if present).
  - Stores `rcloneExecutablePath`, `rcloneConfigPath`, `workingDirectory`, `pinnedRcloneVersion` (`v1.76.0`), `lockRcloneVersion` (`true`), `firstRunCompleted`, `encryptionEnabled`, `encryptionKeyTarget` (`RcloneManager:MasterKey`), `protectedSecrets`, ports (`5580` and `5572`), `globalBandwidthLimit`, and `jobs`.
  - Sensitive fields (`rcloneConfigPath`, `protectedSecrets`, and job `extraFlags`) are encrypted on disk with **AES-256-GCM** (`enc:v1:...`) using the key stored in Windows Credential Manager.
- **`rclone_manager_service.log`**: logs service lifecycle events, hybrid strategy decisions, filter sanitization notices, and scheduler errors.
- **`rclone_backup.log`**: standard transfer log written by `rclone.exe` (`--log-file`).

---

## Common Troubleshooting

- **Error `lookup www.googleapis.com: no such host`**: indicates a temporary network/DNS outage. Enable `RetryOnFailure` in the scheduler (default: 2 retries with a 5-minute delay) and keep `--retries 3 --low-level-retries 5` in `ExtraFlags`.
- **Failure installing Windows Service**: registration invokes `sc.exe` and requires Administrator elevation (UAC). If UAC is declined, use per-user startup (`HKCU\...\Run`).
- **Execution blocked due to mismatched version**: if the release lock is enabled and a different `rclone.exe` binary was placed manually in the folder, click **Download / Lock Release** under **Logs & Service** to restore the pinned version, or uncheck the version lock if you intend to use another binary.
