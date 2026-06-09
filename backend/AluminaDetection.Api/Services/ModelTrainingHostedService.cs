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

    public ModelTrainingHostedService(IServiceProvider serviceProvider, ILogger<ModelTrainingHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ModelTrainingHostedService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

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
        var concentrationEstimator = scope.ServiceProvider.GetRequiredService<IConcentrationEstimator>();
        var anodeEffectPredictor = scope.ServiceProvider.GetRequiredService<IAnodeEffectPredictorService>();

        bool rfNeedsRetrain = await anodeEffectPredictor.CheckAndAutoRetrainIfNeededAsync();
        if (rfNeedsRetrain)
        {
            _logger.LogInformation("RF自动重训练完成. 准确率: {Accuracy:P1}, 上次训练: {LastTrain}",
                anodeEffectPredictor.GetCurrentAccuracy(), anodeEffectPredictor.GetLastTrainingTime());
        }

        if ((DateTime.UtcNow - _lastForcedRetrain).TotalHours >= 1)
        {
            _logger.LogInformation("执行定时重训练 at {Time}", DateTime.UtcNow);

            try
            {
                var svrResult = await concentrationEstimator.RetrainModelAsync();
                _logger.LogInformation("SVR retrained. SV: {Count}, Metric: {Metric:F4}, Duration: {Ms}ms",
                    svrResult.SampleCount, svrResult.Metric, svrResult.TrainingDurationMs);
            }
            catch (Exception ex) { _logger.LogError(ex, "SVR retrain failed."); }

            try
            {
                if (!rfNeedsRetrain)
                {
                    var rfResult = await anodeEffectPredictor.RetrainModelAsync();
                    _logger.LogInformation("RF retrained. Trees: {Count}, Accuracy: {Metric:P1}, Duration: {Ms}ms",
                        rfResult.SampleCount, rfResult.Metric, rfResult.TrainingDurationMs);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "RF retrain failed."); }

            _lastForcedRetrain = DateTime.UtcNow;
        }
    }
}
