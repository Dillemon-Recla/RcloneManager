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
