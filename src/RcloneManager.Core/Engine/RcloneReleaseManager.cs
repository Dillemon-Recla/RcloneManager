using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RcloneManager.Core.Models;
using RcloneManager.Core.Storage;

namespace RcloneManager.Core.Engine;

public sealed class RcloneDownloadProgress
{
    public string Stage { get; set; } = string.Empty;
    public double Percent { get; set; }
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class RcloneInstallationCheck
{
    public string ExecutablePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public string? InstalledVersion { get; set; }
    public string PinnedVersion { get; set; } = RcloneReleaseManager.DefaultPinnedVersion;
    public bool IsLocked { get; set; } = true;
    public bool IsVersionMatch { get; set; }
    public bool NeedsDownload { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

public static partial class RcloneReleaseManager
{
    public const string DefaultPinnedVersion = "v1.76.0";

    private static readonly IReadOnlyDictionary<string, string> BuiltInSha256 =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rclone-v1.76.0-windows-amd64.zip"] = "8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc",
            ["rclone-v1.76.0-windows-arm64.zip"] = "c406c8a69a5a408e50624aa990fbb0a6266b3a4e83782a94de3cb96f50f86f10",
            ["rclone-v1.76.0-windows-386.zip"] = "b9c8d062ff61e2addfc31c1981219d90f99e0012cba45b649f728082ad810d5c"
        };

    [GeneratedRegex(@"^v\d+\.\d+(\.\d+)*(-[a-zA-Z0-9.]+)?$", RegexOptions.Compiled)]
    private static partial Regex VersionPatternRegex();

    public static string GetWindowsArchToken() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "386",
        _ => "amd64"
    };

    public static string NormalizeVersion(string? raw)
    {
        if (!TryNormalizeVersion(raw, out var normalized))
            throw new ArgumentException($"Formato de versão do Rclone inválido: '{raw}'. Use algo como v1.76.0.");

        return normalized;
    }

    public static bool TryNormalizeVersion(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('v') && !trimmed.StartsWith('V'))
            trimmed = "v" + trimmed;
        else
            trimmed = "v" + trimmed[1..];

        if (!VersionPatternRegex().IsMatch(trimmed))
            return false;

        normalized = trimmed;
        return true;
    }

    public static string GetArchiveFileName(string version)
    {
        var normalized = NormalizeVersion(version);
        return $"rclone-{normalized}-windows-{GetWindowsArchToken()}.zip";
    }

    public static string GetExpectedReleaseDirectory(string rootDir, string version)
    {
        var normalized = NormalizeVersion(version);
        return Path.Combine(rootDir, $"rclone-{normalized}-windows-{GetWindowsArchToken()}");
    }

    public static string GetExpectedExecutablePath(string rootDir, string version)
    {
        return Path.Combine(GetExpectedReleaseDirectory(rootDir, version), "rclone.exe");
    }

    public static async Task<string?> DetectInstalledVersionAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdOut = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdOut))
                return null;

            var firstLine = stdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var rawToken = firstLine.Replace("rclone", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            return TryNormalizeVersion(rawToken, out var normalized) ? normalized : rawToken;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<RcloneInstallationCheck> CheckInstallationAsync(
        AppManagerConfig config,
        string rootDir,
        CancellationToken ct = default)
    {
        var pinned = TryNormalizeVersion(config.PinnedRcloneVersion, out var norm)
            ? norm
            : DefaultPinnedVersion;

        var candidateExe = config.RcloneExecutablePath;
        if (string.IsNullOrWhiteSpace(candidateExe) || !File.Exists(candidateExe))
        {
            candidateExe = WorkspacePathResolver.FindRcloneExecutable(rootDir, pinned);
        }

        // Quando a trava estiver ativa, prioriza o diretório exato da release travada caso já tenha sido baixado antes
        var expectedPinnedExe = GetExpectedExecutablePath(rootDir, pinned);
        if (config.LockRcloneVersion && File.Exists(expectedPinnedExe))
        {
            candidateExe = expectedPinnedExe;
        }

        var exists = File.Exists(candidateExe);
        var installedVersion = exists ? await DetectInstalledVersionAsync(candidateExe, ct) : null;
        var isMatch = exists && string.Equals(installedVersion, pinned, StringComparison.OrdinalIgnoreCase);
        var needsDownload = !exists || (config.LockRcloneVersion && !isMatch);

        string statusMessage;
        if (!exists)
        {
            statusMessage = $"rclone.exe não encontrado. Necessário baixar a release {pinned} ({GetWindowsArchToken()}).";
        }
        else if (config.LockRcloneVersion && !isMatch)
        {
            statusMessage = $"Versão instalada ({installedVersion ?? "desconhecida"}) diverge da release travada ({pinned}). Necessário baixar {pinned}.";
        }
        else if (isMatch)
        {
            statusMessage = $"Rclone {pinned} pronto e verificado.";
        }
        else
        {
            statusMessage = $"Rclone {installedVersion ?? "desconhecido"} instalado (trava desativada).";
        }

        return new RcloneInstallationCheck
        {
            ExecutablePath = candidateExe,
            Exists = exists,
            InstalledVersion = installedVersion,
            PinnedVersion = pinned,
            IsLocked = config.LockRcloneVersion,
            IsVersionMatch = isMatch,
            NeedsDownload = needsDownload,
            StatusMessage = statusMessage
        };
    }

    public static async Task<string> EnsurePinnedReleaseDownloadedAsync(
        string rootDir,
        string pinnedVersion,
        IProgress<RcloneDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var version = NormalizeVersion(pinnedVersion);
        var zipName = GetArchiveFileName(version);
        var primaryUrl = $"https://downloads.rclone.org/{version}/{zipName}";
        var fallbackUrl = $"https://github.com/rclone/rclone/releases/download/{version}/{zipName}";

        var runtimeDir = Path.Combine(rootDir, ".rclone-manager-runtime", "downloads");
        Directory.CreateDirectory(runtimeDir);

        var partPath = Path.Combine(runtimeDir, zipName + ".part");
        var stagingDir = Path.Combine(runtimeDir, "extract_" + Guid.NewGuid().ToString("N")[..8]);
        var targetReleaseDir = GetExpectedReleaseDirectory(rootDir, version);
        var targetExePath = Path.Combine(targetReleaseDir, "rclone.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RcloneManager/1.0");

        try
        {
            progress?.Report(new RcloneDownloadProgress
            {
                Stage = "Conectando...",
                Percent = 2,
                Detail = $"Obtendo {zipName} ({version})..."
            });

            await DownloadWithFallbackAsync(http, primaryUrl, fallbackUrl, partPath, zipName, progress, ct);

            progress?.Report(new RcloneDownloadProgress
            {
                Stage = "Verificando SHA-256...",
                Percent = 82,
                Detail = "Validando integridade do pacote baixado..."
            });

            var expectedSha256 = await ResolveExpectedSha256Async(http, version, zipName, ct);
            var actualSha256 = await ComputeSha256HexAsync(partPath, ct);

            if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Falha na verificação SHA-256 de {zipName}. Esperado: {expectedSha256}; Obtido: {actualSha256}.");
            }

            progress?.Report(new RcloneDownloadProgress
            {
                Stage = "Extraindo...",
                Percent = 90,
                Detail = $"Extraindo para '{targetReleaseDir}'..."
            });

            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(partPath, stagingDir, overwriteFiles: true);

            var extractedExe = Directory.EnumerateFiles(stagingDir, "rclone.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedExe))
                throw new InvalidDataException($"O pacote '{zipName}' não contém rclone.exe.");

            var extractedRoot = Path.GetDirectoryName(extractedExe)!;
            CopyDirectoryPreservingExistingUserFiles(extractedRoot, targetReleaseDir);

            progress?.Report(new RcloneDownloadProgress
            {
                Stage = "Validando binário...",
                Percent = 97,
                Detail = $"Confirmando execução de {targetExePath}..."
            });

            var detected = await DetectInstalledVersionAsync(targetExePath, ct);
            if (!string.Equals(detected, version, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Binário extraído reportou versão '{detected ?? "desconhecida"}', diferente da release travada '{version}'.");
            }

            progress?.Report(new RcloneDownloadProgress
            {
                Stage = "Concluído",
                Percent = 100,
                Detail = $"Rclone {version} instalado e travado com sucesso!"
            });

            return targetExePath;
        }
        finally
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    private static async Task DownloadWithFallbackAsync(
        HttpClient http,
        string primaryUrl,
        string fallbackUrl,
        string destinationFile,
        string zipName,
        IProgress<RcloneDownloadProgress>? progress,
        CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var url in new[] { primaryUrl, fallbackUrl })
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long totalRead = 0;
                var lastReportTick = Environment.TickCount64;

                while (true)
                {
                    var read = await networkStream.ReadAsync(buffer, ct);
                    if (read == 0)
                        break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;

                    var now = Environment.TickCount64;
                    if (now - lastReportTick >= 100 || (totalBytes.HasValue && totalRead >= totalBytes.Value))
                    {
                        lastReportTick = now;
                        var downloadPct = totalBytes is > 0
                            ? 5.0 + (totalRead * 75.0 / totalBytes.Value)
                            : 40.0;
                        var detail = totalBytes is > 0
                            ? $"Baixando {zipName}... {FormatBytes(totalRead)} / {FormatBytes(totalBytes.Value)}"
                            : $"Baixando {zipName}... {FormatBytes(totalRead)}";

                        progress?.Report(new RcloneDownloadProgress
                        {
                            Stage = "Baixando Rclone...",
                            Percent = Math.Clamp(downloadPct, 5.0, 80.0),
                            BytesDownloaded = totalRead,
                            TotalBytes = totalBytes,
                            Detail = detail
                        });
                    }
                }

                await fileStream.FlushAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { if (File.Exists(destinationFile)) File.Delete(destinationFile); } catch { }
            }
        }

        throw new HttpRequestException($"Não foi possível baixar {zipName} das fontes oficiais ({lastError?.Message}).", lastError);
    }

    private static async Task<string> ResolveExpectedSha256Async(
        HttpClient http,
        string version,
        string zipName,
        CancellationToken ct)
    {
        if (BuiltInSha256.TryGetValue(zipName, out var embedded))
            return embedded;

        var urls = new[]
        {
            $"https://downloads.rclone.org/{version}/SHA256SUMS",
            $"https://github.com/rclone/rclone/releases/download/{version}/SHA256SUMS"
        };

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                var content = await http.GetStringAsync(url, ct);
                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[^1].Equals(zipName, StringComparison.OrdinalIgnoreCase))
                    {
                        return parts[0].Trim();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"Não foi possível localizar o hash SHA-256 oficial para '{zipName}' ({lastError?.Message ?? "arquivo ausente em SHA256SUMS"}).",
            lastError);
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void CopyDirectoryPreservingExistingUserFiles(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var destSub = Path.Combine(destinationDir, Path.GetFileName(dir));
            CopyDirectoryPreservingExistingUserFiles(dir, destSub);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double val = bytes;
        var idx = 0;
        while (val >= 1024 && idx < units.Length - 1)
        {
            val /= 1024;
            idx++;
        }
        return $"{val:0.##} {units[idx]}";
    }
}
