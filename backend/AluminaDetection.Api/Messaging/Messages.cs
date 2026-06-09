using AluminaDetection.Api.Models;
using MediatR;

namespace AluminaDetection.Api.Messaging;

public class ZigBeeDataReceivedCommand : IRequest<Unit>
{
    public ZigBeeDataDto Data { get; init; } = null!;
}

public class FeaturesExtractedEvent : INotification
{
    public int PotId { get; init; }
    public VoltageFeature Feature { get; init; } = null!;
}

public class ConcentrationEstimatedEvent : INotification
{
    public int PotId { get; init; }
    public double EstimatedConcentration { get; init; }
    public double RawVoltage { get; init; }
}

public class AnodeEffectPredictedEvent : INotification
{
    public int PotId { get; init; }
    public double Probability { get; init; }
}

public class AlarmTriggeredEvent : INotification
{
    public long AlarmId { get; init; }
    public int PotId { get; init; }
    public string AlarmType { get; init; } = string.Empty;
    public int AlarmLevel { get; init; }
    public string Message { get; init; } = string.Empty;
}

public class FeedingRequiredCommand : IRequest<Unit>
{
    public int PotId { get; init; }
    public double EstimatedConcentration { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public class EffectQuenchCommand : IRequest<Unit>
{
    public int PotId { get; init; }
    public double Probability { get; init; }
}
