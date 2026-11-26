# Architecture and Hybrid Engine (RC API + CLI)

## Context

`RcloneManager` is designed to separate the **visual user interface** from the **execution and scheduling engine**, ensuring sync and backup jobs keep running in the background even after the main window is closed. In addition, control over `rclone.exe` follows a **hybrid** approach, choosing between the Remote Control HTTP API (`rcd` / `--rc`) and the command-line interface (`CLI`) depending on each operation's needs.

---

## Project Structure

### 1. `src/RcloneManager.Core` (`net10.0`)
Shared library with zero external NuGet dependencies:
- `Models/SyncJob.cs`: job definition (`Sync`, `Copy`, `Move`, `Check`), flags (`SkipLinks`, `FastList`, `CopyLinks`, `DryRun`, `MaxSize`, `BandwidthLimit`, `ExtraFlags`), strategy (`ExecutionStrategy`), and schedule (`JobSchedule`).
- `Models/RcloneStats.cs`: strongly-typed DTOs to deserialize Rclone's `/core/stats`, `/core/bwlimit`, and `/operations/about`.
- `Models/ServiceStatusDto.cs`: global config (`AppManagerConfig`) and complete status payload (`ServiceStatusDto`).
- `Engine/RcloneHybridEngine.cs`: hybrid orchestrator (persistent `rclone rcd` daemon, RC-monitored CLI execution, and pure CLI fallback).
- `Engine/RcloneRcClient.cs`: HTTP client for RC endpoints (`/core/pid`, `/core/version`, `/config/listremotes`, `/core/stats`, `/core/bwlimit`, `/operations/about`, `/sync/*`, `/job/*`, `/core/quit`).
- `Engine/RcloneCliRunner.cs`: safe `ProcessStartInfo` builder using `ArgumentList` (no console window and no quote-escaping issues).
- `Engine/RcloneReleaseManager.cs`: verification, download, SHA-256 validation, and version lock for `rclone.exe`.
- `Engine/FilterFileHelper.cs`: analyzer and sanitizer for `--filter-from` / `--exclude-from` files.
- `Scheduling/JobScheduler.cs`: manual execution queue, `NextRunAt` calculation, retry management, and persistence via `AppConfigStore`.
- `Storage/WorkspacePathResolver.cs` & `Storage/AppConfigStore.cs`: root/binary discovery and atomic read/write (`.tmp` + `File.Move`) of `rclone_manager_config.json`.

### 2. `src/RcloneManager.Service` (`net10.0`)
ASP.NET Core host running `RcloneBackgroundWorker` (`BackgroundService`):
- Drives `JobScheduler` on a `1`-second tick and manages the `rclone rcd` lifecycle.
- Exposes a local HTTP API exclusively on `127.0.0.1:5580`.
- Includes `WindowsServiceHost.cs` (native SCM dispatcher via P/Invoke in `advapi32.dll`) to run as a Windows Service without extra NuGet packages.

### 3. `src/RcloneManager.UI` (`net10.0-windows`)
WPF application (Dark UI) with `NotifyIcon` (`TrayIconHelper`):
- Provisions `rclone.exe` before connecting to the service (`RcloneSetupWindow`).
- Automatically launches `RcloneManager.Service.exe --background` via `ServiceLauncher` if port `5580` is not responding.
- Polls `GET /api/status` every `1500 ms` to update live KPIs, transfer speed, ETA, and active file transfers.

---

## How the Hybrid Engine Decides (`RcloneHybridEngine.ResolveStrategy`)

When a job uses `ExecutionStrategy.HybridAuto`:

1. **Evaluation for `CliWithRcMonitoring`**:
   - Automatically selected if the job uses `--filter-from` / `--exclude-from`, `--max-size`, `--log-file`, `ExtraFlags`, `--skip-links`, `--fast-list`, or `--copy-links`.
   - Starts `rclone.exe` as a dedicated CLI process with full flags **plus** `--rc --rc-addr=127.0.0.1:<freePort> --rc-no-auth --stats=1s`.
   - During execution, `RcloneRcClient` polls `/core/stats` every second and allows live `--bwlimit` updates via `/core/bwlimit`.
2. **Evaluation for `RcDaemonApi`**:
   - Selected for simple operations (without CLI-only flags) when the persistent `rclone rcd` daemon (`127.0.0.1:5572`) is running.
   - Triggers `/sync/sync`, `/sync/copy`, `/sync/move`, or `/operations/check` with `_async = true` and tracks progress via `/job/status` and `/core/stats`.
   - If the daemon call fails (unless cancelled by the user), it applies an **automatic fallback** to `CliWithRcMonitoring`.
3. **Hybrid queries (`ListRemotesHybridAsync`, `GetRemoteAboutAsync`, `GetRcloneVersionAsync`)**:
   - Try the `rclone rcd` HTTP daemon first (instant response) and fall back to CLI (`rclone listremotes`, `rclone about --json`, `rclone version`) if the daemon is off or fails.

---

## Scheduler & Directory Resolution

- **`JobScheduler`**:
  - Uses `SemaphoreSlim(1, 1)` to prevent concurrent sync executions from competing for disk and bandwidth.
  - Computes `NextRunAt` for `IntervalMinutes`, `DailyAtTime`, `WeeklyDays`, and triggers `OnServiceStart` once on service startup.
  - When scheduled runs fail (`RetryOnFailure = true`), schedules another attempt after `RetryDelayMinutes` up to `MaxRetries`.
- **`WorkspacePathResolver.ResolveRootDirectory`**:
  - Prioritizes `--workdir` and `RCLONE_MANAGER_HOME`; next walks up to 6 parent levels from `AppContext.BaseDirectory` looking for `rclone_manager_config.json` or `RcloneManager.sln`, and finally scans for `rclone.exe` directly or inside `rclone-*` folders.

---

## Validation

Full `.NET 10` build (`Release`):
```powershell
dotnet build c:\rclone\RcloneManager.sln -c Release
```
Observed output: `Build succeeded. 0 Warning(s), 0 Error(s)` producing `RcloneManager.Core.dll`, `RcloneManager.Service.dll` (`net10.0`), and `RcloneManager.UI.dll` (`net10.0-windows`).

Local integration test (executed in temporary `%TEMP%` workspace via service API):
- Created temporary `Copy` job (`HybridAuto`), triggered via `POST /api/jobs/{id}/run`, automatically sanitized the filter file, and completed with `lastStatus: "Success"`.

---

## Notes / Things to Watch

- Only one transfer job executes at a time (`_executionLock` in `JobScheduler` and `_activeExecution` in `RcloneHybridEngine`); additional manual requests are queued in `_manualQueue` and executed sequentially.
- Stopping a running Rclone process first attempts `/core/quit` over RC and only resorts to `Process.Kill(entireProcessTree: true)` if it does not exit within 2 seconds.
