# Filter Rules and Optimizer (`FilterFileHelper`)

## Context & Diagnosed Issues

The legacy `script.bat` backup ran:
```bat
rclone sync "C:\Users\dille" gdrive:BackupPC/ --exclude-from "exclude_user_folder.txt" --skip-links --fast-list --log-file="rclone_backup.log"
```
Testing `exclude_user_folder.txt` directly against `rclone.exe` uncovered three real issues that caused folders like `Downloads` and `.gemini` to upload to Google Drive (triggering permission errors and stalls in `rclone_backup.log`):

1. **Incompatible flag (`--exclude-from` vs `--filter-from`)**: the file contained inclusion rules like `+ **.jpg`, which belong to `--filter-from` syntax.
2. **Malformed syntax for `--filter-from`**: entries such as `/Downloads/**`, `/AppData/**`, and `**/.*/**` lacked the `- ` prefix, causing fatal `malformed rule "/Downloads/**"` errors with `--filter-from`. Also, `- * [-- max-size 50M]` is not valid inline Rclone filter syntax (size limits must be passed via CLI flag `--max-size 50M`).
3. **Precedence order (*First-Match-Wins*)**: Rclone evaluates rules top-to-bottom and picks the first match. Because `+ **.json` and `+ **.md` appeared before exclusions for `/AppData/**` and `**/.*/**`, files inside hidden directories were included before reaching the exclusion rules.

---

## Fixes Applied (`FilterFileHelper.cs`)

Implemented in `src/RcloneManager.Core/Engine/FilterFileHelper.cs`:

- **`Analyze(string filePath)`**:
  - Detects `+ ` include rules, missing `- ` prefixes, inline `[-- max-size ...]` usage (automatically extracting values such as `50M`), and exclusions placed after includes.
  - Returns `FilterAnalysisResult` with `RecommendedMode`, `ExtractedMaxSize`, `Warnings`, and `OptimizedContent`.
- **`ResolveForExecution(...)`**:
  - At runtime, if the original file still contains warnings incompatible with `--filter-from`, the engine **keeps the original file untouched** and generates a sanitized copy under `.rclone-manager-runtime\<name>.sanitized.filter` (logging the adjustment in the service log).
  - When a job's `MaxSize` is empty and the filter file had `[-- max-size 50M]`, `RcloneCliRunner` automatically supplies `--max-size 50M` on the command line.
- **`ApplyOptimizationWithBackup(string filePath)`**:
  - Available in the **Filters & Optimizer** tab (and via `POST /api/filters/optimize`): creates a `.bak` backup (or `.yyyyMMdd_HHmmss.bak` if `.bak` already exists) and writes priority exclusions before `+` rules, replacing `- * [-- max-size ...]` with `- *`.

---

## Validation

- Tested with `rclone.exe ls ... --filter-from ".rclone-manager-runtime\exclude_user_folder.sanitized.filter" --dump filters`: Rclone compiled every exclusion and inclusion rule without `malformed rule` errors.
- Calling `GET /api/filters/analyze` on the original file returned all 3 expected warnings (`hasIncludeRules: true`, `hasInvalidInlineMaxSize: true`, `extractedMaxSize: "50M"`, `hasExclusionsAfterIncludes: true`).

---

## Notes / Things to Watch

- **`--max-size 50M` is global in Rclone**: this flag caps **all** files in the job (including `.mp4`/`.mkv` videos over 50 MB). To transfer large videos while capping other types at 50 MB, split them into two separate jobs.
- **Folder pattern matching in Rclone**:
  - Patterns starting with `/` (e.g., `- /node_modules/**`) match **only at the source root**; without a leading slash (`- node_modules/**`, `- bin/**`, `- obj/**`) they match in any subfolder.
  - Pattern `- **/.*/**` requires a parent subfolder before the dot; to block hidden root folders as well (such as `.gemini/`), add `- .*/**`.
- **`Sync` vs `Copy`**: `Sync` mirrors the source (and can delete destination files removed locally); to copy without deleting on the destination, use `Copy` and preview first with **Dry-Run**.
