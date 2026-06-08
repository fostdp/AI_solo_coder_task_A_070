using AluminaDetection.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AluminaDetection.Api.Services;

public class ModelTrainingHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ModelTrainingHostedService> _logger;
    private DateTime _lastForcedRetrain = DateTime.MinValue;

    public ModelTrainingHostedService(
        IServiceProvider serviceProvider,
        ILogger<ModelTrainingHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ModelTrainingHostedService started. Periodic retraining + drift monitoring active.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await MonitorAndRetrainAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during model monitoring/retraining cycle.");
            }
        }

        _logger.LogInformation("ModelTrainingHostedService stopped.");
    }

    private async Task MonitorAndRetrainAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var concentrationEstimator = scope.ServiceProvider.GetRequiredService<IAluminaConcentrationEstimator>();
        var anodeEffectPredictor = scope.ServiceProvider.GetRequiredService<IAnodeEffectPredictor>();

        bool rfNeedsRetrain = await anodeEffectPredictor.CheckAndAutoRetrainIfNeededAsync();
        if (rfNeedsRetrain)
        {
            _logger.LogInformation("RF模型已通过自动重训练逻辑完成重训练. 当前准确率: {Accuracy:P1}, 上次训练: {LastTrain}",
                anodeEffectPredictor.GetCurrentAccuracy(),
                anodeEffectPredictor.GetLastTrainingTime());
        }

        if ((DateTime.UtcNow - _lastForcedRetrain).TotalHours >= 1)
        {
            _logger.LogInformation("执行定时重训练周期 at {Time}", DateTime.UtcNow);

            try
            {
                var svrResult = await concentrationEstimator.RetrainModelAsync();
                _logger.LogInformation(
                    "SVR model retrained. Support vectors: {SampleCount}, Metric: {Metric:F4}, Duration: {Duration}ms",
                    svrResult.SampleCount, svrResult.Metric, svrResult.TrainingDurationMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrain SVR model.");
            }

            try
            {
                if (!rfNeedsRetrain)
                {
                    var rfResult = await anodeEffectPredictor.RetrainModelAsync();
                    _logger.LogInformation(
                        "RF model retrained. Trees: {SampleCount}, Accuracy: {Metric:P1}, Duration: {Duration}ms",
                        rfResult.SampleCount, rfResult.Metric, rfResult.TrainingDurationMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrain RF model.");
            }

            _lastForcedRetrain = DateTime.UtcNow;
        }

        _logger.LogDebug(
            "模型监控状态 - RF准确率: {RfAccuracy:P1}, RF上次训练: {RfLastTrain}, SVR/RF定时训练上次: {LastForced}",
            anodeEffectPredictor.GetCurrentAccuracy(),
            anodeEffectPredictor.GetLastTrainingTime(),
            _lastForcedRetrain);
    }
}
