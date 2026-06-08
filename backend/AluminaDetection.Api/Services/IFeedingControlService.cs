using AluminaDetection.Api.Models;

namespace AluminaDetection.Api.Services;

public interface IFeedingControlService
{
    Task<FeedingRecord> TriggerFeedingAsync(int potId, double amount, string feedType = "Manual", string operatorName = "System");
    Task<FeedingRecord?> AutoFeedIfNeededAsync(int potId, double estimatedConcentration);
    Task<List<FeedingDto>> GetRecentFeedingsAsync(int potId, int count);
}
