namespace AluminaDetection.Api.Models;

public class ZigBeeDataDto
{
    public int PotId { get; set; }
    public double Voltage { get; set; }
    public string AnodeCurrentDistribution { get; set; } = string.Empty;
    public double PotTemperature { get; set; }
    public double BathTemperature { get; set; }
    public double AluminumLevel { get; set; }
    public double BathLevel { get; set; }
}

public class PotStatusDto
{
    public int PotId { get; set; }
    public string PotCode { get; set; } = string.Empty;
    public int RowIndex { get; set; }
    public int ColIndex { get; set; }
    public double AluminaConcentration { get; set; }
    public double EstimatedConcentration { get; set; }
    public double AnodeEffectProbability { get; set; }
    public double LastVoltage { get; set; }
    public double PotTemperature { get; set; }
    public double BathTemperature { get; set; }
    public double AluminumLevel { get; set; }
    public double BathLevel { get; set; }
    public int Status { get; set; }
    public DateTime LastUpdateTime { get; set; }
}

public class TrendDataDto
{
    public DateTime RecordedAt { get; set; }
    public double Voltage { get; set; }
    public string CurrentDistribution { get; set; } = string.Empty;
}

public class FeedingDto
{
    public long Id { get; set; }
    public double FeedAmount { get; set; }
    public string FeedType { get; set; } = string.Empty;
    public DateTime FeedTime { get; set; }
    public string Operator { get; set; } = string.Empty;
}

public class AlarmDto
{
    public long Id { get; set; }
    public int PotId { get; set; }
    public string PotCode { get; set; } = string.Empty;
    public string AlarmType { get; set; } = string.Empty;
    public int AlarmLevel { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsHandled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ControlCommandDto
{
    public int PotId { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
}

public class AlarmNotificationDto
{
    public long Id { get; set; }
    public int PotId { get; set; }
    public string PotCode { get; set; } = string.Empty;
    public string AlarmType { get; set; } = string.Empty;
    public int AlarmLevel { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class AnodeEffectWarningDto
{
    public int PotId { get; set; }
    public double Probability { get; set; }
    public string Recommendation { get; set; } = string.Empty;
}

public class ModelTrainingResult
{
    public int SampleCount { get; set; }
    public double Metric { get; set; }
    public long TrainingDurationMs { get; set; }
}
