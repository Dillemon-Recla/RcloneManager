using Microsoft.Extensions.Hosting;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Logging;
using RcloneManager.Core.Scheduling;

namespace RcloneManager.Service;

public sealed class RcloneBackgroundWorker : BackgroundService
{
    private readonly JobScheduler _scheduler;
    private readonly RcloneHybridEngine _engine;
    private readonly ServiceLogBuffer _log;

    public RcloneBackgroundWorker(JobScheduler scheduler, RcloneHybridEngine engine, ServiceLogBuffer log)
    {
        _scheduler = scheduler;
        _engine = engine;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = _scheduler.GetConfigSnapshot();
        _log.Info($"Serviço RcloneManager iniciado. Pasta de trabalho: {cfg.WorkingDirectory}");

        if (cfg.AutoStartRcdDaemon)
        {
            await _engine.StartRcDaemonAsync(cfg, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _scheduler.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"Erro no loop do agendador: {ex.Message}");
            }

            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.Info("Encerrando Serviço RcloneManager...");
        await _engine.DisposeAsync();
    }
}
