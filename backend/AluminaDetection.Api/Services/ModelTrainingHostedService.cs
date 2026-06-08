using AluminaDetection.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AluminaDetection.Api.Services;

public class ModelTrainingHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ModelTrainingHostedService> _logger;

    public ModelTrainingHostedService(
        IServiceProvider serviceProvider,
        ILogger<ModelTrainingHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ModelTrainingHostedService started. Training will occur every hour.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RetrainModelsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during model retraining.");
            }
        }

        _logger.LogInformation("ModelTrainingHostedService stopped.");
    }

    private async Task RetrainModelsAsync()
    {
        _logger.LogInformation("Starting model retraining at {Time}", DateTime.UtcNow);

        using var scope = _serviceProvider.CreateScope();
        var concentrationEstimator = scope.ServiceProvider.GetRequiredService<IAluminaConcentrationEstimator>();
        var anodeEffectPredictor = scope.ServiceProvider.GetRequiredService<IAnodeEffectPredictor>();

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
            var rfResult = await anodeEffectPredictor.RetrainModelAsync();
            _logger.LogInformation(
                "Random Forest model retrained. Trees: {SampleCount}, Metric: {Metric:F4}, Duration: {Duration}ms",
                rfResult.SampleCount, rfResult.Metric, rfResult.TrainingDurationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrain Random Forest model.");
        }

        _logger.LogInformation("Model retraining completed at {Time}", DateTime.UtcNow);
    }
}
