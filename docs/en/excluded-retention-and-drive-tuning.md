# 30-Day Excluded Grace Period, Instant Purge & Google Drive Tuning

## Context & Root Cause

Real-world user profile backups (`C:\Users\<user> -> gdrive:BackupPC/`) surfaced three critical behaviors:

1. **Severe throughput drops (`~100–823 B/s`) and `HTTP 403 RATE_LIMIT_EXCEEDED`**:
   - Google Drive remotes authenticated without a custom `client_id` share Rclone's global OAuth project (`project_number:202264815644`). Unthrottled request bursts trigger `403 Quota exceeded` and exponential backoff, compounded when uploading tens of thousands of tiny (~500-byte) game/cache files.
2. **Root dot-folder pitfall (`- **/.*/**`)**:
   - In Rclone filter syntax, `- **/.*/**` requires a slash before the dot and misses hidden folders at the source root (such as `.ravion/`, `.gemini/`, `.claude/`), requiring `- .*/**` and `- .*`.
3. **Orphaned cloud files after adding new exclusion rules**:
   - Running `sync` without `--delete-excluded` leaves previously uploaded folders on the remote forever; enabling `--delete-excluded` directly on `sync` would immediately wipe newly excluded folders with zero recovery window if a filter rule was added by mistake.

---

## Changes Implemented

### 1. 30-Day Grace Tracking & On-Demand Purge (`RcloneManager.Core` & `RcloneManager.Service`)
- **`Engine/FilterFileHelper.cs`**:
  - **`BuildOptimizedContent(...)`**: automatically expands `- **/.*/**` and `- **/.*` to include `- .*/**` and `- .*` so root hidden folders/files are always covered.
  - **`BuildInvertedExcludedOnlyFilterContent(string filterFilePath, FilterRuleMode mode)`**: builds the exact boolean complement (*First-Match-Wins*) of any `--filter-from` or `--exclude-from` file, matching only excluded destination files while protecting (`- ...`) every included backup file.
- **`Engine/ExcludedRetentionManager.cs`**:
  - Locally tracks `FirstExcludedUtc` in `.rclone-manager-runtime\excluded_retention.json` for destination files matching exclusion rules (`rclone lsf --files-only -R --fast-list --filter-from <inverted>`), **without moving or duplicating cloud objects** (zero extra storage or move quota).
  - Once `SyncJob.ExcludedRetentionDays` elapses (default `30` days with `EnableExcludedGraceRetention = true`), deletes only expired paths via `rclone delete --files-from-raw` + best-effort empty directory cleanup (`rclone rmdirs --leave-root`).
  - **`PurgeAllExcludedNowAsync(...)`**: immediately purges all excluded items on the destination and clears the job's grace record.
- **`Scheduling/JobScheduler.cs` & `Service/Program.cs`**:
  - Automatically evaluates retention after successful `Sync` / `Copy` runs (at most once every 12 hours per job).
  - Added local endpoints: `GET /api/jobs/{id}/excluded-retention?scan=false|true` and `POST /api/jobs/{id}/purge-excluded`.

### 2. Anti-Throttling Google Drive Defaults (`DefaultConfigFactory` & `FirstRunWizardWindow`)
- Default and Wizard-created Google Drive jobs now include `--tpslimit 6 --tpslimit-burst 6 --drive-chunk-size 64M --retries 3 --low-level-retries 5`, smoothing HTTP request pacing (preventing `403 RATE_LIMIT_EXCEEDED` even without a custom `client_id`) and reducing round-trips on larger files.

### 3. Clear Wizard Service Selector & Full `WinService` Support (`UI`, `Service`, and `Security`)
- **`FirstRunWizardWindow.xaml` / `.cs` & `WizardOnboardingService.cs`**:
  - Step 4 now presents an explicit background mode selector with dynamic plain-language guidance:
    1. `UserStartup` (*Start with my Windows user — Recommended, no Admin required*);
    2. `WindowsService` (*Install Native Windows Service — Server / pre-logon, prompts UAC Admin once*);
    3. `ManualOnly` (*Manual only — runs only while the app is opened*).
- **`ServiceControlHelper.cs` & `ServiceLauncher.cs`**:
  - `InstallAndStartWindowsService(...)` stops running instances, updates (`sc config`) or creates (`sc create`) the service, configures failure recovery (`sc failure`), and starts `RcloneManagerService` (`sc start`) after releasing port `5580`, falling back gracefully if UAC is declined.
- **`Security/WindowsCredentialVault.cs`**:
  - Mirrors the disposable key using machine-scoped DPAPI (`CryptProtectData` with `CRYPTPROTECT_LOCAL_MACHINE` under `%ProgramData%\RcloneManager\vault_*.dpapi`) so `WinService` running under `LocalSystem` can decrypt `rclone_manager_config.json` seamlessly.

---

## Validation

Full solution build and automated `.NET 10` integration test against `rclone.exe` (`v1.76.0`):

```powershell
dotnet build C:\rclone\RcloneManager.sln -c Debug --nologo
```

Verified scenarios with `rclone.exe`:
- **Initial grace scan**: detected only excluded destination files (`.ravion/sprite1.png`, `Documents/Ravion/Ravion-Client/assets/mob1.png`, and catch-all excluded `.exe`), preserving `DaysRemaining = 30` and `Purged = 0`.
- **Selective expiration (> 30 days)**: backdating `.ravion/sprite1.png` to 31 days caused `ScanAndEvaluateAsync` to delete only the expired file while keeping remaining excluded items in grace.
- **Instant purge (`PurgeAllExcludedNowAsync`)**: deleted remaining excluded files immediately while leaving 100% of included files (`contrato.pdf` and `foto.png`) untouched.

---

## Notes / Things to Watch

- **Google Drive file-creation ceiling**: Google Drive enforces ~2–3 new file creations per second. Folders containing tens of thousands of tiny files (loose game sprites, `node_modules`, `bin`, `obj`, `packages`) should remain excluded or be archived into a `.zip` before syncing.
- **UAC prompt for `WinService`**: selecting `WindowsService` in the Wizard prompts UAC once; if cancelled, onboarding automatically falls back to `UserStartup` (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
