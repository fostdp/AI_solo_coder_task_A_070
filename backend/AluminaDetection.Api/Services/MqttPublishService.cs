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

    private enum MessagePriority
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3
    }

    private sealed class PrioritizedMessage
    {
        public required MessagePriority Priority { get; init; }
        public required MqttApplicationMessage Message { get; init; }
        public DateTime EnqueuedAt { get; init; } = DateTime.UtcNow;
    }

    private sealed class TokenBucketRateLimiter
    {
        private readonly int _maxTokens;
        private readonly double _refillRatePerSecond;
        private double _currentTokens;
        private DateTime _lastRefillTime;
        private readonly object _lock = new();

        public TokenBucketRateLimiter(int maxTokens, double refillRatePerSecond)
        {
            _maxTokens = maxTokens;
            _refillRatePerSecond = refillRatePerSecond;
            _currentTokens = maxTokens;
            _lastRefillTime = DateTime.UtcNow;
        }

        public bool TryConsume()
        {
            lock (_lock)
            {
                Refill();

                if (_currentTokens >= 1)
                {
                    _currentTokens -= 1;
                    return true;
                }
                return false;
            }
        }

        public async Task WaitAsync(CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                if (TryConsume())
                    return;

                await Task.Delay(50, ct);
            }
        }

        private void Refill()
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - _lastRefillTime).TotalSeconds;
            _currentTokens = Math.Min(_maxTokens, _currentTokens + elapsed * _refillRatePerSecond);
            _lastRefillTime = now;
        }
    }

    private readonly PriorityQueue<PrioritizedMessage, int> _messageQueue = new();
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly SemaphoreSlim _queueSignal = new(0);
    private Task? _dispatchTask;
    private CancellationTokenSource? _dispatchCts;
    private bool _disposed;

    public MqttPublishService(
        ILogger<MqttPublishService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        int maxBurst = int.Parse(configuration["Mqtt:MaxBurst"] ?? "20");
        double refillRate = double.Parse(configuration["Mqtt:RefillRatePerSecond"] ?? "10");
        _rateLimiter = new TokenBucketRateLimiter(maxBurst, refillRate);
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

            StartDispatchLoop();

            _logger.LogInformation("MQTT已连接到 {Broker}:{Port} (限流: {Burst}条突发, {Rate}条/秒)", broker, port,
                _configuration["Mqtt:MaxBurst"] ?? "20",
                _configuration["Mqtt:RefillRatePerSecond"] ?? "10");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT连接失败，将在后台重试");
            _connected = false;
        }
    }

    public async Task PublishAlarmAsync(AlarmRecord alarm)
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

        var priority = alarm.AlarmLevel == 2 ? MessagePriority.Critical : MessagePriority.High;

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"aluminum/alarm/{alarm.PotId}")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        EnqueueMessage(priority, message);
        await Task.CompletedTask;
    }

    public async Task PublishPotStatusAsync(int potId, PotStatusDto status)
    {
        var payload = JsonSerializer.Serialize(status);

        var priority = status.AnodeEffectProbability > 0.8
            ? MessagePriority.High
            : MessagePriority.Normal;

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"aluminum/status/{potId}")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        EnqueueMessage(priority, message);
        await Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        StopDispatchLoop();

        if (_mqttClient?.IsConnected == true)
        {
            await _mqttClient.DisconnectAsync();
        }
        _connected = false;
    }

    private void EnqueueMessage(MessagePriority priority, MqttApplicationMessage message)
    {
        var item = new PrioritizedMessage
        {
            Priority = priority,
            Message = message
        };

        int sortKey = (int)priority * 10000 - (int)(DateTime.UtcNow - DateTime.MinValue).TotalMilliseconds;
        lock (_messageQueue)
        {
            _messageQueue.Enqueue(item, sortKey);
        }

        _queueSignal.Release();

        var queueDepth = _messageQueue.Count;
        if (queueDepth > 100)
        {
            _logger.LogWarning("MQTT消息队列深度={Depth}，可能存在积压", queueDepth);
        }
    }

    private void StartDispatchLoop()
    {
        if (_dispatchTask != null) return;

        _dispatchCts = new CancellationTokenSource();
        _dispatchTask = Task.Run(() => DispatchLoopAsync(_dispatchCts.Token));
    }

    private void StopDispatchLoop()
    {
        _dispatchCts?.Cancel();
        _dispatchTask = null;
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("MQTT消息调度循环已启动");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(ct);

                if (!_connected || _mqttClient?.IsConnected != true)
                {
                    await EnsureConnectedAsync();
                    if (!_connected) continue;
                }

                PrioritizedMessage? item = null;
                lock (_messageQueue)
                {
                    if (_messageQueue.Count > 0)
                        _messageQueue.TryDequeue(out item);
                }

                if (item == null) continue;

                await _rateLimiter.WaitAsync(ct);

                try
                {
                    await _mqttClient!.PublishAsync(item.Message, ct);
                    _logger.LogDebug("MQTT消息已发送: Topic={Topic}, Priority={Priority}",
                        item.Message.Topic, item.Priority);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MQTT消息发送失败: Topic={Topic}", item.Message.Topic);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT调度循环异常");
                await Task.Delay(100, ct);
            }
        }

        _logger.LogInformation("MQTT消息调度循环已停止");
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
        if (_disposed) return;
        _disposed = true;

        StopDispatchLoop();
        _mqttClient?.Dispose();
        _queueSignal.Dispose();
        _dispatchCts?.Dispose();
    }
}
