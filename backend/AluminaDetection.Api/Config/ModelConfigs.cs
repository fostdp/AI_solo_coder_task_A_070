namespace AluminaDetection.Api.Config;

public class SvrConfig
{
    public double C { get; set; } = 1.0;
    public double Epsilon { get; set; } = 0.1;
    public double Gamma { get; set; } = 0.5;
    public int MaxTrainingSamples { get; set; } = 2000;
    public double MatchingTimeToleranceMinutes { get; set; } = 2.0;
    public int MinTrainingSamples { get; set; } = 5;
    public double OutputMin { get; set; } = 1.5;
    public double OutputMax { get; set; } = 4.0;
    public double SmoTolerance { get; set; } = 1e-3;
    public int SmoMaxPasses { get; set; } = 100;
    public double SupportVectorThreshold { get; set; } = 1e-8;
    public List<string> FeatureNames { get; set; } = new()
    {
        "MeanVoltage", "StdVoltage", "Skewness", "Kurtosis",
        "FrequencyPeak", "NoisePower",
        "DominantFrequencyAmplitude", "SpectralEnergyRatio",
        "HighFrequencyNoiseRatio", "SpectralCentroid", "SpectralBandwidth"
    };
    public HeuristicWeights Heuristic { get; set; } = new();

    public class HeuristicWeights
    {
        public double Base { get; set; } = 3.0;
        public double StdVoltageFactor { get; set; } = -0.5;
        public double StdVoltageScale { get; set; } = 0.1;
        public double SkewnessFactor { get; set; } = -0.3;
        public double SkewnessScale { get; set; } = 0.5;
        public double HighFreqNoiseFactor { get; set; } = -0.4;
        public double SpectralEnergyFactor { get; set; } = 0.2;
    }
}

public class RfConfig
{
    public int TreeCount { get; set; } = 10;
    public int MaxDepth { get; set; } = 5;
    public int MinSamplesLeaf { get; set; } = 5;
    public int MaxTrainingSamples { get; set; } = 5000;
    public int MinTrainingSamples { get; set; } = 10;
    public int RandomSeed { get; set; } = 42;
    public double EffectTimeWindowMinutes { get; set; } = 3.0;
    public double MinAccuracyForProduction { get; set; } = 0.70;
    public double AccuracyDropThreshold { get; set; } = 0.15;
    public int EvaluationWindowSize { get; set; } = 50;
    public double TrainingIntervalHours { get; set; } = 2.0;
    public List<string> FeatureNames { get; set; } = new()
    {
        "NoisePower", "StdVoltage", "FrequencyPeak", "MeanVoltage",
        "DominantFrequencyAmplitude", "HighFrequencyNoiseRatio",
        "SpectralEnergyRatio", "SpectralBandwidth"
    };
    public HeuristicWeights Heuristic { get; set; } = new();

    public class HeuristicWeights
    {
        public double NoisePowerWeight { get; set; } = 2.0;
        public double NoisePowerOffset { get; set; } = 0.5;
        public double StdVoltageWeight { get; set; } = 1.5;
        public double StdVoltageOffset { get; set; } = 0.3;
        public double FrequencyPeakWeight { get; set; } = 0.5;
        public double HighFreqNoiseWeight { get; set; } = 3.0;
    }
}

public class AlarmConfig
{
    public double ConcentrationAlarmThreshold { get; set; } = 1.5;
    public int ConcentrationAlarmDurationMinutes { get; set; } = 5;
    public int ConcentrationAlarmDedupMinutes { get; set; } = 10;
    public double AnodeEffectProbabilityThreshold { get; set; } = 0.8;
    public int AnodeEffectAlarmDedupMinutes { get; set; } = 5;
    public double FeedingTriggerConcentration { get; set; } = 1.8;
    public double FeedingTargetConcentration { get; set; } = 2.5;
    public double FeedingMinAmountKg { get; set; } = 1.0;
    public int FeedingCooldownMinutes { get; set; } = 5;
    public double EffectQuenchPrimaryFeedKg { get; set; } = 3.0;
    public double EffectQuenchSecondaryFeedKg { get; set; } = 2.0;
}
