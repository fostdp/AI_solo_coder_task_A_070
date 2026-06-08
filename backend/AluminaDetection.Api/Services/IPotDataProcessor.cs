using AluminaDetection.Api.Models;

namespace AluminaDetection.Api.Services;

public interface IPotDataProcessor
{
    Task ProcessIncomingDataAsync(ZigBeeDataDto data);
    Task<List<PotStatusDto>> GetAllPotsStatusAsync();
    Task<List<TrendDataDto>> GetPotTrendAsync(int potId, TimeSpan period);
}
