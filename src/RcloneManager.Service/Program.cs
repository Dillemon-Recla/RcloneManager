using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Logging;
using RcloneManager.Core.Models;
using RcloneManager.Core.Scheduling;
using RcloneManager.Core.Storage;
using RcloneManager.Service;

[assembly: SupportedOSPlatform("windows")]

var workDir = ServiceControlHelper.ResolveWorkspaceDirectory(args);

if (args.Any(a => a.Equals("--install-service", StringComparison.OrdinalIgnoreCase)))
{
    var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RcloneManager.Service.exe");
    var binPath = $"\"{exePath}\" --windows-service --workdir \"{workDir}\"";
    var (code, output) = ServiceControlHelper.RunScCommand(
        $"create {ServiceControlHelper.WindowsServiceName} binPath= \"{binPath.Replace("\"", "\\\"")}\" start= auto DisplayName= \"Rclone Manager Service\"");
    Console.WriteLine(output);
    return code;
}

if (args.Any(a => a.Equals("--uninstall-service", StringComparison.OrdinalIgnoreCase)))
{
    ServiceControlHelper.RunScCommand($"stop {ServiceControlHelper.WindowsServiceName}");
    var (code, output) = ServiceControlHelper.RunScCommand($"delete {ServiceControlHelper.WindowsServiceName}");
    Console.WriteLine(output);
    return code;
}

var isWindowsService = args.Any(a => a.Equals("--windows-service", StringComparison.OrdinalIgnoreCase));
var isBackground = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
var serviceStartedAt = DateTime.Now;

WebApplication BuildApp(string[] cliArgs, string resolvedWorkDir)
{
    var builder = WebApplication.CreateBuilder(cliArgs);

    if (isWindowsService || isBackground)
    {
        builder.Logging.ClearProviders();
    }

    var configStore = new AppConfigStore(resolvedWorkDir);
    var initialConfig = configStore.LoadOrCreateDefault();
    var logBuffer = new ServiceLogBuffer(resolvedWorkDir);

    builder.WebHost.UseUrls($"http://127.0.0.1:{initialConfig.ServiceApiPort}");

    builder.Services.ConfigureHttpJsonOptions(opts =>
    {
        opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    builder.Services.AddSingleton(configStore);
    builder.Services.AddSingleton(logBuffer);
    builder.Services.AddSingleton<RcloneHybridEngine>();
    builder.Services.AddSingleton<JobScheduler>();
    builder.Services.AddHostedService<RcloneBackgroundWorker>();

    var app = builder.Build();

    app.MapGet("/api/status", async (
        JobScheduler scheduler,
        RcloneHybridEngine engine,
        ServiceLogBuffer log,
        CancellationToken ct) =>
    {
        var cfg = scheduler.GetConfigSnapshot();
        var rcloneVersion = await engine.GetRcloneVersionAsync(cfg, ct);
        var (isVersionValid, _) = await engine.ValidatePinnedVersionAsync(cfg, ct);

        return Results.Ok(new ServiceStatusDto
        {
            ServiceRunning = true,
            ServiceStartedAt = serviceStartedAt,
            ServerTimeUtc = DateTime.UtcNow,
            RcloneVersion = rcloneVersion,
            PinnedRcloneVersion = cfg.PinnedRcloneVersion,
            LockRcloneVersion = cfg.LockRcloneVersion,
            IsRcloneVersionValid = isVersionValid,
            RcloneExecutablePath = cfg.RcloneExecutablePath,
            RcloneConfigPath = cfg.RcloneConfigPath,
            RcDaemonRunning = engine.IsRcDaemonRunning,
            RcDaemonPort = engine.RcDaemonPort,
            CurrentBandwidthLimit = cfg.GlobalBandwidthLimit,
            StartupWithWindowsEnabled = ServiceControlHelper.IsStartupEnabled(),
            ActiveJob = engine.GetActiveExecution(),
            LiveStats = engine.GetLiveStats(),
            Jobs = scheduler.GetJobs(),
            RecentServiceLogs = log.GetRecent(150)
        });
    });

    app.MapGet("/api/jobs", (JobScheduler scheduler) =>
        Results.Ok(scheduler.GetJobs()));

    app.MapPost("/api/jobs", (SyncJob job, JobScheduler scheduler) =>
    {
        if (string.IsNullOrWhiteSpace(job.SourcePath) || string.IsNullOrWhiteSpace(job.DestinationPath))
            return Results.BadRequest(new { error = "Origem e Destino são obrigatórios." });

        var saved = scheduler.UpsertJob(job);
        return Results.Ok(saved);
    });

    app.MapDelete("/api/jobs/{id}", (string id, JobScheduler scheduler) =>
    {
        var removed = scheduler.DeleteJob(id);
        return removed ? Results.Ok(new { deleted = true }) : Results.NotFound(new { error = "Tarefa não encontrada." });
    });

    app.MapPost("/api/jobs/{id}/run", async (
        string id,
        HttpRequest request,
        JobScheduler scheduler) =>
    {
        var dryRunQuery = request.Query["dryRun"].FirstOrDefault();
        var dryRun = bool.TryParse(dryRunQuery, out var dr) && dr;
        ExecutionStrategy? overrideStrategy = null;

        if (request.ContentLength is > 0)
        {
            try
            {
                var body = await request.ReadFromJsonAsync<RunJobRequest>();
                if (body != null)
                {
                    dryRun = dryRun || body.DryRun;
                    overrideStrategy = body.OverrideStrategy;
                }
            }
            catch
            {
                // Corpo opcional
            }
        }

        var queued = scheduler.EnqueueManualRun(id, dryRun, overrideStrategy);
        return queued
            ? Results.Ok(new { queued = true, jobId = id, dryRun })
            : Results.NotFound(new { error = "Tarefa não encontrada." });
    });

    app.MapPost("/api/jobs/{id}/stop", (string id, JobScheduler scheduler) =>
    {
        var stopped = scheduler.StopRunningJob(id);
        return Results.Ok(new { stopRequested = stopped });
    });

    app.MapPost("/api/bwlimit", async (BandwidthUpdateRequest req, JobScheduler scheduler, CancellationToken ct) =>
    {
        var message = await scheduler.UpdateBandwidthLimitAsync(req.Rate, ct);
        return Results.Ok(new { rate = string.IsNullOrWhiteSpace(req.Rate) ? "off" : req.Rate.Trim(), message });
    });

    app.MapPost("/api/rcd/toggle", async (
        JobScheduler scheduler,
        RcloneHybridEngine engine,
        AppConfigStore store,
        CancellationToken ct) =>
    {
        var cfg = scheduler.GetConfigSnapshot();
        if (engine.IsRcDaemonRunning)
        {
            await engine.StopRcDaemonAsync();
            cfg.AutoStartRcdDaemon = false;
            store.Save(cfg);
            return Results.Ok(new { running = false, port = 0 });
        }
        else
        {
            var started = await engine.StartRcDaemonAsync(cfg, ct);
            cfg.AutoStartRcdDaemon = true;
            store.Save(cfg);
            return Results.Ok(new { running = started, port = engine.RcDaemonPort });
        }
    });

    app.MapGet("/api/remotes", async (JobScheduler scheduler, RcloneHybridEngine engine, CancellationToken ct) =>
    {
        try
        {
            var cfg = scheduler.GetConfigSnapshot();
            var (remotes, sourceUsed) = await engine.ListRemotesHybridAsync(cfg, ct);
            return Results.Ok(new { remotes, sourceUsed });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    });

    app.MapGet("/api/about", async (string remote, JobScheduler scheduler, RcloneHybridEngine engine, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(remote))
            return Results.BadRequest(new { error = "Informe o parâmetro remote (ex: gdrive:)." });

        var cfg = scheduler.GetConfigSnapshot();
        var about = await engine.GetRemoteAboutAsync(cfg, remote, ct);
        return about != null ? Results.Ok(about) : Results.NotFound(new { error = "Não foi possível obter informações de espaço do remote." });
    });

    app.MapGet("/api/logs", (int? maxLines, JobScheduler scheduler, ServiceLogBuffer log) =>
    {
        var limit = Math.Clamp(maxLines ?? 200, 10, 2000);
        var cfg = scheduler.GetConfigSnapshot();
        var jobLogPath = cfg.Jobs.Select(j => j.LogFilePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "rclone_backup.log";
        var fullRcloneLogPath = Path.IsPathRooted(jobLogPath) ? jobLogPath : Path.Combine(cfg.WorkingDirectory, jobLogPath);

        return Results.Ok(new
        {
            serviceLogs = log.GetRecent(limit),
            rcloneLogLines = ServiceControlHelper.ReadTailLines(fullRcloneLogPath, limit),
            rcloneLogPath = fullRcloneLogPath
        });
    });

    app.MapGet("/api/filters/analyze", (string? path, JobScheduler scheduler) =>
    {
        var cfg = scheduler.GetConfigSnapshot();
        var targetPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(cfg.WorkingDirectory, "exclude_user_folder.txt")
            : (Path.IsPathRooted(path) ? path : Path.Combine(cfg.WorkingDirectory, path));

        var analysis = FilterFileHelper.Analyze(targetPath);
        var content = File.Exists(targetPath) ? File.ReadAllText(targetPath) : string.Empty;
        return Results.Ok(new { analysis, content });
    });

    app.MapPost("/api/filters/save", (FilterFilePayload payload, JobScheduler scheduler) =>
    {
        var cfg = scheduler.GetConfigSnapshot();
        var targetPath = Path.IsPathRooted(payload.FilePath)
            ? payload.FilePath
            : Path.Combine(cfg.WorkingDirectory, payload.FilePath);

        File.WriteAllText(targetPath, payload.Content ?? string.Empty, new UTF8Encoding(false));
        var analysis = FilterFileHelper.Analyze(targetPath);
        return Results.Ok(new { saved = true, analysis });
    });

    app.MapPost("/api/filters/optimize", (FilterFilePayload payload, JobScheduler scheduler) =>
    {
        var cfg = scheduler.GetConfigSnapshot();
        var targetPath = Path.IsPathRooted(payload.FilePath)
            ? payload.FilePath
            : Path.Combine(cfg.WorkingDirectory, payload.FilePath);

        if (!File.Exists(targetPath))
            return Results.NotFound(new { error = $"Arquivo '{targetPath}' não encontrado." });

        var analysis = FilterFileHelper.ApplyOptimizationWithBackup(targetPath);
        var content = File.ReadAllText(targetPath);
        return Results.Ok(new { optimized = true, analysis, content });
    });

    app.MapPost("/api/rclone/stop-rcd", async (RcloneHybridEngine engine) =>
    {
        if (engine.IsRcDaemonRunning)
            await engine.StopRcDaemonAsync();
        return Results.Ok(new { running = engine.IsRcDaemonRunning });
    });

    app.MapPost("/api/rclone/release-config", async (
        UpdateReleaseConfigRequest req,
        JobScheduler scheduler,
        RcloneHybridEngine engine,
        CancellationToken ct) =>
    {
        try
        {
            var normalized = RcloneReleaseManager.NormalizeVersion(req.PinnedRcloneVersion);
            if (engine.IsRcDaemonRunning)
                await engine.StopRcDaemonAsync();

            var updated = scheduler.UpdateReleaseSettings(normalized, req.LockRcloneVersion, req.RcloneExecutablePath);
            if (updated.AutoStartRcdDaemon && File.Exists(updated.RcloneExecutablePath))
                await engine.StartRcDaemonAsync(updated, ct);

            var ver = await engine.GetRcloneVersionAsync(updated, ct);
            return Results.Ok(new
            {
                updated.PinnedRcloneVersion,
                updated.LockRcloneVersion,
                updated.RcloneExecutablePath,
                rcloneVersion = ver,
                rcDaemonRunning = engine.IsRcDaemonRunning
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    app.MapPost("/api/service/startup", (bool enabled, JobScheduler scheduler) =>
    {
        var cfg = scheduler.GetConfigSnapshot();
        ServiceControlHelper.SetStartupEnabled(enabled, cfg.WorkingDirectory);
        return Results.Ok(new { startupWithWindowsEnabled = ServiceControlHelper.IsStartupEnabled() });
    });

    app.MapPost("/api/service/shutdown", (IHostApplicationLifetime lifetime, ServiceLogBuffer log) =>
    {
        log.Warn("Encerramento solicitado via API.");
        Task.Run(async () =>
        {
            await Task.Delay(250);
            lifetime.StopApplication();
        });
        return Results.Ok(new { shuttingDown = true });
    });

    return app;
}

if (isWindowsService)
{
    WindowsServiceHost.RunWithStopCallback(ServiceControlHelper.WindowsServiceName, async registerStop =>
    {
        var app = BuildApp(args, workDir);
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        registerStop(() => lifetime.StopApplication());
        await app.RunAsync();
    });
}
else
{
    var app = BuildApp(args, workDir);
    await app.RunAsync();
}

return 0;
