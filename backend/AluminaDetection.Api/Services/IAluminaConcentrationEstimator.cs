using AluminaDetection.Api.Models;

namespace AluminaDetection.Api.Services;

public interface IAluminaConcentrationEstimator
{
    Task TrainModelAsync();
    Task<double> EstimateAsync(int potId);
    Task<ModelTrainingResult> RetrainModelAsync();
}
