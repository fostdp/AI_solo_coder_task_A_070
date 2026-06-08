using AluminaDetection.Api.Data;
using AluminaDetection.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public class AnodeEffectPredictor : IAnodeEffectPredictor
{
    private readonly AppDbContext _db;
    private readonly ILogger<AnodeEffectPredictor> _logger;

    private List<DecisionTree>? _forest;
    private bool _isTrained;
    private DateTime _lastTrainingTime = DateTime.MinValue;

    private const int TreeCount = 10;
    private const int MaxDepth = 5;
    private const int MinSamplesLeaf = 5;
    private const double AccuracyDropThreshold = 0.15;
    private const double MinAccuracyForProduction = 0.70;
    private const int EvaluationWindowSize = 50;

    private readonly ConcurrentQueue<PredictionRecord> _recentPredictions = new();
    private double _lastEvaluatedAccuracy = 1.0;

    private sealed class PredictionRecord
    {
        public required int PotId { get; init; }
        public required double PredictedProbability { get; init; }
        public required DateTime PredictionTime { get; init; }
        public bool? ActualOutcome { get; set; }
    }

    public AnodeEffectPredictor(AppDbContext db, ILogger<AnodeEffectPredictor> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<double> PredictAsync(int potId)
    {
        if (!_isTrained || _forest == null || _forest.Count == 0)
        {
            return await HeuristicPredictAsync(potId);
        }

        var latestFeature = await _db.VoltageFeatures
            .Where(f => f.PotId == potId)
            .OrderByDescending(f => f.ExtractedAt)
            .FirstOrDefaultAsync();

        if (latestFeature == null)
            return 0.0;

        double[] input = FeatureVector(latestFeature);

        double sum = 0;
        foreach (var tree in _forest)
        {
            sum += tree.Predict(input);
        }

        double probability = sum / _forest.Count;

        _recentPredictions.Enqueue(new PredictionRecord
        {
            PotId = potId,
            PredictedProbability = probability,
            PredictionTime = DateTime.UtcNow
        });

        TrimPredictionWindow();

        return Math.Clamp(probability, 0.0, 1.0);
    }

    public async Task TrainModelAsync()
    {
        var features = await _db.VoltageFeatures
            .OrderByDescending(f => f.ExtractedAt)
            .Take(5000)
            .ToListAsync();

        var alarmEvents = await _db.AlarmRecords
            .Where(a => a.AlarmType == "AnodeEffect")
            .ToDictionaryAsync(a => a.PotId, a => a.CreatedAt);

        var trainingData = new List<(double[] Input, int Label)>();
        foreach (var f in features)
        {
            bool effectOccurred = alarmEvents.TryGetValue(f.PotId, out var effectTime)
                && Math.Abs((f.ExtractedAt - effectTime).TotalMinutes) < 3;
            trainingData.Add((FeatureVector(f), effectOccurred ? 1 : 0));
        }

        if (trainingData.Count < 10)
        {
            _isTrained = false;
            return;
        }

        Random rng = new(42);
        _forest = new List<DecisionTree>();

        for (int t = 0; t < TreeCount; t++)
        {
            var bootstrap = BootstrapSample(trainingData, rng);
            var tree = BuildTree(bootstrap.Select(p => p.Input).ToArray(),
                                 bootstrap.Select(p => p.Label).ToArray(),
                                 MaxDepth, rng);
            _forest.Add(tree);
        }

        _isTrained = true;
        _lastTrainingTime = DateTime.UtcNow;
    }

    public async Task<ModelTrainingResult> RetrainModelAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await TrainModelAsync();
        sw.Stop();

        var accuracy = await EvaluateModelAccuracyAsync();
        _lastEvaluatedAccuracy = accuracy;

        _logger.LogInformation(
            "RF重训练完成. 树数={Trees}, 评估准确率={Accuracy:P1}, 耗时={Duration}ms",
            _forest?.Count ?? 0, accuracy, sw.ElapsedMilliseconds);

        return new ModelTrainingResult
        {
            SampleCount = _forest?.Count ?? 0,
            Metric = accuracy,
            TrainingDurationMs = sw.ElapsedMilliseconds
        };
    }

    public async Task<bool> CheckAndAutoRetrainIfNeededAsync()
    {
        var accuracy = await EvaluateModelAccuracyAsync();
        _lastEvaluatedAccuracy = accuracy;

        bool needsRetrain = false;

        if (accuracy < MinAccuracyForProduction)
        {
            _logger.LogWarning(
                "RF模型准确率{Accuracy:P1}低于阈值{Threshold:P1}，触发自动重训练",
                accuracy, MinAccuracyForProduction);
            needsRetrain = true;
        }

        if (_lastEvaluatedAccuracy > 0 && accuracy < _lastEvaluatedAccuracy - AccuracyDropThreshold)
        {
            _logger.LogWarning(
                "RF模型准确率从{Previous:P1}下降至{Current:P1}，降幅超过{Drop:P1}，触发自动重训练",
                _lastEvaluatedAccuracy, accuracy, AccuracyDropThreshold);
            needsRetrain = true;
        }

        if (_isTrained && (DateTime.UtcNow - _lastTrainingTime).TotalHours > 2)
        {
            _logger.LogInformation("RF模型已超过2小时未重训练，触发定期重训练");
            needsRetrain = true;
        }

        if (needsRetrain)
        {
            try
            {
                await RetrainModelAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RF自动重训练失败");
            }
        }

        return false;
    }

    public double GetCurrentAccuracy() => _lastEvaluatedAccuracy;

    public DateTime GetLastTrainingTime() => _lastTrainingTime;

    private async Task<double> EvaluateModelAccuracyAsync()
    {
        var recentAlarms = await _db.AlarmRecords
            .Where(a => a.AlarmType == "AnodeEffect" && a.CreatedAt >= DateTime.UtcNow.AddHours(-1))
            .ToListAsync();

        var resolvedPredictions = new List<(double Predicted, bool Actual)>();

        foreach (var pred in _recentPredictions)
        {
            bool actualEffect = recentAlarms.Any(a =>
                a.PotId == pred.PotId &&
                Math.Abs((a.CreatedAt - pred.PredictionTime).TotalMinutes) < 3);

            pred.ActualOutcome = actualEffect;
            resolvedPredictions.Add((pred.PredictedProbability, actualEffect));
        }

        if (resolvedPredictions.Count < 5)
            return 1.0;

        int correct = 0;
        foreach (var (predicted, actual) in resolvedPredictions)
        {
            bool predictedPositive = predicted > 0.5;
            if (predictedPositive == actual)
                correct++;
        }

        return (double)correct / resolvedPredictions.Count;
    }

    private void TrimPredictionWindow()
    {
        while (_recentPredictions.Count > EvaluationWindowSize * 2)
        {
            _recentPredictions.TryDequeue(out _);
        }
    }

    private static double[] FeatureVector(VoltageFeature f)
    {
        return [
            f.NoisePower,
            f.StdVoltage,
            f.FrequencyPeak,
            f.MeanVoltage,
            f.DominantFrequencyAmplitude,
            f.HighFrequencyNoiseRatio,
            f.SpectralEnergyRatio,
            f.SpectralBandwidth
        ];
    }

    private static List<(double[] Input, int Label)> BootstrapSample(
        List<(double[] Input, int Label)> data, Random rng)
    {
        var sample = new List<(double[] Input, int Label)>();
        for (int i = 0; i < data.Count; i++)
        {
            int idx = rng.Next(data.Count);
            sample.Add(data[idx]);
        }
        return sample;
    }

    private static DecisionTree BuildTree(double[][] X, int[] y, int depth, Random rng)
    {
        int n = X.Length;
        int numFeatures = X[0].Length;

        int positiveCount = y.Count(l => l == 1);
        double positiveRatio = n > 0 ? (double)positiveCount / n : 0;

        if (depth <= 0 || n < MinSamplesLeaf * 2 || positiveCount == 0 || positiveCount == n)
        {
            return new DecisionTree { LeafValue = positiveRatio };
        }

        int bestFeature = 0;
        double bestThreshold = 0;
        double bestGini = double.MaxValue;

        int featuresToTry = Math.Max(1, (int)Math.Sqrt(numFeatures));
        var featureIndices = Enumerable.Range(0, numFeatures).OrderBy(_ => rng.Next()).Take(featuresToTry).ToList();

        foreach (int featIdx in featureIndices)
        {
            var values = X.Select(row => row[featIdx]).Distinct().OrderBy(v => v).ToList();
            if (values.Count < 2) continue;

            for (int v = 0; v < values.Count - 1; v++)
            {
                double threshold = (values[v] + values[v + 1]) / 2;

                var leftLabels = new List<int>();
                var rightLabels = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (X[i][featIdx] <= threshold)
                        leftLabels.Add(y[i]);
                    else
                        rightLabels.Add(y[i]);
                }

                if (leftLabels.Count < MinSamplesLeaf || rightLabels.Count < MinSamplesLeaf)
                    continue;

                double gini = ComputeGini(leftLabels) * leftLabels.Count / n +
                              ComputeGini(rightLabels) * rightLabels.Count / n;

                if (gini < bestGini)
                {
                    bestGini = gini;
                    bestFeature = featIdx;
                    bestThreshold = threshold;
                }
            }
        }

        if (bestGini == double.MaxValue)
            return new DecisionTree { LeafValue = positiveRatio };

        var leftIndices = new List<int>();
        var rightIndices = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (X[i][bestFeature] <= bestThreshold)
                leftIndices.Add(i);
            else
                rightIndices.Add(i);
        }

        var leftX = leftIndices.Select(i => X[i]).ToArray();
        var leftY = leftIndices.Select(i => y[i]).ToArray();
        var rightX = rightIndices.Select(i => X[i]).ToArray();
        var rightY = rightIndices.Select(i => y[i]).ToArray();

        return new DecisionTree
        {
            FeatureIndex = bestFeature,
            Threshold = bestThreshold,
            Left = BuildTree(leftX, leftY, depth - 1, rng),
            Right = BuildTree(rightX, rightY, depth - 1, rng)
        };
    }

    private static double ComputeGini(List<int> labels)
    {
        if (labels.Count == 0) return 0;
        double p1 = (double)labels.Count(l => l == 1) / labels.Count;
        double p0 = 1 - p1;
        return 1 - p0 * p0 - p1 * p1;
    }

    private async Task<double> HeuristicPredictAsync(int potId)
    {
        var latestFeature = await _db.VoltageFeatures
            .Where(f => f.PotId == potId)
            .OrderByDescending(f => f.ExtractedAt)
            .FirstOrDefaultAsync();

        if (latestFeature == null)
            return 0.0;

        double logit = 2.0 * (latestFeature.NoisePower - 0.5)
                     + 1.5 * (latestFeature.StdVoltage - 0.3)
                     + latestFeature.FrequencyPeak * 0.5
                     + 3.0 * latestFeature.HighFrequencyNoiseRatio;

        double probability = Sigmoid(logit);
        return Math.Clamp(probability, 0.0, 1.0);
    }

    private static double Sigmoid(double x)
    {
        return 1.0 / (1.0 + Math.Exp(-x));
    }

    private class DecisionTree
    {
        public int FeatureIndex { get; set; }
        public double Threshold { get; set; }
        public double LeafValue { get; set; }
        public DecisionTree? Left { get; set; }
        public DecisionTree? Right { get; set; }

        public bool IsLeaf => Left == null && Right == null;

        public double Predict(double[] input)
        {
            if (IsLeaf)
                return LeafValue;

            return input[FeatureIndex] <= Threshold
                ? Left!.Predict(input)
                : Right!.Predict(input);
        }
    }
}
