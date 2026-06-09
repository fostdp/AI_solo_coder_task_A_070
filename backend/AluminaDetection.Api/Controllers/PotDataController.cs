using AluminaDetection.Api.Data;
using AluminaDetection.Api.Models;
using AluminaDetection.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Controllers;

[ApiController]
[Route("api/potdata")]
public class PotDataController : ControllerBase
{
    private readonly IZigBeeReceiver _zigBeeReceiver;
    private readonly IAlarmController _alarmController;
    private readonly AppDbContext _db;
    private readonly ILogger<PotDataController> _logger;

    public PotDataController(
        IZigBeeReceiver zigBeeReceiver,
        IAlarmController alarmController,
        AppDbContext db,
        ILogger<PotDataController> logger)
    {
        _zigBeeReceiver = zigBeeReceiver;
        _alarmController = alarmController;
        _db = db;
        _logger = logger;
    }

    [HttpPost("zigbee")]
    public async Task<IActionResult> ReceiveZigBeeData([FromBody] ZigBeeDataDto data)
    {
        if (data == null)
            return BadRequest("Invalid data payload.");

        _logger.LogInformation("Received ZigBee data for Pot {PotId}", data.PotId);

        await _zigBeeReceiver.ReceiveAsync(data);

        return Ok(new { message = "Data received and processing." });
    }

    [HttpGet("status")]
    public async Task<ActionResult<List<PotStatusDto>>> GetAllPotsStatus()
    {
        var statuses = await _zigBeeReceiver.GetAllPotsStatusAsync();
        return Ok(statuses);
    }

    [HttpGet("{potId}/trend")]
    public async Task<ActionResult<List<TrendDataDto>>> GetPotTrend(int potId)
    {
        var cutoff = DateTime.UtcNow.AddHours(-8);
        var trend = await _db.PotRealtimeData
            .Where(r => r.PotId == potId && r.RecordedAt >= cutoff)
            .OrderBy(r => r.RecordedAt)
            .Select(r => new TrendDataDto
            {
                RecordedAt = r.RecordedAt,
                Voltage = r.Voltage,
                CurrentDistribution = r.AnodeCurrentDistribution
            })
            .ToListAsync();
        return Ok(trend);
    }

    [HttpGet("{potId}/feedings")]
    public async Task<IActionResult> GetRecentFeedings(int potId)
    {
        var feedings = await _db.FeedingRecords
            .Where(fr => fr.PotId == potId)
            .OrderByDescending(fr => fr.FeedTime)
            .Take(10)
            .Select(fr => new FeedingDto
            {
                Id = fr.Id, FeedAmount = fr.FeedAmount, FeedType = fr.FeedType,
                FeedTime = fr.FeedTime, Operator = fr.Operator
            })
            .ToListAsync();
        return Ok(feedings);
    }

    [HttpPost("{potId}/feed")]
    public async Task<IActionResult> ManualFeed(int potId, [FromBody] ControlCommandDto command)
    {
        if (command == null)
            return BadRequest("Invalid command payload.");

        _logger.LogInformation("Manual feeding command received for Pot {PotId}", potId);

        double amount = 2.0;
        if (double.TryParse(command.Parameter, out double parsedAmount))
            amount = parsedAmount;

        var record = new FeedingRecord
        {
            PotId = potId, FeedAmount = Math.Round(amount, 2), FeedType = "Manual",
            FeedTime = DateTime.UtcNow, Operator = command.CommandType ?? "Operator", Status = "Pending"
        };

        _db.FeedingRecords.Add(record);
        await _db.SaveChangesAsync();

        return Ok(new { message = $"Manual feeding command sent to Pot {potId}." });
    }

    [HttpGet("alarms")]
    public async Task<IActionResult> GetActiveAlarms()
    {
        var alarms = await _alarmController.GetActiveAlarmsAsync();
        return Ok(alarms);
    }

    [HttpPost("alarms/{id}/handle")]
    public async Task<IActionResult> HandleAlarm(long id, [FromBody] HandleAlarmRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.HandlerName))
            return BadRequest("Handler name is required.");

        var result = await _alarmController.HandleAlarmAsync(id, request.HandlerName);

        if (!result)
            return NotFound($"Alarm with ID {id} not found or already handled.");

        return Ok(new { message = $"Alarm {id} handled by {request.HandlerName}." });
    }
}

public class HandleAlarmRequest
{
    public string HandlerName { get; set; } = string.Empty;
}
