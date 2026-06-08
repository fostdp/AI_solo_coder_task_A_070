using AluminaDetection.Api.Models;
using Microsoft.AspNetCore.SignalR;

namespace AluminaDetection.Api.Hubs;

public class PotMonitorHub : Hub
{
    private readonly ILogger<PotMonitorHub> _logger;

    public PotMonitorHub(ILogger<PotMonitorHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinPotGroup(int potId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"pot-{potId}");
        _logger.LogInformation("Client {ConnectionId} joined group for Pot {PotId}", Context.ConnectionId, potId);
    }

    public async Task LeavePotGroup(int potId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"pot-{potId}");
        _logger.LogInformation("Client {ConnectionId} left group for Pot {PotId}", Context.ConnectionId, potId);
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

public static class PotMonitorHubExtensions
{
    public static async Task SendPotStatusUpdated(this IHubContext<PotMonitorHub> hubContext, List<PotStatusDto> statuses)
    {
        await hubContext.Clients.All.SendAsync("PotStatusUpdated", statuses);
    }

    public static async Task SendAlarmTriggered(this IHubContext<PotMonitorHub> hubContext, AlarmNotificationDto alarm)
    {
        await hubContext.Clients.All.SendAsync("AlarmTriggered", alarm);
    }

    public static async Task SendAnodeEffectWarning(this IHubContext<PotMonitorHub> hubContext, int potId, AnodeEffectWarningDto warning)
    {
        await hubContext.Clients.Group($"pot-{potId}").SendAsync("AnodeEffectWarning", warning);
    }
}
