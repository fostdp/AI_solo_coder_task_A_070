using AluminaDetection.Api.Data;
using AluminaDetection.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public class AlarmService : IAlarmService
{
    private readonly AppDbContext _db;
    private readonly IFeedingControlService _feedingControlService;
    private readonly ILogger<AlarmService> _logger;

    public AlarmService(
        AppDbContext db,
        IFeedingControlService feedingControlService,
        ILogger<AlarmService> logger)
    {
        _db = db;
        _feedingControlService = feedingControlService;
        _logger = logger;
    }

    public async Task CheckConcentrationAlarmAsync(int potId, double currentConcentration)
    {
        const double threshold = 1.5;
        const int durationMinutes = 5;

        if (currentConcentration >= threshold)
            return;

        var cutoff = DateTime.UtcNow.AddMinutes(-durationMinutes);
        var lowConcentrationRecords = await _db.ConcentrationHistories
            .Where(ch => ch.PotId == potId
                && ch.Concentration < threshold
                && ch.RecordedAt >= cutoff)
            .OrderBy(ch => ch.RecordedAt)
            .ToListAsync();

        if (lowConcentrationRecords.Count < 2)
            return;

        var earliest = lowConcentrationRecords.First().RecordedAt;
        var latest = lowConcentrationRecords.Last().RecordedAt;

        if ((latest - earliest).TotalMinutes < durationMinutes)
            return;

        var existingAlarm = await _db.AlarmRecords
            .AnyAsync(a => a.PotId == potId
                && a.AlarmType == "LowConcentration"
                && a.AlarmLevel == 1
                && !a.IsHandled
                && a.CreatedAt >= DateTime.UtcNow.AddMinutes(-10));

        if (existingAlarm)
            return;

        var pot = await _db.PotInfos.FindAsync(potId);
        var alarm = new AlarmRecord
        {
            PotId = potId,
            AlarmType = "LowConcentration",
            AlarmLevel = 1,
            Message = $"电解槽{pot?.PotCode ?? potId.ToString()} 氧化铝浓度低于1.5%已持续{durationMinutes}分钟，当前浓度: {currentConcentration:F2}%",
            IsHandled = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.AlarmRecords.Add(alarm);
        await _db.SaveChangesAsync();

        _logger.LogWarning("一级浓度告警: PotId={PotId}, 浓度={Concentration:F2}%", potId, currentConcentration);
    }

    public async Task CheckAnodeEffectAlarmAsync(int potId, double probability)
    {
        if (probability <= 0.8)
            return;

        var existingAlarm = await _db.AlarmRecords
            .AnyAsync(a => a.PotId == potId
                && a.AlarmType == "AnodeEffect"
                && a.AlarmLevel == 2
                && !a.IsHandled
                && a.CreatedAt >= DateTime.UtcNow.AddMinutes(-5));

        if (existingAlarm)
            return;

        var pot = await _db.PotInfos.FindAsync(potId);
        var alarm = new AlarmRecord
        {
            PotId = potId,
            AlarmType = "AnodeEffect",
            AlarmLevel = 2,
            Message = $"电解槽{pot?.PotCode ?? potId.ToString()} 阳极效应概率{probability:P0}，即将执行效应熄灭程序",
            IsHandled = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.AlarmRecords.Add(alarm);
        await _db.SaveChangesAsync();

        _logger.LogError("二级效应告警: PotId={PotId}, 阳极效应概率={Probability:P0}", potId, probability);

        await TriggerEffectQuenchingAsync(potId);
    }

    public async Task TriggerEffectQuenchingAsync(int potId)
    {
        _logger.LogInformation("执行阳极效应熄灭程序: PotId={PotId}", potId);

        await _feedingControlService.TriggerFeedingAsync(potId, 3.0, "AutoQuench", "System-AEQuench");
        await _feedingControlService.TriggerFeedingAsync(potId, 2.0, "AutoQuench", "System-AEQuench");

        var quenchAlarm = new AlarmRecord
        {
            PotId = potId,
            AlarmType = "EffectQuench",
            AlarmLevel = 2,
            Message = $"电解槽{potId} 已自动执行阳极效应熄灭程序（双次下料5.0kg）",
            IsHandled = true,
            HandledAt = DateTime.UtcNow,
            HandledBy = "System-AEQuench",
            CreatedAt = DateTime.UtcNow
        };

        _db.AlarmRecords.Add(quenchAlarm);
        await _db.SaveChangesAsync();
    }

    public async Task CreateLowConcentrationAlarmAsync(int potId, double concentration)
    {
        await CheckConcentrationAlarmAsync(potId, concentration);
    }

    public async Task<List<AlarmDto>> GetActiveAlarmsAsync()
    {
        return await _db.AlarmRecords
            .Where(a => !a.IsHandled)
            .OrderByDescending(a => a.AlarmLevel)
            .ThenByDescending(a => a.CreatedAt)
            .Join(_db.PotInfos,
                alarm => alarm.PotId,
                pot => pot.PotId,
                (alarm, pot) => new AlarmDto
                {
                    Id = alarm.Id,
                    PotId = alarm.PotId,
                    PotCode = pot.PotCode,
                    AlarmType = alarm.AlarmType,
                    AlarmLevel = alarm.AlarmLevel,
                    Message = alarm.Message,
                    IsHandled = alarm.IsHandled,
                    CreatedAt = alarm.CreatedAt
                })
            .Take(100)
            .ToListAsync();
    }

    public async Task<bool> HandleAlarmAsync(long alarmId, string handler)
    {
        var alarm = await _db.AlarmRecords.FindAsync(alarmId);
        if (alarm == null || alarm.IsHandled)
            return false;

        alarm.IsHandled = true;
        alarm.HandledAt = DateTime.UtcNow;
        alarm.HandledBy = handler;
        await _db.SaveChangesAsync();

        _logger.LogInformation("告警已处理: AlarmId={AlarmId}, Handler={Handler}", alarmId, handler);
        return true;
    }
}
