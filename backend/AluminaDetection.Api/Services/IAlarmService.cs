using AluminaDetection.Api.Models;

namespace AluminaDetection.Api.Services;

public interface IAlarmService
{
    Task CheckConcentrationAlarmAsync(int potId, double currentConcentration);
    Task CheckAnodeEffectAlarmAsync(int potId, double probability);
    Task TriggerEffectQuenchingAsync(int potId);
    Task<List<AlarmDto>> GetActiveAlarmsAsync();
    Task<bool> HandleAlarmAsync(long alarmId, string handler);
    Task CreateLowConcentrationAlarmAsync(int potId, double concentration);
}
