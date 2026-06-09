using AluminaDetection.Api.Config;
using AluminaDetection.Api.Data;
using AluminaDetection.Api.Messaging;
using AluminaDetection.Api.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public interface IAnodeEffectPredictorService
{
    Task<double> PredictAsync(int potId);
    Task TrainModelAsync();
    Task<ModelTrainingResult> RetrainModelAsync();
    Task<bool> CheckAndAutoRetrainIfNeededAsync();
    double GetCurrentAccuracy();
    DateTime GetLastTrainingTime();
}

public class AnodeEffectPredictorService : IAnodeEffectPredictorService,
    INotificationHandler<AnodeEffectPredictedEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RfConfig _rfConfig;
    private readonly IMediator _mediator;
    private readonly ILogger<AnodeEffectPredictorService> _logger;

    private List<DecisionTree>? _forest;
    private bool _isTrained;
    private DateTime _lastTrainingTime = DateTime.MinValue;
    private readonly ConcurrentQueue<PredictionRecord> _recentPredictions = new();
    private double _lastEvaluatedAccuracy = 1.0;
    private readonly object _modelLock = new();

    private sealed class PredictionRecord
    {
        public required int PotId { get; init; }
        public required double PredictedProbability { get; init; }
        public required DateTime PredictionTime { get; init; }
        public bool? ActualOutcome { get; set; }
    }

    public AnodeEffectPredictorService(
        IServiceScopeFactory scopeFactory, RfConfig rfConfig, IMediator mediator, ILogger<AnodeEffectPredictorService> logger)
    {
        _scopeFactory = scopeFactory;
        _rfConfig = rfConfig;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<double> PredictAsync(int potId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        lock (_modelLock)
        {
            if (!_isTrained || _forest == null || _forest.Count == 0)
                return Task.FromResult(0.0).Result;
        }

        var latestFeature = await db.VoltageFeatures
            .Where(f => f.PotId == potId)
            .OrderByDescending(f => f.ExtractedAt)
            .FirstOrDefaultAsync();

        if (latestFeature == null) return 0.0;

        double[] input = FeatureVector(latestFeature);
        double sum;

        lock (_modelLock)
        {
            sum = 0;
            foreach (var tree in _forest!) sum += tree.Predict(input);
        }

        double probability = sum / (_forest?.Count ?? 1);

        _recentPredictions.Enqueue(new PredictionRecord
        {
            PotId = potId, PredictedProbability = probability, PredictionTime = DateTime.UtcNow
        });
        TrimPredictionWindow();

        return Math.Clamp(probability, 0.0, 1.0);
    }

    public async Task Handle(AnodeEffectPredictedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.Probability > _rfConfig.MinAccuracyForProduction)
        {
            _logger.LogDebug("阳极效应预防检查: PotId={PotId}, 概率={Prob:P1}",
                notification.PotId, notification.Probability);
        }
    }

    public async Task TrainModelAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var features = await db.VoltageFeatures
            .OrderByDescending(f => f.ExtractedAt)
            .Take(_rfConfig.MaxTrainingSamples)
            .ToListAsync();

        var alarmEvents = await db.AlarmRecords
            .Where(a => a.AlarmType == "AnodeEffect")
            .ToDictionaryAsync(a => a.PotId, a => a.CreatedAt);

        var trainingData = new List<(double[] Input, int Label)>();
        foreach (var f in features)
        {
            bool effectOccurred = alarmEvents.TryGetValue(f.PotId, out var effectTime)
                && Math.Abs((f.ExtractedAt - effectTime).TotalMinutes) < _rfConfig.EffectTimeWindowMinutes;
            trainingData.Add((FeatureVector(f), effectOccurred ? 1 : 0));
        }

        if (trainingData.Count < _rfConfig.MinTrainingSamples)
        {
            _isTrained = false;
            return;
        }

        Random rng = new(_rfConfig.RandomSeed);
        var forest = new List<DecisionTree>();
        for (int t = 0; t < _rfConfig.TreeCount; t++)
        {
            var bootstrap = BootstrapSample(trainingData, rng);
            forest.Add(BuildTree(bootstrap.Select(p => p.Input).ToArray(),
                bootstrap.Select(p => p.Label).ToArray(), _rfConfig.MaxDepth, rng));
        }

        lock (_modelLock)
        {
            _forest = forest;
            _isTrained = true;
            _lastTrainingTime = DateTime.UtcNow;
        }
    }

    public async Task<ModelTrainingResult> RetrainModelAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await TrainModelAsync();
        sw.Stop();
        var accuracy = await EvaluateModelAccuracyAsync();
        _lastEvaluatedAccuracy = accuracy;
        return new ModelTrainingResult
        {
            SampleCount = _forest?.Count ?? 0, Metric = accuracy, TrainingDurationMs = sw.ElapsedMilliseconds
        };
    }

    public async Task<bool> CheckAndAutoRetrainIfNeededAsync()
    {
        var accuracy = await EvaluateModelAccuracyAsync();
        bool needsRetrain = accuracy < _rfConfig.MinAccuracyForProduction
            || (_lastEvaluatedAccuracy > 0 && accuracy < _lastEvaluatedAccuracy - _rfConfig.AccuracyDropThreshold)
            || (_isTrained && (DateTime.UtcNow - _lastTrainingTime).TotalHours > _rfConfig.TrainingIntervalHours);

        _lastEvaluatedAccuracy = accuracy;
        if (needsRetrain) { await RetrainModelAsync(); return true; }
        return false;
    }

    public double GetCurrentAccuracy() => _lastEvaluatedAccuracy;
    public DateTime GetLastTrainingTime() => _lastTrainingTime;

    private async Task<double> EvaluateModelAccuracyAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var recentAlarms = await db.AlarmRecords
            .Where(a => a.AlarmType == "AnodeEffect" && a.CreatedAt >= DateTime.UtcNow.AddHours(-1))
            .ToListAsync();
        var resolved = new List<(double Predicted, bool Actual)>();
        foreach (var pred in _recentPredictions)
        {
            bool actual = recentAlarms.Any(a => a.PotId == pred.PotId &&
                Math.Abs((a.CreatedAt - pred.PredictionTime).TotalMinutes) < _rfConfig.EffectTimeWindowMinutes);
            resolved.Add((pred.PredictedProbability, actual));
        }
        if (resolved.Count < 5) return 1.0;
        int correct = resolved.Count(r => (r.Predicted > 0.5) == r.Actual);
        return (double)correct / resolved.Count;
    }

    private void TrimPredictionWindow()
    {
        while (_recentPredictions.Count > _rfConfig.EvaluationWindowSize * 2)
            _recentPredictions.TryDequeue(out _);
    }

    private double[] FeatureVector(VoltageFeature f)
    {
        var names = _rfConfig.FeatureNames;
        var propMap = new Dictionary<string, double>
        {
            ["NoisePower"] = f.NoisePower, ["StdVoltage"] = f.StdVoltage,
            ["FrequencyPeak"] = f.FrequencyPeak, ["MeanVoltage"] = f.MeanVoltage,
            ["DominantFrequencyAmplitude"] = f.DominantFrequencyAmplitude,
            ["HighFrequencyNoiseRatio"] = f.HighFrequencyNoiseRatio,
            ["SpectralEnergyRatio"] = f.SpectralEnergyRatio,
            ["SpectralBandwidth"] = f.SpectralBandwidth
        };
        var result = new double[names.Count];
        for (int i = 0; i < names.Count; i++) result[i] = propMap.GetValueOrDefault(names[i], 0);
        return result;
    }

    private static List<(double[] Input, int Label)> BootstrapSample(
        List<(double[] Input, int Label)> data, Random rng)
    {
        var sample = new List<(double[] Input, int Label)>();
        for (int i = 0; i < data.Count; i++) sample.Add(data[rng.Next(data.Count)]);
        return sample;
    }

    private DecisionTree BuildTree(double[][] X, int[] y, int depth, Random rng)
    {
        int n = X.Length, numFeatures = X[0].Length;
        int posCount = y.Count(l => l == 1);
        double posRatio = n > 0 ? (double)posCount / n : 0;
        if (depth <= 0 || n < _rfConfig.MinSamplesLeaf * 2 || posCount == 0 || posCount == n)
            return new DecisionTree { LeafValue = posRatio };

        int bestFeature = 0; double bestThreshold = 0, bestGini = double.MaxValue;
        int tryCount = Math.Max(1, (int)Math.Sqrt(numFeatures));
        var featIdx = Enumerable.Range(0, numFeatures).OrderBy(_ => rng.Next()).Take(tryCount).ToList();

        foreach (int fi in featIdx)
        {
            var vals = X.Select(row => row[fi]).Distinct().OrderBy(v => v).ToList();
            if (vals.Count < 2) continue;
            for (int v = 0; v < vals.Count - 1; v++)
            {
                double thr = (vals[v] + vals[v + 1]) / 2;
                var left = new List<int>(); var right = new List<int>();
                for (int i = 0; i < n; i++) { if (X[i][fi] <= thr) left.Add(y[i]); else right.Add(y[i]); }
                if (left.Count < _rfConfig.MinSamplesLeaf || right.Count < _rfConfig.MinSamplesLeaf) continue;
                double gini = ComputeGini(left) * left.Count / n + ComputeGini(right) * right.Count / n;
                if (gini < bestGini) { bestGini = gini; bestFeature = fi; bestThreshold = thr; }
            }
        }
        if (bestGini == double.MaxValue) return new DecisionTree { LeafValue = posRatio };

        var li = new List<int>(); var ri = new List<int>();
        for (int i = 0; i < n; i++) { if (X[i][bestFeature] <= bestThreshold) li.Add(i); else ri.Add(i); }
        return new DecisionTree
        {
            FeatureIndex = bestFeature, Threshold = bestThreshold,
            Left = BuildTree(li.Select(i => X[i]).ToArray(), li.Select(i => y[i]).ToArray(), depth - 1, rng),
            Right = BuildTree(ri.Select(i => X[i]).ToArray(), ri.Select(i => y[i]).ToArray(), depth - 1, rng)
        };
    }

    private static double ComputeGini(List<int> labels)
    {
        if (labels.Count == 0) return 0;
        double p1 = (double)labels.Count(l => l == 1) / labels.Count;
        return 1 - (1 - p1) * (1 - p1) - p1 * p1;
    }

    private class DecisionTree
    {
        public int FeatureIndex { get; set; }
        public double Threshold { get; set; }
        public double LeafValue { get; set; }
        public DecisionTree? Left { get; set; }
        public DecisionTree? Right { get; set; }
        public bool IsLeaf => Left == null && Right == null;
        public double Predict(double[] input) => IsLeaf ? LeafValue :
            input[FeatureIndex] <= Threshold ? Left!.Predict(input) : Right!.Predict(input);
    }
}
