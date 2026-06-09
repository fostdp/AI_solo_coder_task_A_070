using AluminaDetection.Api.Data;
using AluminaDetection.Api.Hubs;
using AluminaDetection.Api.Messaging;
using AluminaDetection.Api.Models;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public interface IZigBeeReceiver
{
    Task ReceiveAsync(ZigBeeDataDto data);
    Task<List<PotStatusDto>> GetAllPotsStatusAsync();
    PotStatusDto? GetPotStatus(int potId);
}

public class ZigBeeReceiver : IZigBeeReceiver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediator _mediator;
    private readonly ILogger<ZigBeeReceiver> _logger;

    private static readonly ConcurrentDictionary<int, ReaderWriterLockSlim> _potLocks = new();
    private static readonly ConcurrentDictionary<int, PotDataBuffer> _potBuffers = new();

    private sealed class PotDataBuffer
    {
        public double Voltage;
        public double PotTemperature;
        public double BathTemperature;
        public double AluminumLevel;
        public double BathLevel;
        public string AnodeCurrentDistribution = string.Empty;
        public double EstimatedConcentration;
        public double AnodeEffectProbability;
        public DateTime LastUpdateTime;
    }

    private static ReaderWriterLockSlim GetPotLock(int potId) =>
        _potLocks.GetOrAdd(potId, _ => new ReaderWriterLockSlim());

    private static PotDataBuffer GetPotBuffer(int potId) =>
        _potBuffers.GetOrAdd(potId, _ => new PotDataBuffer
        {
            Voltage = 4.2, PotTemperature = 960, BathTemperature = 965,
            AluminumLevel = 25, BathLevel = 22, AnodeCurrentDistribution = "[]",
            EstimatedConcentration = 3.0, AnodeEffectProbability = 0.0,
            LastUpdateTime = DateTime.MinValue
        });

    public ZigBeeReceiver(IServiceScopeFactory scopeFactory, IMediator mediator, ILogger<ZigBeeReceiver> logger)
    {
        _scopeFactory = scopeFactory;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task ReceiveAsync(ZigBeeDataDto data)
    {
        var potLock = GetPotLock(data.PotId);
        var buffer = GetPotBuffer(data.PotId);

        potLock.EnterWriteLock();
        try
        {
            buffer.Voltage = data.Voltage;
            buffer.AnodeCurrentDistribution = data.AnodeCurrentDistribution;
            buffer.PotTemperature = data.PotTemperature;
            buffer.BathTemperature = data.BathTemperature;
            buffer.AluminumLevel = data.AluminumLevel;
            buffer.BathLevel = data.BathLevel;
            buffer.LastUpdateTime = DateTime.UtcNow;
        }
        finally
        {
            potLock.ExitWriteLock();
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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

        db.PotRealtimeData.Add(realtimeData);
        await db.SaveChangesAsync();

        await _mediator.Send(new ZigBeeDataReceivedCommand { Data = data });
    }

    public async Task<List<PotStatusDto>> GetAllPotsStatusAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pots = await db.PotInfos.OrderBy(p => p.RowIndex).ThenBy(p => p.ColIndex).ToListAsync();
        var result = new List<PotStatusDto>();
        foreach (var pot in pots)
        {
            var status = GetPotStatus(pot.PotId);
            if (status != null)
            {
                status.PotCode = pot.PotCode;
                status.RowIndex = pot.RowIndex;
                status.ColIndex = pot.ColIndex;
                status.Status = pot.Status;
                result.Add(status);
            }
        }
        return result;
    }

    public PotStatusDto? GetPotStatus(int potId)
    {
        var buffer = GetPotBuffer(potId);
        var potLock = GetPotLock(potId);

        double voltage, potTemp, bathTemp, alLevel, bathLevel, estConc, effectProb;
        DateTime lastUpdate;

        potLock.EnterReadLock();
        try
        {
            voltage = buffer.Voltage;
            potTemp = buffer.PotTemperature;
            bathTemp = buffer.BathTemperature;
            alLevel = buffer.AluminumLevel;
            bathLevel = buffer.BathLevel;
            estConc = buffer.EstimatedConcentration;
            effectProb = buffer.AnodeEffectProbability;
            lastUpdate = buffer.LastUpdateTime;
        }
        finally
        {
            potLock.ExitReadLock();
        }

        return new PotStatusDto
        {
            PotId = potId,
            EstimatedConcentration = estConc,
            AluminaConcentration = estConc,
            AnodeEffectProbability = effectProb,
            LastVoltage = voltage,
            PotTemperature = potTemp,
            BathTemperature = bathTemp,
            AluminumLevel = alLevel,
            BathLevel = bathLevel,
            LastUpdateTime = lastUpdate
        };
    }

    public static void UpdateBufferEstimated(int potId, double concentration, double effectProb)
    {
        var potLock = GetPotLock(potId);
        var buffer = GetPotBuffer(potId);
        potLock.EnterWriteLock();
        try
        {
            buffer.EstimatedConcentration = concentration;
            buffer.AnodeEffectProbability = effectProb;
        }
        finally
        {
            potLock.ExitWriteLock();
        }
    }
}

public class ZigBeeDataReceivedHandler : IRequestHandler<ZigBeeDataReceivedCommand, Unit>
{
    private static readonly ConcurrentDictionary<int, DateTime> _lastFeatureExtraction = new();
    private readonly IVoltageFeatureExtractor _featureExtractor;
    private readonly IConcentrationEstimator _concentrationEstimator;
    private readonly IAnodeEffectPredictorService _anodeEffectPredictor;
    private readonly IMediator _mediator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ZigBeeDataReceivedHandler> _logger;

    public ZigBeeDataReceivedHandler(
        IVoltageFeatureExtractor featureExtractor,
        IConcentrationEstimator concentrationEstimator,
        IAnodeEffectPredictorService anodeEffectPredictor,
        IMediator mediator,
        IConfiguration configuration,
        ILogger<ZigBeeDataReceivedHandler> logger)
    {
        _featureExtractor = featureExtractor;
        _concentrationEstimator = concentrationEstimator;
        _anodeEffectPredictor = anodeEffectPredictor;
        _mediator = mediator;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Unit> Handle(ZigBeeDataReceivedCommand request, CancellationToken cancellationToken)
    {
        var data = request.Data;
        var featureWindowMinutes = int.Parse(_configuration["Processing:FeatureWindowMinutes"] ?? "5");

        _lastFeatureExtraction.TryGetValue(data.PotId, out var lastExtraction);
        if ((DateTime.UtcNow - lastExtraction).TotalMinutes < 1)
            return Unit.Value;

        try
        {
            var feature = await _featureExtractor.ExtractFeaturesAsync(data.PotId, featureWindowMinutes);
            _lastFeatureExtraction[data.PotId] = DateTime.UtcNow;

            await _mediator.Publish(new FeaturesExtractedEvent { PotId = data.PotId, Feature = feature }, cancellationToken);

            var estimatedConc = await _concentrationEstimator.EstimateAsync(data.PotId);
            var effectProb = await _anodeEffectPredictor.PredictAsync(data.PotId);

            ZigBeeReceiver.UpdateBufferEstimated(data.PotId, estimatedConc, effectProb);

            await _mediator.Publish(new ConcentrationEstimatedEvent
            {
                PotId = data.PotId,
                EstimatedConcentration = estimatedConc,
                RawVoltage = data.Voltage
            }, cancellationToken);

            await _mediator.Publish(new AnodeEffectPredictedEvent
            {
                PotId = data.PotId,
                Probability = effectProb
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediatR pipeline处理异常: PotId={PotId}", data.PotId);
        }

        return Unit.Value;
    }
}
