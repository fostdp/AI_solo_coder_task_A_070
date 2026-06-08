using AluminaDetection.Api.Models;
using AluminaDetection.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AluminaDetection.Api.Controllers;

[ApiController]
[Route("api/potdata")]
public class PotDataController : ControllerBase
{
    private readonly IPotDataProcessor _potDataProcessor;
    private readonly IFeedingControlService _feedingControlService;
    private readonly IAlarmService _alarmService;
    private readonly ILogger<PotDataController> _logger;

    public PotDataController(
        IPotDataProcessor potDataProcessor,
        IFeedingControlService feedingControlService,
        IAlarmService alarmService,
        ILogger<PotDataController> logger)
    {
        _potDataProcessor = potDataProcessor;
        _feedingControlService = feedingControlService;
        _alarmService = alarmService;
        _logger = logger;
    }

    [HttpPost("zigbee")]
    public async Task<IActionResult> ReceiveZigBeeData([FromBody] ZigBeeDataDto data)
    {
        if (data == null)
            return BadRequest("Invalid data payload.");

        _logger.LogInformation("Received ZigBee data for Pot {PotId}", data.PotId);

        await _potDataProcessor.ProcessIncomingDataAsync(data);

        return Ok(new { message = "Data received and processed." });
    }

    [HttpGet("status")]
    public async Task<ActionResult<List<PotStatusDto>>> GetAllPotsStatus()
    {
        var statuses = await _potDataProcessor.GetAllPotsStatusAsync();
        return Ok(statuses);
    }

    [HttpGet("{potId}/trend")]
    public async Task<ActionResult<List<TrendDataDto>>> GetPotTrend(int potId)
    {
        var trend = await _potDataProcessor.GetPotTrendAsync(potId, TimeSpan.FromHours(8));
        return Ok(trend);
    }

    [HttpGet("{potId}/feedings")]
    public async Task<IActionResult> GetRecentFeedings(int potId)
    {
        var feedings = await _feedingControlService.GetRecentFeedingsAsync(potId, 10);
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

        await _feedingControlService.TriggerFeedingAsync(potId, amount, "Manual", command.CommandType ?? "Operator");

        return Ok(new { message = $"Manual feeding command sent to Pot {potId}." });
    }

    [HttpGet("alarms")]
    public async Task<IActionResult> GetActiveAlarms()
    {
        var alarms = await _alarmService.GetActiveAlarmsAsync();
        return Ok(alarms);
    }

    [HttpPost("alarms/{id}/handle")]
    public async Task<IActionResult> HandleAlarm(long id, [FromBody] HandleAlarmRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.HandlerName))
            return BadRequest("Handler name is required.");

        var result = await _alarmService.HandleAlarmAsync(id, request.HandlerName);

        if (!result)
            return NotFound($"Alarm with ID {id} not found or already handled.");

        return Ok(new { message = $"Alarm {id} handled by {request.HandlerName}." });
    }
}

public class HandleAlarmRequest
{
    public string HandlerName { get; set; } = string.Empty;
}
