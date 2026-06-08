using AluminaDetection.Api.Data;
using AluminaDetection.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public class FeedingControlService : IFeedingControlService
{
    private readonly AppDbContext _db;
    private readonly ILogger<FeedingControlService> _logger;

    public FeedingControlService(AppDbContext db, ILogger<FeedingControlService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<FeedingRecord> TriggerFeedingAsync(int potId, double amount, string feedType = "Manual", string operatorName = "System")
    {
        var record = new FeedingRecord
        {
            PotId = potId,
            FeedAmount = Math.Round(amount, 2),
            FeedType = feedType,
            FeedTime = DateTime.UtcNow,
            Operator = operatorName,
            Status = "Pending"
        };

        _db.FeedingRecords.Add(record);
        await _db.SaveChangesAsync();

        _logger.LogInformation("下料记录: PotId={PotId}, Amount={Amount}kg, Type={Type}", potId, amount, feedType);
        return record;
    }

    public async Task<FeedingRecord?> AutoFeedIfNeededAsync(int potId, double estimatedConcentration)
    {
        const double lowThreshold = 1.8;
        if (estimatedConcentration >= lowThreshold)
            return null;

        double amount = (2.5 - estimatedConcentration) * 2.0;
        amount = Math.Max(amount, 1.0);

        var recentAutoFeed = await _db.FeedingRecords
            .Where(fr => fr.PotId == potId && fr.FeedType == "Auto" && fr.Status != "Cancelled")
            .OrderByDescending(fr => fr.FeedTime)
            .FirstOrDefaultAsync();

        if (recentAutoFeed != null && (DateTime.UtcNow - recentAutoFeed.FeedTime).TotalMinutes < 5)
            return null;

        var record = new FeedingRecord
        {
            PotId = potId,
            FeedAmount = Math.Round(amount, 2),
            FeedType = "Auto",
            FeedTime = DateTime.UtcNow,
            Operator = "System-AutoFeed",
            EstimatedConcentration = Math.Round(estimatedConcentration, 4),
            Status = "Pending"
        };

        _db.FeedingRecords.Add(record);
        await _db.SaveChangesAsync();

        _logger.LogWarning("自动下料触发: PotId={PotId}, 浓度={Conc:F2}%, 下料量={Amount}kg",
            potId, estimatedConcentration, amount);
        return record;
    }

    public async Task<List<FeedingDto>> GetRecentFeedingsAsync(int potId, int count)
    {
        return await _db.FeedingRecords
            .Where(fr => fr.PotId == potId)
            .OrderByDescending(fr => fr.FeedTime)
            .Take(count)
            .Select(fr => new FeedingDto
            {
                Id = fr.Id,
                FeedAmount = fr.FeedAmount,
                FeedType = fr.FeedType,
                FeedTime = fr.FeedTime,
                Operator = fr.Operator
            })
            .ToListAsync();
    }
}
