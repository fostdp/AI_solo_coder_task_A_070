using AluminaDetection.Api.Models;

namespace AluminaDetection.Api.Services;

public interface IMqttPublishService
{
    Task ConnectAsync();
    Task PublishAlarmAsync(AlarmRecord alarm);
    Task PublishPotStatusAsync(int potId, PotStatusDto status);
    Task DisconnectAsync();
}
