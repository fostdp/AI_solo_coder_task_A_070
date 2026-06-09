using AluminaDetection.Api.Config;
using AluminaDetection.Api.Data;
using AluminaDetection.Api.Hubs;
using AluminaDetection.Api.Messaging;
using AluminaDetection.Api.Models;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public interface IAlarmController
{
    Task<List<AlarmDto>> GetActiveAlarmsAsync();
    Task<bool> HandleAlarmAsync(long alarmId, string handler);
}

public class AlarmControllerService : IAlarmController,
    INotificationHandler<ConcentrationEstimatedEvent>,
    INotificationHandler<AnodeEffectPredictedEvent>,
    IRequestHandler<EffectQuenchCommand, Unit>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AlarmConfig _alarmConfig;
    private readonly IMqttPublishService _mqttPublishService;
    private readonly IHubContext<PotMonitorHub> _hubContext;
    private readonly IMediator _mediator;
    private readonly ILogger<AlarmControllerService> _logger;

    public AlarmControllerService(
        IServiceScopeFactory scopeFactory,
        AlarmConfig alarmConfig,
        IMqttPublishService mqttPublishService,
        IHubContext<PotMonitorHub> hubContext,
        IMediator mediator,
        ILogger<AlarmControllerService> logger)
    {
        _scopeFactory = scopeFactory;
        _alarmConfig = alarmConfig;
        _mqttPublishService = mqttPublishService;
        _hubContext = hubContext;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task Handle(ConcentrationEstimatedEvent notification, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conc = notification.EstimatedConcentration;
        if (conc >= _alarmConfig.ConcentrationAlarmThreshold)
            return;

        var cutoff = DateTime.UtcNow.AddMinutes(-_alarmConfig.ConcentrationAlarmDurationMinutes);
        var lowRecords = await db.ConcentrationHistories
            .Where(ch => ch.PotId == notification.PotId && ch.Concentration < _alarmConfig.ConcentrationAlarmThreshold && ch.RecordedAt >= cutoff)
            .OrderBy(ch => ch.RecordedAt).ToListAsync();

        if (lowRecords.Count < 2) return;

        var earliest = lowRecords.First().RecordedAt;
        var latest = lowRecords.Last().RecordedAt;
        if ((latest - earliest).TotalMinutes < _alarmConfig.ConcentrationAlarmDurationMinutes) return;

        var exists = await db.AlarmRecords.AnyAsync(a => a.PotId == notification.PotId
            && a.AlarmType == "LowConcentration" && a.AlarmLevel == 1 && !a.IsHandled
            && a.CreatedAt >= DateTime.UtcNow.AddMinutes(-_alarmConfig.ConcentrationAlarmDedupMinutes), cancellationToken);

        if (exists) return;

        var pot = await db.PotInfos.FindAsync(notification.PotId);
        var alarm = new AlarmRecord
        {
            PotId = notification.PotId, AlarmType = "LowConcentration", AlarmLevel = 1,
            Message = $"电解槽{pot?.PotCode ?? notification.PotId.ToString()} 氧化铝浓度低于{_alarmConfig.ConcentrationAlarmThreshold}%已持续{_alarmConfig.ConcentrationAlarmDurationMinutes}分钟，当前: {conc:F2}%",
            IsHandled = false, CreatedAt = DateTime.UtcNow
        };

        db.AlarmRecords.Add(alarm);
        await db.SaveChangesAsync(cancellationToken);

        await PublishAlarmAndNotifyAsync(alarm);
        _logger.LogWarning("一级浓度告警: PotId={PotId}, 浓度={Conc:F2}%", notification.PotId, conc);
    }

    public async Task Handle(AnodeEffectPredictedEvent notification, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (notification.Probability <= _alarmConfig.AnodeEffectProbabilityThreshold)
            return;

        var exists = await db.AlarmRecords.AnyAsync(a => a.PotId == notification.PotId
            && a.AlarmType == "AnodeEffect" && a.AlarmLevel == 2 && !a.IsHandled
            && a.CreatedAt >= DateTime.UtcNow.AddMinutes(-_alarmConfig.AnodeEffectAlarmDedupMinutes), cancellationToken);

        if (exists) return;

        var pot = await db.PotInfos.FindAsync(notification.PotId);
        var alarm = new AlarmRecord
        {
            PotId = notification.PotId, AlarmType = "AnodeEffect", AlarmLevel = 2,
            Message = $"电解槽{pot?.PotCode ?? notification.PotId.ToString()} 阳极效应概率{notification.Probability:P0}，即将执行效应熄灭程序",
            IsHandled = false, CreatedAt = DateTime.UtcNow
        };

        db.AlarmRecords.Add(alarm);
        await db.SaveChangesAsync(cancellationToken);

        await PublishAlarmAndNotifyAsync(alarm);
        _logger.LogError("二级效应告警: PotId={PotId}, 概率={Prob:P0}", notification.PotId, notification.Probability);

        await _mediator.Send(new EffectQuenchCommand { PotId = notification.PotId, Probability = notification.Probability }, cancellationToken);
    }

    public async Task<Unit> Handle(EffectQuenchCommand request, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation("执行阳极效应熄灭程序: PotId={PotId}", request.PotId);

        db.FeedingRecords.Add(new FeedingRecord
        {
            PotId = request.PotId, FeedAmount = _alarmConfig.EffectQuenchPrimaryFeedKg,
            FeedType = "AutoQuench", FeedTime = DateTime.UtcNow, Operator = "System-AEQuench", Status = "Pending"
        });
        db.FeedingRecords.Add(new FeedingRecord
        {
            PotId = request.PotId, FeedAmount = _alarmConfig.EffectQuenchSecondaryFeedKg,
            FeedType = "AutoQuench", FeedTime = DateTime.UtcNow, Operator = "System-AEQuench", Status = "Pending"
        });

        var quenchAlarm = new AlarmRecord
        {
            PotId = request.PotId, AlarmType = "EffectQuench", AlarmLevel = 2,
            Message = $"电解槽{request.PotId} 已自动执行阳极效应熄灭程序（双次下料{_alarmConfig.EffectQuenchPrimaryFeedKg + _alarmConfig.EffectQuenchSecondaryFeedKg}kg）",
            IsHandled = true, HandledAt = DateTime.UtcNow, HandledBy = "System-AEQuench", CreatedAt = DateTime.UtcNow
        };

        db.AlarmRecords.Add(quenchAlarm);
        await db.SaveChangesAsync(cancellationToken);

        await PublishAlarmAndNotifyAsync(quenchAlarm);
        return Unit.Value;
    }

    private async Task PublishAlarmAndNotifyAsync(AlarmRecord alarm)
    {
        try { await _mqttPublishService.ConnectAsync(); await _mqttPublishService.PublishAlarmAsync(alarm); }
        catch (Exception ex) { _logger.LogWarning(ex, "MQTT推送告警失败: AlarmId={Id}", alarm.Id); }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var pot = await db.PotInfos.FindAsync(alarm.PotId);
            await _hubContext.Clients.All.SendAsync("AlarmTriggered", new AlarmNotificationDto
            {
                Id = alarm.Id, PotId = alarm.PotId, PotCode = pot?.PotCode ?? "",
                AlarmType = alarm.AlarmType, AlarmLevel = alarm.AlarmLevel,
                Message = alarm.Message, CreatedAt = alarm.CreatedAt
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SignalR推送告警失败"); }

        if (alarm.AlarmType == "AnodeEffect" || alarm.AlarmType == "EffectQuench")
        {
            try
            {
                await _hubContext.Clients.Group($"pot-{alarm.PotId}").SendAsync("AnodeEffectWarning",
                    new AnodeEffectWarningDto { PotId = alarm.PotId, Probability = 0.9, Recommendation = "效应熄灭程序已执行" });
            }
            catch (Exception ex) { _logger.LogWarning(ex, "SignalR效应预警推送失败"); }
        }
    }

    public async Task<List<AlarmDto>> GetActiveAlarmsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.AlarmRecords
            .Where(a => !a.IsHandled).OrderByDescending(a => a.AlarmLevel).ThenByDescending(a => a.CreatedAt)
            .Join(db.PotInfos, a => a.PotId, p => p.PotId, (a, p) => new AlarmDto
            {
                Id = a.Id, PotId = a.PotId, PotCode = p.PotCode, AlarmType = a.AlarmType,
                AlarmLevel = a.AlarmLevel, Message = a.Message, IsHandled = a.IsHandled, CreatedAt = a.CreatedAt
            }).Take(100).ToListAsync();
    }

    public async Task<bool> HandleAlarmAsync(long alarmId, string handler)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var alarm = await db.AlarmRecords.FindAsync(alarmId);
        if (alarm == null || alarm.IsHandled) return false;
        alarm.IsHandled = true; alarm.HandledAt = DateTime.UtcNow; alarm.HandledBy = handler;
        await db.SaveChangesAsync();
        return true;
    }
}
