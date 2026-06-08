using AluminaDetection.Api.Models;

namespace AluminaDetection.Api.Services;

public interface IVoltageFeatureExtractor
{
    Task<VoltageFeature> ExtractFeaturesAsync(int potId, int windowMinutes);
}
