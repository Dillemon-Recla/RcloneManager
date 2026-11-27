using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RcloneManager.Core.Storage;

namespace RcloneManager.UI.Services;

public static class ServiceLauncher
{
    public static string ResolveWorkspaceDirectory()
    {
        return WorkspacePathResolver.ResolveRootDirectory();
    }

    public static async Task<(bool Success, bool CancelledByUser, string Message)> TryInstallAndStartWindowsServiceAsync(
        string workDir,
        ServiceApiClient? api = null,
        CancellationToken ct = default)
    {
        var serviceExe = FindServiceExecutable(workDir);
        if (string.IsNullOrWhiteSpace(serviceExe))
            return (false, false, "RcloneManager.Service.exe não foi encontrado para registrar o Serviço do Windows.");

        // Encerra o processo em segundo plano da sessão atual para liberar a porta 5580 antes do WinService assumir
        if (api != null)
        {
            try
            {
                await api.ShutdownServiceAsync(ct);
                await Task.Delay(700, ct);
            }
            catch
            {
            }
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = serviceExe,
                Arguments = $"--install-service --workdir \"{workDir}\"",
                WorkingDirectory = Path.GetDirectoryName(serviceExe) ?? workDir,
                UseShellExecute = true,
                Verb = "runas"
            });

            if (proc == null)
                return (false, false, "Não foi possível iniciar o instalador do Serviço do Windows.");

            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                if (api != null)
                    await EnsureServiceRunningAsync(api, ct);
                return (false, false, $"O registro do Serviço do Windows retornou código {proc.ExitCode}. O modo em 2º plano padrão foi mantido.");
            }

            if (api != null)
            {
                for (var i = 0; i < 32; i++)
                {
                    await Task.Delay(250, ct);
                    if (await IsAliveAsync(api, ct))
                        return (true, false, "Serviço Nativo do Windows (RcloneManagerService) instalado e em execução!");
                }

                await EnsureServiceRunningAsync(api, ct);
            }

            return (true, false, "Serviço Nativo do Windows registrado com sucesso.");
        }
        catch (Win32Exception)
        {
            if (api != null)
                await EnsureServiceRunningAsync(api, ct);
            return (false, true, "Permissão de Administrador (UAC) cancelada. O agendador continuará no modo padrão do usuário.");
        }
        catch (Exception ex)
        {
            if (api != null)
                await EnsureServiceRunningAsync(api, ct);
            return (false, false, $"Erro ao instalar Serviço do Windows: {ex.Message}");
        }
    }

    public static async Task<(bool Success, string Message)> TryUninstallWindowsServiceAsync(
        string workDir,
        ServiceApiClient? api = null,
        CancellationToken ct = default)
    {
        var serviceExe = FindServiceExecutable(workDir);
        if (string.IsNullOrWhiteSpace(serviceExe))
            return (false, "RcloneManager.Service.exe não foi encontrado.");

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = serviceExe,
                Arguments = $"--uninstall-service --workdir \"{workDir}\"",
                WorkingDirectory = Path.GetDirectoryName(serviceExe) ?? workDir,
                UseShellExecute = true,
                Verb = "runas"
            });

            if (proc != null)
                await proc.WaitForExitAsync(ct);

            if (api != null)
                await EnsureServiceRunningAsync(api, ct);

            return proc is { ExitCode: 0 }
                ? (true, "Serviço Nativo do Windows removido. O processo local assumiu o 2º plano.")
                : (false, $"Remoção do serviço retornou código {proc?.ExitCode}.");
        }
        catch (Win32Exception)
        {
            return (false, "Operação administrativa cancelada pelo usuário.");
        }
        catch (Exception ex)
        {
            return (false, $"Erro ao remover serviço: {ex.Message}");
        }
    }

    public static async Task<bool> EnsureServiceRunningAsync(ServiceApiClient api, CancellationToken ct = default)
    {
        if (await IsAliveAsync(api, ct))
            return true;

        var workDir = ResolveWorkspaceDirectory();
        var serviceExe = FindServiceExecutable(workDir);

        ProcessStartInfo psi;
        if (!string.IsNullOrWhiteSpace(serviceExe))
        {
            psi = new ProcessStartInfo
            {
                FileName = serviceExe,
                Arguments = $"--background --workdir \"{workDir}\"",
                WorkingDirectory = Path.GetDirectoryName(serviceExe) ?? workDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            var serviceCsproj = Path.Combine(workDir, "src", "RcloneManager.Service", "RcloneManager.Service.csproj");
            if (!File.Exists(serviceCsproj))
                return false;

            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{serviceCsproj}\" -c Release -- --background --workdir \"{workDir}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        Process.Start(psi);

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(250, ct);
            if (await IsAliveAsync(api, ct))
                return true;
        }

        return false;
    }

    private static async Task<bool> IsAliveAsync(ServiceApiClient api, CancellationToken ct)
    {
        try
        {
            var status = await api.GetStatusAsync(ct);
            return status is { ServiceRunning: true };
        }
        catch
        {
            return false;
        }
    }

    private static string? FindServiceExecutable(string workDir)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "RcloneManager.Service.exe"),
            Path.Combine(workDir, "RcloneManager.Service.exe"),
            Path.Combine(workDir, "publish", "RcloneManager.Service.exe"),
            Path.Combine(workDir, "src", "RcloneManager.Service", "bin", "Release", "net10.0", "RcloneManager.Service.exe"),
            Path.Combine(workDir, "src", "RcloneManager.Service", "bin", "Debug", "net10.0", "RcloneManager.Service.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
