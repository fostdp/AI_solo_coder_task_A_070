using System.Collections.Concurrent;
using AluminaDetection.Api.Hubs;
using AluminaDetection.Api.Models;
using AluminaDetection.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace AluminaDetection.Api.Services;

public class PotDataProcessingHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<PotMonitorHub> _hubContext;
    private readonly ILogger<PotDataProcessingHostedService> _logger;
    private readonly ConcurrentQueue<ZigBeeDataDto> _dataQueue = new();
    private DateTime _lastConcentrationCheck = DateTime.MinValue;

    public PotDataProcessingHostedService(
        IServiceProvider serviceProvider,
        IHubContext<PotMonitorHub> hubContext,
        ILogger<PotDataProcessingHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

    public void EnqueueData(ZigBeeDataDto data)
    {
        _dataQueue.Enqueue(data);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PotDataProcessingHostedService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueuedDataAsync(stoppingToken);
                await RunPeriodicConcentrationCheckAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PotDataProcessingHostedService execution loop.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("PotDataProcessingHostedService stopped.");
    }

    private async Task ProcessQueuedDataAsync(CancellationToken stoppingToken)
    {
        if (_dataQueue.IsEmpty)
            return;

        var processedCount = 0;
        using var scope = _serviceProvider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IPotDataProcessor>();

        while (_dataQueue.TryDequeue(out var data) && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.ProcessIncomingDataAsync(data);
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing ZigBee data for Pot {PotId}", data.PotId);
            }
        }

        if (processedCount > 0)
        {
            _logger.LogDebug("Processed {Count} data items from queue.", processedCount);

            try
            {
                var statuses = await processor.GetAllPotsStatusAsync();
                await _hubContext.Clients.All.SendAsync("PotStatusUpdated", statuses);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pushing pot status updates via SignalR.");
            }
        }
    }

    private async Task RunPeriodicConcentrationCheckAsync(CancellationToken stoppingToken)
    {
        if (DateTime.UtcNow - _lastConcentrationCheck < TimeSpan.FromMinutes(5))
            return;

        _lastConcentrationCheck = DateTime.UtcNow;
        _logger.LogInformation("Running periodic concentration check and anode effect prediction.");

        using var scope = _serviceProvider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IPotDataProcessor>();
        var concentrationEstimator = scope.ServiceProvider.GetRequiredService<IAluminaConcentrationEstimator>();
        var anodeEffectPredictor = scope.ServiceProvider.GetRequiredService<IAnodeEffectPredictor>();
        var alarmService = scope.ServiceProvider.GetRequiredService<IAlarmService>();

        try
        {
            var allStatuses = await processor.GetAllPotsStatusAsync();

            foreach (var pot in allStatuses)
            {
                try
                {
                    var concentration = await concentrationEstimator.EstimateAsync(pot.PotId);

                    if (concentration < 1.5)
                    {
                        await alarmService.CreateLowConcentrationAlarmAsync(pot.PotId, concentration);
                    }

                    var probability = await anodeEffectPredictor.PredictAsync(pot.PotId);

                    if (probability > 0.8)
                    {
                        await _hubContext.Clients.Group($"pot-{pot.PotId}").SendAsync("AnodeEffectWarning",
                            new AnodeEffectWarningDto
                            {
                                PotId = pot.PotId,
                                Probability = probability,
                                Recommendation = "建议立即下料并提升极距"
                            });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during concentration/anode effect check for Pot {PotId}", pot.PotId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during periodic concentration check.");
        }
    }
}
