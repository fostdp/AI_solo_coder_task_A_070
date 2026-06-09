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

    public PotDataProcessingHostedService(
        IServiceProvider serviceProvider,
        IHubContext<PotMonitorHub> hubContext,
        ILogger<PotDataProcessingHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PotDataProcessingHostedService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PushAllPotStatusAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PotDataProcessingHostedService.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("PotDataProcessingHostedService stopped.");
    }

    private async Task PushAllPotStatusAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IZigBeeReceiver>();

        try
        {
            var statuses = await receiver.GetAllPotsStatusAsync();
            await _hubContext.Clients.All.SendAsync("PotStatusUpdated", statuses, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pushing pot status via SignalR.");
        }
    }
}
