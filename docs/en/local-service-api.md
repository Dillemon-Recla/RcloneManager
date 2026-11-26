# Local HTTP API and Service Modes

## Context

`RcloneManager.Service` acts as a background control and scheduling daemon decoupled from the desktop UI. It listens locally via Kestrel on **`http://127.0.0.1:5580`** (configurable via `serviceApiPort` in `rclone_manager_config.json`), using `camelCase` JSON serialization and string enums (`JsonStringEnumConverter`).

---

## Local API Endpoints (`src/RcloneManager.Service/Program.cs`)

### Status & Monitoring
| Method | Route | Description |
| :--- | :--- | :--- |
| `GET` | `/api/status` | Returns full `ServiceStatusDto`: service state, Rclone version, version lock status (`pinnedRcloneVersion`, `lockRcloneVersion`, `isRcloneVersionValid`), vault/wizard (`firstRunCompleted`, `encryptionKeyActive`, `encryptionKeyTarget`), `rcd` daemon state, current bandwidth limit, active job, live `/core/stats`, job list, and recent logs. |
| `GET` | `/api/logs?maxLines=200` | Returns the last `10`–`2000` lines of `serviceLogs` (in-memory ring buffer) and `rcloneLogLines` (non-blocking read with `FileShare.ReadWrite \| FileShare.Delete`). |

### Job Management
| Method | Route | Description |
| :--- | :--- | :--- |
| `GET` | `/api/jobs` | Lists all configured jobs. |
| `POST` | `/api/jobs` | Creates or updates a `SyncJob` (requires `sourcePath` and `destinationPath`) and recalculates `nextRunAt`. |
| `DELETE` | `/api/jobs/{id}` | Deletes a job by `id`. |
| `POST` | `/api/jobs/{id}/run?dryRun=false` | Enqueues a manual execution; accepts optional `RunJobRequest` body (`dryRun`, `overrideStrategy`). |
| `POST` | `/api/jobs/{id}/stop` | Requests cancellation of the currently running job. |

### Hybrid RC Control, Remotes, Bandwidth & Release
| Method | Route | Description |
| :--- | :--- | :--- |
| `POST` | `/api/bwlimit` | Accepts `{ "rate": "10M" }` (or `"off"`), persists to `globalBandwidthLimit`, and applies live to both the active job and `rcd` via `/core/bwlimit`. |
| `POST` | `/api/rcd/toggle` | Toggles the persistent `rclone rcd` daemon (port `5572`) and updates `autoStartRcdDaemon`. |
| `GET` | `/api/remotes` | Lists configured remotes via RC API (if `rcd` is running) with fallback to `rclone listremotes`. |
| `GET` | `/api/remotes/details` | Lists remotes with provider type, token status (`hasToken`), and masked parameters (`••••••••`) via `/config/dump`. |
| `GET` | `/api/remotes/providers` | Lists supported Rclone providers and option metadata (`/config/providers` with cache). |
| `POST` | `/api/remotes/save` | Creates or updates a remote (`CreateOrUpdateRemoteRequest`) across 3 modes (`OAuthBrowserLink`, `ManualTokenJson`, `KeyOrCredentials`). |
| `POST` | `/api/remotes/{name}/reconnect` | Runs `rclone config reconnect <name>:` to renew OAuth via browser. |
| `POST` | `/api/remotes/{name}/test` | Tests the remote with `rclone lsd <name>: --max-depth 1` and queries `/operations/about`. |
| `DELETE` | `/api/remotes/{name}` | Removes a remote from `rclone.conf` via `/config/delete` (or CLI). |
| `GET` | `/api/about?remote=gdrive:` | Queries total, used, free, and trashed quota space for the target remote. |
| `POST` | `/api/rclone/stop-rcd` | Stops `rclone rcd` to release the `rclone.exe` file lock before switching/updating releases. |
| `POST` | `/api/rclone/release-config` | Accepts `UpdateReleaseConfigRequest`, validates the version, updates lock/path in `JobScheduler`, and restarts `rclone rcd` if enabled. |

### Filters & Service Lifecycle
| Method | Route | Description |
| :--- | :--- | :--- |
| `GET` | `/api/filters/analyze?path=...` | Analyzes a filter file (defaults to `exclude_user_folder.txt`) and returns `FilterAnalysisResult` + current content. |
| `POST` | `/api/filters/save` | Saves edited content from `FilterFilePayload` and returns updated analysis. |
| `POST` | `/api/filters/optimize` | Creates a `.bak` backup, reorders, and fixes rules on disk via `FilterFileHelper.ApplyOptimizationWithBackup`. |
| `POST` | `/api/service/startup?enabled=true\|false` | Creates or removes the `RcloneManagerService` entry in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. |
| `POST` | `/api/service/complete-first-run` | Marks `firstRunCompleted = true` in persisted configuration. |
| `POST` | `/api/service/reload` | Stops `rcd` if running, reloads `JobScheduler` from disk (`rclone_manager_config.json`), and restarts `rcd`. |
| `POST` | `/api/service/reset` | Accepts `ResetConfigRequest` (`deleteCredentialKey`, `deleteLogsAndRuntime`, `deleteRcloneConf`), clears selected items, and reinitializes config. |
| `POST` | `/api/service/shutdown` | Gracefully stops the service (`IHostApplicationLifetime.StopApplication()`). |

---

## Command-Line Arguments (`RcloneManager.Service.exe`)

- `--workdir <path>`: explicitly sets the root directory where `rclone_manager_config.json` and logs reside.
- `--background`: suppresses console log providers for silent background execution.
- `--windows-service`: enables `WindowsServiceHost` (native `StartServiceCtrlDispatcherW` / `RegisterServiceCtrlHandlerExW` / `SetServiceStatus` dispatcher in `advapi32.dll`).
- `--install-service`: executes `sc.exe create RcloneManagerService ... start= auto` pointing to `--windows-service --workdir "<root>"`.
- `--uninstall-service`: runs `sc.exe stop` and `sc.exe delete RcloneManagerService`.

---

## Validation

Smoke test executed after `.NET 10` migration:
```powershell
Start-Process "c:\rclone\src\RcloneManager.Service\bin\Release\net10.0\RcloneManager.Service.exe" -ArgumentList "--background" -WindowStyle Hidden
Invoke-RestMethod -Uri "http://127.0.0.1:5580/api/status"
```
Observed output:
- `serviceRunning: true`
- `rcloneVersion: "v1.76.0"`
- `pinnedRcloneVersion: "v1.76.0"`
- `lockRcloneVersion: true`
- `isRcloneVersionValid: true`
- `rcDaemonRunning: true` (`rcDaemonPort: 5572`)

---

## Notes / Things to Watch

- **Network surface**: the API binds strictly to `127.0.0.1` (local loopback) with no HTTP authentication; it must not be exposed on external interfaces without adding authentication and TLS.
- **Administrative privileges**: `--install-service` and `--uninstall-service` invoke `sc.exe` and require UAC elevation (`runas`).
