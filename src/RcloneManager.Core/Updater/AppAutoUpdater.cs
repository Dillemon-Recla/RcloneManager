using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RcloneManager.Core.Updater;

public sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

public sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAssetDto> Assets { get; set; } = new();
}

public sealed class PendingAppUpdateInfo
{
    public string VersionTag { get; set; } = string.Empty;
    public string NormalizedVersion { get; set; } = string.Empty;
    public string StagedDirectory { get; set; } = string.Empty;
    public string TargetAppDirectory { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public DateTime DownloadedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppUpdateCheckResult
{
    public bool UpdateAvailable { get; set; }
    public bool IsStagedAndReady { get; set; }
    public string CurrentVersion { get; set; } = "1.0.0";
    public string? LatestVersionTag { get; set; }
    public string? ReleaseNotes { get; set; }
    public string? ReleaseUrl { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public PendingAppUpdateInfo? PendingUpdate { get; set; }
}

[SupportedOSPlatform("windows")]
public static class AppAutoUpdater
{
    public const string DefaultGitHubRepo = "Dillemon-Recla/RcloneManager";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] ProtectedUserFilePatterns =
    {
        "rclone_manager_config.json*",
        "rclone.conf*",
        "exclude_user_folder.txt*",
        "*.filter",
        "*.log",
        "*.bak"
    };

    public static Version GetCurrentVersion(Assembly? entryAssembly = null)
    {
        var asm = entryAssembly ?? Assembly.GetEntryAssembly() ?? typeof(AppAutoUpdater).Assembly;
        var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoAttr))
        {
            var cleanInfo = infoAttr.Split('+', '-')[0];
            if (TryParseSemanticVersion(cleanInfo, out var parsedInfo))
                return parsedInfo;
        }

        var v = asm.GetName().Version;
        return v != null ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
    }

    public static string FormatVersion(Version v) =>
        $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    public static bool IsDevelopmentBuild(string workDir, string? appBaseDir = null)
    {
        var baseDir = Path.GetFullPath(appBaseDir ?? AppContext.BaseDirectory);
        var slnExists = File.Exists(Path.Combine(workDir, "RcloneManager.sln"));
        var inSrcBin = baseDir.Contains(Path.Combine("src", "RcloneManager.UI"), StringComparison.OrdinalIgnoreCase);
        return slnExists && inSrcBin;
    }

    public static string GetUpdatesRootDirectory(string workDir) =>
        Path.Combine(workDir, ".rclone-manager-runtime", "updates");

    public static string GetPendingManifestPath(string workDir) =>
        Path.Combine(GetUpdatesRootDirectory(workDir), "pending_update.json");

    public static PendingAppUpdateInfo? TryGetValidPendingUpdate(string workDir, Version? currentVersion = null)
    {
        try
        {
            var manifestPath = GetPendingManifestPath(workDir);
            if (!File.Exists(manifestPath))
                return null;

            var json = File.ReadAllText(manifestPath);
            var pending = JsonSerializer.Deserialize<PendingAppUpdateInfo>(json, JsonOptions);
            if (pending == null ||
                string.IsNullOrWhiteSpace(pending.StagedDirectory) ||
                !Directory.Exists(pending.StagedDirectory) ||
                !File.Exists(Path.Combine(pending.StagedDirectory, "RcloneManager.UI.exe")))
            {
                ClearPendingUpdate(workDir);
                return null;
            }

            var current = currentVersion ?? GetCurrentVersion();
            if (TryParseSemanticVersion(pending.NormalizedVersion, out var pendingVer) && pendingVer <= current)
            {
                // A atualização já foi aplicada (ou a versão instalada já é igual/superior): limpa os arquivos temporários.
                ClearPendingUpdate(workDir);
                return null;
            }

            return pending;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearPendingUpdate(string workDir)
    {
        try
        {
            var root = GetUpdatesRootDirectory(workDir);
            var manifest = GetPendingManifestPath(workDir);
            if (File.Exists(manifest))
                File.Delete(manifest);

            var stagedRoot = Path.Combine(root, "staged");
            if (Directory.Exists(stagedRoot))
                Directory.Delete(stagedRoot, recursive: true);
        }
        catch
        {
        }
    }

    public static async Task<AppUpdateCheckResult> CheckAndStageUpdateAsync(
        string workDir,
        bool downloadAndStageIfAvailable,
        string repo = DefaultGitHubRepo,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default)
    {
        var currentVer = GetCurrentVersion();
        var currentVerStr = FormatVersion(currentVer);

        var existingPending = TryGetValidPendingUpdate(workDir, currentVer);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RcloneManager/{currentVerStr}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        GitHubReleaseDto? release;
        try
        {
            statusProgress?.Report("Consultando última release no GitHub...");
            using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new AppUpdateCheckResult
                {
                    UpdateAvailable = false,
                    CurrentVersion = currentVerStr,
                    StatusMessage = "Nenhuma release publicada no repositório do GitHub ainda."
                };
            }

            resp.EnsureSuccessStatusCode();
            release = await resp.Content.ReadFromJsonAsync<GitHubReleaseDto>(JsonOptions, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (existingPending != null)
            {
                return new AppUpdateCheckResult
                {
                    UpdateAvailable = true,
                    IsStagedAndReady = true,
                    CurrentVersion = currentVerStr,
                    LatestVersionTag = existingPending.VersionTag,
                    ReleaseNotes = existingPending.ReleaseNotes,
                    ReleaseUrl = existingPending.ReleaseUrl,
                    PendingUpdate = existingPending,
                    StatusMessage = $"Atualização {existingPending.VersionTag} pronta para aplicar ao fechar/reabrir."
                };
            }

            return new AppUpdateCheckResult
            {
                UpdateAvailable = false,
                CurrentVersion = currentVerStr,
                StatusMessage = $"Não foi possível verificar atualizações agora: {ex.Message}"
            };
        }

        if (release == null || release.Draft || string.IsNullOrWhiteSpace(release.TagName))
        {
            return new AppUpdateCheckResult
            {
                UpdateAvailable = false,
                CurrentVersion = currentVerStr,
                StatusMessage = "Nenhuma versão estável disponível."
            };
        }

        if (!TryParseSemanticVersion(release.TagName, out var latestVer) || latestVer <= currentVer)
        {
            return new AppUpdateCheckResult
            {
                UpdateAvailable = false,
                CurrentVersion = currentVerStr,
                LatestVersionTag = release.TagName,
                ReleaseUrl = release.HtmlUrl,
                StatusMessage = $"Você já está na versão mais recente (v{currentVerStr})."
            };
        }

        var normalizedLatest = FormatVersion(latestVer);
        if (existingPending != null &&
            string.Equals(existingPending.NormalizedVersion, normalizedLatest, StringComparison.OrdinalIgnoreCase))
        {
            return new AppUpdateCheckResult
            {
                UpdateAvailable = true,
                IsStagedAndReady = true,
                CurrentVersion = currentVerStr,
                LatestVersionTag = release.TagName,
                ReleaseNotes = release.Body,
                ReleaseUrl = release.HtmlUrl,
                PendingUpdate = existingPending,
                StatusMessage = $"Nova versão {release.TagName} já baixada — será aplicada ao fechar ou reiniciar."
            };
        }

        if (!downloadAndStageIfAvailable)
        {
            return new AppUpdateCheckResult
            {
                UpdateAvailable = true,
                IsStagedAndReady = false,
                CurrentVersion = currentVerStr,
                LatestVersionTag = release.TagName,
                ReleaseNotes = release.Body,
                ReleaseUrl = release.HtmlUrl,
                StatusMessage = $"Nova versão {release.TagName} disponível no GitHub."
            };
        }

        var zipAsset = SelectMatchingZipAsset(release.Assets);
        if (zipAsset == null)
        {
            return new AppUpdateCheckResult
            {
                UpdateAvailable = true,
                IsStagedAndReady = false,
                CurrentVersion = currentVerStr,
                LatestVersionTag = release.TagName,
                ReleaseNotes = release.Body,
                ReleaseUrl = release.HtmlUrl,
                StatusMessage = $"Versão {release.TagName} encontrada, mas sem pacote .zip compatível com Windows ({GetRuntimeRid()})."
            };
        }

        var updatesRoot = GetUpdatesRootDirectory(workDir);
        Directory.CreateDirectory(updatesRoot);

        var tempZipPath = Path.Combine(updatesRoot, $"{zipAsset.Name}.part");
        var tempExtractDir = Path.Combine(updatesRoot, "extract_" + Guid.NewGuid().ToString("N")[..8]);
        var finalStagedDir = Path.Combine(updatesRoot, "staged", normalizedLatest);

        try
        {
            statusProgress?.Report($"Baixando atualização {release.TagName} em segundo plano...");
            await DownloadFileAsync(http, zipAsset.BrowserDownloadUrl, tempZipPath, ct);

            statusProgress?.Report($"Verificando integridade SHA-256 de {zipAsset.Name}...");
            var expectedSha = await ResolveExpectedSha256Async(http, release.Assets, zipAsset, ct);
            if (!string.IsNullOrWhiteSpace(expectedSha))
            {
                var actualSha = await ComputeSha256Async(tempZipPath, ct);
                if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Checksum SHA-256 inválido para '{zipAsset.Name}'. Esperado: {expectedSha}, obtido: {actualSha}.");
                }
            }

            statusProgress?.Report($"Preparando arquivos da versão {release.TagName}...");
            Directory.CreateDirectory(tempExtractDir);
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractDir, overwriteFiles: true);

            var extractedUiExe = Directory
                .EnumerateFiles(tempExtractDir, "RcloneManager.UI.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(extractedUiExe))
                throw new InvalidDataException($"O pacote '{zipAsset.Name}' não contém RcloneManager.UI.exe.");

            var payloadRoot = Path.GetDirectoryName(extractedUiExe)!;

            if (Directory.Exists(finalStagedDir))
                Directory.Delete(finalStagedDir, recursive: true);

            var stagedParent = Path.GetDirectoryName(finalStagedDir)!;
            Directory.CreateDirectory(stagedParent);
            Directory.Move(payloadRoot, finalStagedDir);

            var targetAppDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pendingInfo = new PendingAppUpdateInfo
            {
                VersionTag = release.TagName,
                NormalizedVersion = normalizedLatest,
                StagedDirectory = finalStagedDir,
                TargetAppDirectory = targetAppDir,
                ReleaseUrl = release.HtmlUrl,
                ReleaseNotes = release.Body ?? string.Empty,
                DownloadedAtUtc = DateTime.UtcNow
            };

            File.WriteAllText(GetPendingManifestPath(workDir), JsonSerializer.Serialize(pendingInfo, JsonOptions));

            return new AppUpdateCheckResult
            {
                UpdateAvailable = true,
                IsStagedAndReady = true,
                CurrentVersion = currentVerStr,
                LatestVersionTag = release.TagName,
                ReleaseNotes = release.Body,
                ReleaseUrl = release.HtmlUrl,
                PendingUpdate = pendingInfo,
                StatusMessage = $"Nova versão {release.TagName} pronta! Será instalada automaticamente ao fechar ou reiniciar."
            };
        }
        finally
        {
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            try { if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, recursive: true); } catch { }
        }
    }

    public static bool LaunchDetachedUpdateApplier(
        string workDir,
        PendingAppUpdateInfo pending,
        bool relaunchUiAfterUpdate,
        bool restartBackgroundServiceIfNotRelaunchingUi,
        int serviceApiPort = 5580)
    {
        if (!Directory.Exists(pending.StagedDirectory) ||
            !File.Exists(Path.Combine(pending.StagedDirectory, "RcloneManager.UI.exe")))
        {
            return false;
        }

        var updatesRoot = GetUpdatesRootDirectory(workDir);
        Directory.CreateDirectory(updatesRoot);

        var scriptPath = Path.Combine(updatesRoot, "apply_update.ps1");
        var logPath = Path.Combine(updatesRoot, "last_update.log");
        var manifestPath = GetPendingManifestPath(workDir);
        var currentPid = Environment.ProcessId;

        var psScript = BuildPowerShellUpdaterScript(
            currentPid: currentPid,
            stagedDir: pending.StagedDirectory,
            targetDir: pending.TargetAppDirectory,
            workDir: workDir,
            manifestPath: manifestPath,
            logPath: logPath,
            serviceApiPort: serviceApiPort,
            relaunchUi: relaunchUiAfterUpdate,
            restartServiceOnly: !relaunchUiAfterUpdate && restartBackgroundServiceIfNotRelaunchingUi);

        File.WriteAllText(scriptPath, psScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            WorkingDirectory = updatesRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process.Start(psi);
        return true;
    }

    private static string BuildPowerShellUpdaterScript(
        int currentPid,
        string stagedDir,
        string targetDir,
        string workDir,
        string manifestPath,
        string logPath,
        int serviceApiPort,
        bool relaunchUi,
        bool restartServiceOnly)
    {
        static string Esc(string s) => s.Replace("'", "''");

        var protectedPatternsJoined = string.Join(", ", ProtectedUserFilePatterns.Select(p => $"'{Esc(p)}'"));

        return $$"""
$ErrorActionPreference = 'Continue'
$stagedDir = '{{Esc(stagedDir)}}'
$targetDir = '{{Esc(targetDir)}}'
$workDir = '{{Esc(workDir)}}'
$manifestPath = '{{Esc(manifestPath)}}'
$logPath = '{{Esc(logPath)}}'
$uiPid = {{currentPid}}
$apiPort = {{serviceApiPort}}
$relaunchUi = ${{relaunchUi.ToString().ToLowerInvariant()}}
$restartService = ${{restartServiceOnly.ToString().ToLowerInvariant()}}
$protectedPatterns = @({{protectedPatternsJoined}})

function Write-UpdateLog([string]$msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    Add-Content -Path $logPath -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
}

Write-UpdateLog "Iniciando aplicacao de atualizacao de '$stagedDir' para '$targetDir'..."

# 1. Aguarda encerramento do processo da interface
for ($i = 0; $i -lt 40; $i++) {
    if (-not (Get-Process -Id $uiPid -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 250
}

# 2. Solicita parada amigavel do servico local caso esteja em execucao
try {
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$apiPort/api/service/shutdown" -TimeoutSec 3 | Out-Null
    Write-UpdateLog "Solicitado encerramento temporario do RcloneManager.Service."
} catch { }

$svcExeTarget = Join-Path $targetDir 'RcloneManager.Service.exe'
for ($i = 0; $i -lt 30; $i++) {
    $runningSvc = Get-Process -Name 'RcloneManager.Service' -ErrorAction SilentlyContinue
    if (-not $runningSvc) { break }
    Start-Sleep -Milliseconds 300
}

# 3. Copia os arquivos novos preservando arquivos de configuracao, filtros e logs do usuario
function Test-ProtectedRelativePath([string]$relPath) {
    $leaf = Split-Path $relPath -Leaf
    if ($relPath -like '.rclone-manager-runtime*' -or $relPath -like 'rclone-v*') { return $true }
    foreach ($pat in $protectedPatterns) {
        if ($leaf -like $pat) { return $true }
    }
    return $false
}

$allSourceFiles = Get-ChildItem -Path $stagedDir -Recurse -File
$copyFailed = $false

foreach ($file in $allSourceFiles) {
    $rel = $file.FullName.Substring($stagedDir.Length).TrimStart('\', '/')
    if (Test-ProtectedRelativePath $rel) { continue }

    $destPath = Join-Path $targetDir $rel
    $destParent = Split-Path $destPath -Parent
    if (-not (Test-Path $destParent)) {
        New-Item -ItemType Directory -Path $destParent -Force | Out-Null
    }

    $copied = $false
    for ($retry = 0; $retry -lt 8; $retry++) {
        try {
            Copy-Item -Path $file.FullName -Destination $destPath -Force -ErrorAction Stop
            $copied = $true
            break
        } catch {
            Start-Sleep -Milliseconds 350
        }
    }

    if (-not $copied) {
        Write-UpdateLog "ERRO ao atualizar arquivo: $destPath"
        $copyFailed = $true
    }
}

if (-not $copyFailed) {
    Remove-Item -Path $manifestPath -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $stagedDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-UpdateLog "Atualizacao concluida com sucesso!"
} else {
    Write-UpdateLog "Atualizacao teve arquivos bloqueados; manifesto mantido para proxima tentativa."
}

# 4. Reabre a interface ou reinicia apenas o servico em 2o plano conforme o modo de fechamento
$uiExeTarget = Join-Path $targetDir 'RcloneManager.UI.exe'
if ($relaunchUi -and (Test-Path $uiExeTarget)) {
    Write-UpdateLog "Reabrindo RcloneManager.UI.exe..."
    Start-Process -FilePath $uiExeTarget -WorkingDirectory $targetDir
} elseif ($restartService -and (Test-Path $svcExeTarget)) {
    Write-UpdateLog "Reiniciando RcloneManager.Service.exe em 2o plano..."
    Start-Process -FilePath $svcExeTarget -ArgumentList "--background --workdir `"$workDir`"" -WorkingDirectory $targetDir -WindowStyle Hidden
}
""";
    }

    public static bool TryParseSemanticVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var clean = raw.Trim().TrimStart('v', 'V');
        var dashIdx = clean.IndexOfAny(new[] { '-', '+' });
        if (dashIdx > 0)
            clean = clean[..dashIdx];

        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return false;

        var patch = parts.Length >= 3 && int.TryParse(parts[2], out var p) ? p : 0;
        version = new Version(major, minor, patch);
        return true;
    }

    public static string GetRuntimeRid() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        Architecture.X86 => "win-x86",
        _ => "win-x64"
    };

    private static GitHubReleaseAssetDto? SelectMatchingZipAsset(IReadOnlyList<GitHubReleaseAssetDto> assets)
    {
        var zips = assets
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (zips.Count == 0)
            return null;

        var rid = GetRuntimeRid();
        var altToken = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "amd64"
        };

        return zips.FirstOrDefault(a => a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase))
               ?? zips.FirstOrDefault(a => a.Name.Contains(altToken, StringComparison.OrdinalIgnoreCase))
               ?? (zips.Count == 1 ? zips[0] : null);
    }

    private static async Task<string?> ResolveExpectedSha256Async(
        HttpClient http,
        IReadOnlyList<GitHubReleaseAssetDto> assets,
        GitHubReleaseAssetDto targetZip,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(targetZip.Digest) &&
            targetZip.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return targetZip.Digest["sha256:".Length..].Trim();
        }

        var sumsAsset = assets.FirstOrDefault(a =>
            a.Name.Contains("SHA256", StringComparison.OrdinalIgnoreCase) ||
            a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));

        if (sumsAsset == null)
            return null;

        try
        {
            var text = await http.GetStringAsync(sumsAsset.BrowserDownloadUrl, ct);
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[^1].Equals(targetZip.Name, StringComparison.OrdinalIgnoreCase))
                    return parts[0].Trim();
                if (parts.Length == 1 && sumsAsset.Name.StartsWith(targetZip.Name, StringComparison.OrdinalIgnoreCase))
                    return parts[0].Trim();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return null;
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string destinationPath, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await netStream.CopyToAsync(fileStream, 81920, ct);
        await fileStream.FlushAsync(ct);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
