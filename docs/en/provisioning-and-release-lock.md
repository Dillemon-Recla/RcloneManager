# Automatic Rclone Provisioning and Release Lock

## Context

To keep the Git repository lightweight (avoiding versioning ~85 MB `rclone.exe` binaries) and ensure consistent CLI flags and RC API behavior across machines, `RcloneManager` implements automatic provisioning with a **release version lock** (`PinnedRcloneVersion` + `LockRcloneVersion`).

By default, the solution pins official release **`v1.76.0`**.

---

## Verification & Download Flow (`RcloneReleaseManager`)

Implemented in `src/RcloneManager.Core/Engine/RcloneReleaseManager.cs`:

1. **Architecture Detection & Normalization**:
   - Maps `RuntimeInformation.OSArchitecture` to official Windows packages: `X64` → `amd64`, `Arm64` → `arm64`, `X86` → `386`.
   - Normalizes and validates versions matching `^v\d+\.\d+(\.\d+)*(-[a-zA-Z0-9.]+)?$` (e.g., `1.76.0` becomes `v1.76.0`).
2. **Installation Check (`CheckInstallationAsync`)**:
   - Checks whether `rclone.exe` exists under `rclone-<version>-windows-<arch>\rclone.exe` (or the configured path).
   - Runs `rclone.exe version` silently to read the actual installed binary version.
   - Sets `NeedsDownload = true` when the binary is missing **or** when `LockRcloneVersion == true` and the installed version differs from the pinned one.
3. **Download with Fallback (`EnsurePinnedReleaseDownloadedAsync`)**:
   - Streams the archive to `.rclone-manager-runtime\downloads\<zip>.part`, trying:
     1. `https://downloads.rclone.org/<version>/rclone-<version>-windows-<arch>.zip`
     2. Fallback: `https://github.com/rclone/rclone/releases/download/<version>/rclone-<version>-windows-<arch>.zip`
4. **SHA-256 Integrity Verification**:
   - For `v1.76.0`, compares against built-in official hashes in `BuiltInSha256` (`amd64`: `8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc`).
   - For other user-selected versions, downloads the official `SHA256SUMS` file (from `downloads.rclone.org` or GitHub Releases) and validates the `.zip` hash. Any mismatch discards the temp file and throws an exception.
5. **Safe Extraction & Post-Install Verification**:
   - Extracts into a staging directory and copies into `rclone-<version>-windows-<arch>\` while preserving existing user files (scripts, logs, filters).
   - Executes `rclone.exe version` on the newly extracted executable to verify an exact match with the pinned release.

---

## UI Integration & Service Enforcement

- **UI Startup (`MainWindow_Loaded` + `RcloneSetupWindow`)**:
  - Before starting `RcloneManager.Service`, the UI runs `CheckInstallationAsync`. If `NeedsDownload` is true, it opens the modal `RcloneSetupWindow` with a live progress bar and updates `rclone_manager_config.json` when finished.
- **Version Switching / Re-applying in UI**:
  - Under the **Logs & Service** tab, the **Rclone Release (Auto-Download & Version Lock)** card lets you change the target version or toggle the lock. Before downloading/replacing, the UI calls `POST /api/rclone/stop-rcd` to release any `rclone.exe` file handle, and upon completion sends `POST /api/rclone/release-config` so the service reloads the binary and restarts `rcd`.
- **Engine Lock (`RcloneHybridEngine.ValidatePinnedVersionAsync`)**:
  - When `LockRcloneVersion` is enabled, both `StartRcDaemonAsync` and `ExecuteJobAsync` refuse to run if the target binary does not match `PinnedRcloneVersion` exactly.

---

## Validation

End-to-end provisioning test run in an empty `%TEMP%` directory after `.NET` compilation:
1. `CheckInstallationAsync` on an empty folder returned `Exists=False, NeedsDownload=True`.
2. `EnsurePinnedReleaseDownloadedAsync` downloaded `rclone-v1.76.0-windows-amd64.zip`, verified SHA-256, extracted it, and confirmed the executable version.
3. Second `CheckInstallationAsync` call returned `Exists=True, Installed=v1.76.0, IsMatch=True, NeedsDownload=False`.

---

## Notes / Things to Watch

- Auto-download requires HTTPS access to `downloads.rclone.org` or `github.com` only when the local binary is missing or mismatches the locked release.
- If a custom version (beta/fork) does not publish an official `SHA256SUMS` file at those URLs, SHA-256 verification refuses the installation for safety.
