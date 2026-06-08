using AluminaDetection.Api.Data;
using AluminaDetection.Api.Hubs;
using AluminaDetection.Api.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public class PotDataProcessor : IPotDataProcessor
{
    private readonly AppDbContext _db;
    private readonly IVoltageFeatureExtractor _featureExtractor;
    private readonly IAluminaConcentrationEstimator _concentrationEstimator;
    private readonly IAnodeEffectPredictor _anodeEffectPredictor;
    private readonly IFeedingControlService _feedingControlService;
    private readonly IAlarmService _alarmService;
    private readonly IMqttPublishService _mqttPublishService;
    private readonly IHubContext<PotMonitorHub> _hubContext;
    private readonly ILogger<PotDataProcessor> _logger;
    private readonly IConfiguration _configuration;

    private static readonly ConcurrentDictionary<int, DateTime> _lastFeatureExtraction = new();

    public PotDataProcessor(
        AppDbContext db,
        IVoltageFeatureExtractor featureExtractor,
        IAluminaConcentrationEstimator concentrationEstimator,
        IAnodeEffectPredictor anodeEffectPredictor,
        IFeedingControlService feedingControlService,
        IAlarmService alarmService,
        IMqttPublishService mqttPublishService,
        IHubContext<PotMonitorHub> hubContext,
        ILogger<PotDataProcessor> logger,
        IConfiguration configuration)
    {
        _db = db;
        _featureExtractor = featureExtractor;
        _concentrationEstimator = concentrationEstimator;
        _anodeEffectPredictor = anodeEffectPredictor;
        _feedingControlService = feedingControlService;
        _alarmService = alarmService;
        _mqttPublishService = mqttPublishService;
        _hubContext = hubContext;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task ProcessIncomingDataAsync(ZigBeeDataDto data)
    {
        var realtimeData = new PotRealtimeData
        {
            PotId = data.PotId,
            Voltage = data.Voltage,
            AnodeCurrentDistribution = data.AnodeCurrentDistribution,
            PotTemperature = data.PotTemperature,
            BathTemperature = data.BathTemperature,
            AluminumLevel = data.AluminumLevel,
            BathLevel = data.BathLevel,
            RecordedAt = DateTime.UtcNow
        };

        _db.PotRealtimeData.Add(realtimeData);
        await _db.SaveChangesAsync();

        var featureWindowMinutes = int.Parse(_configuration["Processing:FeatureWindowMinutes"] ?? "5");

        _lastFeatureExtraction.TryGetValue(data.PotId, out var lastExtraction);
        if ((DateTime.UtcNow - lastExtraction).TotalMinutes >= 1)
        {
            try
            {
                var feature = await _featureExtractor.ExtractFeaturesAsync(data.PotId, featureWindowMinutes);
                _lastFeatureExtraction[data.PotId] = DateTime.UtcNow;

                var estimatedConc = await _concentrationEstimator.EstimateAsync(data.PotId);
                var effectProb = await _anodeEffectPredictor.PredictAsync(data.PotId);

                var latestData = await _db.PotRealtimeData
                    .Where(r => r.PotId == data.PotId)
                    .OrderByDescending(r => r.RecordedAt)
                    .FirstOrDefaultAsync();

                if (latestData != null)
                {
                    latestData.EstimatedConcentration = estimatedConc;
                    latestData.AnodeEffectProbability = effectProb;
                    _db.PotRealtimeData.Update(latestData);
                }

                var concHistory = new ConcentrationHistory
                {
                    PotId = data.PotId,
                    Concentration = estimatedConc,
                    Source = "SVR",
                    RecordedAt = DateTime.UtcNow
                };
                _db.ConcentrationHistories.Add(concHistory);
                await _db.SaveChangesAsync();

                var feedingThreshold = double.Parse(_configuration["Processing:FeedingThreshold"] ?? "1.8");
                if (estimatedConc < feedingThreshold)
                {
                    await _feedingControlService.AutoFeedIfNeededAsync(data.PotId, estimatedConc);
                }

                var effectThreshold = double.Parse(_configuration["Processing:AnodeEffectProbabilityThreshold"] ?? "0.8");
                await _alarmService.CheckConcentrationAlarmAsync(data.PotId, estimatedConc);
                await _alarmService.CheckAnodeEffectAlarmAsync(data.PotId, effectProb);

                if (effectProb > effectThreshold)
                {
                    var pot = await _db.PotInfos.FindAsync(data.PotId);
                    await _hubContext.Clients.Group($"pot-{data.PotId}").SendAsync("AnodeEffectWarning",
                        new AnodeEffectWarningDto
                        {
                            PotId = data.PotId,
                            Probability = effectProb,
                            Recommendation = "建议立即下料并提升极距"
                        });
                }

                try
                {
                    await _mqttPublishService.ConnectAsync();
                    var status = await GetPotStatusAsync(data.PotId);
                    if (status != null)
                        await _mqttPublishService.PublishPotStatusAsync(data.PotId, status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MQTT推送状态失败: PotId={PotId}", data.PotId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "特征提取/浓度估计/效应预测处理异常: PotId={PotId}", data.PotId);
            }
        }
    }

    public async Task<List<PotStatusDto>> GetAllPotsStatusAsync()
    {
        var pots = await _db.PotInfos.OrderBy(p => p.RowIndex).ThenBy(p => p.ColIndex).ToListAsync();
        var result = new List<PotStatusDto>();

        foreach (var pot in pots)
        {
            var status = await GetPotStatusAsync(pot.PotId);
            if (status != null)
                result.Add(status);
        }

        return result;
    }

    public async Task<List<TrendDataDto>> GetPotTrendAsync(int potId, TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        return await _db.PotRealtimeData
            .Where(r => r.PotId == potId && r.RecordedAt >= cutoff)
            .OrderBy(r => r.RecordedAt)
            .Select(r => new TrendDataDto
            {
                RecordedAt = r.RecordedAt,
                Voltage = r.Voltage,
                CurrentDistribution = r.AnodeCurrentDistribution
            })
            .ToListAsync();
    }

    private async Task<PotStatusDto?> GetPotStatusAsync(int potId)
    {
        var pot = await _db.PotInfos.FindAsync(potId);
        if (pot == null) return null;

        var latestData = await _db.PotRealtimeData
            .Where(r => r.PotId == potId)
            .OrderByDescending(r => r.RecordedAt)
            .FirstOrDefaultAsync();

        return new PotStatusDto
        {
            PotId = pot.PotId,
            PotCode = pot.PotCode,
            RowIndex = pot.RowIndex,
            ColIndex = pot.ColIndex,
            AluminaConcentration = latestData?.AluminaConcentration ?? 0,
            EstimatedConcentration = latestData?.EstimatedConcentration ?? 0,
            AnodeEffectProbability = latestData?.AnodeEffectProbability ?? 0,
            LastVoltage = latestData?.Voltage ?? 0,
            PotTemperature = latestData?.PotTemperature ?? 0,
            BathTemperature = latestData?.BathTemperature ?? 0,
            AluminumLevel = latestData?.AluminumLevel ?? 0,
            BathLevel = latestData?.BathLevel ?? 0,
            Status = pot.Status,
            LastUpdateTime = latestData?.RecordedAt ?? DateTime.MinValue
        };
    }
}
