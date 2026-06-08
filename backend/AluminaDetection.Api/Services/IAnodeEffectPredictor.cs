using AluminaDetection.Api.Models;

namespace AluminaDetection.Api.Services;

public interface IAnodeEffectPredictor
{
    Task<double> PredictAsync(int potId);
    Task TrainModelAsync();
    Task<ModelTrainingResult> RetrainModelAsync();
    Task<bool> CheckAndAutoRetrainIfNeededAsync();
    double GetCurrentAccuracy();
    DateTime GetLastTrainingTime();
}
