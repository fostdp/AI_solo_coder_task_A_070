using AluminaDetection.Api.Models;
using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Client;
using System.Text.Json;

namespace AluminaDetection.Api.Services;

public class MqttPublishService : IMqttPublishService, IDisposable
{
    private readonly ILogger<MqttPublishService> _logger;
    private readonly IConfiguration _configuration;
    private IMqttClient? _mqttClient;
    private bool _connected;

    public MqttPublishService(
        ILogger<MqttPublishService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public async Task ConnectAsync()
    {
        if (_connected && _mqttClient?.IsConnected == true)
            return;

        try
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();

            var broker = _configuration["Mqtt:Broker"] ?? "localhost";
            var port = int.Parse(_configuration["Mqtt:Port"] ?? "1883");

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(broker, port)
                .WithClientId($"AluminaDetection-{Environment.MachineName}")
                .WithCleanSession(true)
                .Build();

            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogWarning("MQTT断开连接: {Reason}", e.Reason);
                _connected = false;
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    await _mqttClient.ConnectAsync(options);
                    _connected = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MQTT重连失败");
                }
            };

            await _mqttClient.ConnectAsync(options);
            _connected = true;
            _logger.LogInformation("MQTT已连接到 {Broker}:{Port}", broker, port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT连接失败，将在后台重试");
            _connected = false;
        }
    }

    public async Task PublishAlarmAsync(AlarmRecord alarm)
    {
        await EnsureConnectedAsync();

        if (!_connected) return;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                alarm.Id,
                alarm.PotId,
                alarm.AlarmType,
                alarm.AlarmLevel,
                alarm.Message,
                alarm.CreatedAt
            });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"aluminum/alarm/{alarm.PotId}")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient!.PublishAsync(message);
            _logger.LogDebug("告警已推送MQTT: PotId={PotId}, Type={Type}", alarm.PotId, alarm.AlarmType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT告警推送失败");
        }
    }

    public async Task PublishPotStatusAsync(int potId, PotStatusDto status)
    {
        await EnsureConnectedAsync();

        if (!_connected) return;

        try
        {
            var payload = JsonSerializer.Serialize(status);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"aluminum/status/{potId}")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            await _mqttClient!.PublishAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT状态推送失败: PotId={PotId}", potId);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_mqttClient?.IsConnected == true)
        {
            await _mqttClient.DisconnectAsync();
        }
        _connected = false;
    }

    private async Task EnsureConnectedAsync()
    {
        if (!_connected || _mqttClient?.IsConnected != true)
        {
            await ConnectAsync();
        }
    }

    public void Dispose()
    {
        _mqttClient?.Dispose();
    }
}
