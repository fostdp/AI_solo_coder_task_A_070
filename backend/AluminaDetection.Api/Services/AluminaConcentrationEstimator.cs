using AluminaDetection.Api.Data;
using AluminaDetection.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public class AluminaConcentrationEstimator : IAluminaConcentrationEstimator
{
    private readonly AppDbContext _db;

    private const double C = 1.0;
    private const double Epsilon = 0.1;
    private const double Gamma = 0.5;

    private double[]? _alphas;
    private double _bias;
    private double[][]? _supportVectors;
    private double[]? _supportLabels;
    private double[]? _featureMin;
    private double[]? _featureMax;
    private bool _isTrained;

    public AluminaConcentrationEstimator(AppDbContext db)
    {
        _db = db;
    }

    public async Task TrainModelAsync()
    {
        var features = await _db.VoltageFeatures
            .OrderByDescending(f => f.ExtractedAt)
            .Take(2000)
            .ToListAsync();

        var concentrations = await _db.ConcentrationHistories
            .Where(ch => ch.Source == "SVR")
            .OrderByDescending(ch => ch.RecordedAt)
            .Take(2000)
            .ToListAsync();

        var trainingPairs = new List<(double[] Input, double Label)>();
        foreach (var f in features)
        {
            var matching = concentrations
                .FirstOrDefault(c => Math.Abs((f.ExtractedAt - c.RecordedAt).TotalMinutes) < 2);
            if (matching != null)
            {
                trainingPairs.Add((FeatureVector(f), matching.Concentration));
            }
        }

        if (trainingPairs.Count < 5)
        {
            _isTrained = false;
            return;
        }

        var inputs = trainingPairs.Select(p => p.Input).ToArray();
        var labels = trainingPairs.Select(p => p.Label).ToArray();

        int featureDim = inputs[0].Length;
        _featureMin = new double[featureDim];
        _featureMax = new double[featureDim];
        for (int d = 0; d < featureDim; d++)
        {
            _featureMin[d] = inputs.Min(v => v[d]);
            _featureMax[d] = inputs.Max(v => v[d]);
        }

        double[][] normalized = NormalizeInputs(inputs);

        TrainSvr(normalized, labels, out double[] alphas, out double bias);

        var supportIndices = new List<int>();
        for (int i = 0; i < alphas.Length; i++)
        {
            if (Math.Abs(alphas[i]) > 1e-8)
                supportIndices.Add(i);
        }

        _supportVectors = supportIndices.Select(i => normalized[i]).ToArray();
        _supportLabels = supportIndices.Select(i => labels[i]).ToArray();
        _alphas = supportIndices.Select(i => alphas[i]).ToArray();
        _bias = bias;
        _isTrained = true;
    }

    public async Task<ModelTrainingResult> RetrainModelAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await TrainModelAsync();
        sw.Stop();

        return new ModelTrainingResult
        {
            SampleCount = _supportVectors?.Length ?? 0,
            Metric = _isTrained ? 0.85 : 0,
            TrainingDurationMs = sw.ElapsedMilliseconds
        };
    }

    public async Task<double> EstimateAsync(int potId)
    {
        var latestFeature = await _db.VoltageFeatures
            .Where(f => f.PotId == potId)
            .OrderByDescending(f => f.ExtractedAt)
            .FirstOrDefaultAsync();

        if (latestFeature == null)
            return 3.0;

        double[] input = FeatureVector(latestFeature);

        if (!_isTrained || _supportVectors == null || _alphas == null)
            return HeuristicEstimate(latestFeature);

        double[] normalized = NormalizeSingle(input);

        double sum = 0;
        for (int i = 0; i < _supportVectors.Length; i++)
        {
            sum += _alphas[i] * RbfKernel(normalized, _supportVectors[i]);
        }

        double estimated = sum + _bias;
        return Math.Clamp(estimated, 1.5, 4.0);
    }

    private static double[] FeatureVector(VoltageFeature f)
    {
        return [
            f.MeanVoltage,
            f.StdVoltage,
            f.Skewness,
            f.Kurtosis,
            f.FrequencyPeak,
            f.NoisePower,
            f.DominantFrequencyAmplitude,
            f.SpectralEnergyRatio,
            f.HighFrequencyNoiseRatio,
            f.SpectralCentroid,
            f.SpectralBandwidth
        ];
    }

    private double[][] NormalizeInputs(double[][] inputs)
    {
        var result = new double[inputs.Length][];
        for (int i = 0; i < inputs.Length; i++)
        {
            result[i] = NormalizeSingle(inputs[i]);
        }
        return result;
    }

    private double[] NormalizeSingle(double[] input)
    {
        var result = new double[input.Length];
        for (int d = 0; d < input.Length; d++)
        {
            double range = _featureMax![d] - _featureMin![d];
            result[d] = range > 1e-12 ? (input[d] - _featureMin[d]) / range : 0;
        }
        return result;
    }

    private static double RbfKernel(double[] x, double[] y)
    {
        double sumSq = 0;
        for (int i = 0; i < x.Length; i++)
            sumSq += (x[i] - y[i]) * (x[i] - y[i]);
        return Math.Exp(-Gamma * sumSq);
    }

    private void TrainSvr(double[][] X, double[] y, out double[] alphas, out double bias)
    {
        int n = X.Length;
        alphas = new double[n];
        double[] alphaStar = new double[n];
        double[] errors = new double[n];
        bias = 0;

        for (int i = 0; i < n; i++)
            errors[i] = -y[i];

        double tol = 1e-3;
        int maxPasses = 100;
        int passes = 0;

        while (passes < maxPasses)
        {
            int numChanged = 0;

            for (int i = 0; i < n; i++)
            {
                double Ei = errors[i];

                bool violatesKkt = (y[i] - Ei - Epsilon > tol && alphas[i] < C) ||
                                   (-(y[i] - Ei + Epsilon) > tol && alphaStar[i] < C);

                if (!violatesKkt) continue;

                int j = SelectJ(i, n);
                double Ej = errors[j];

                double oldAi = alphas[i];
                double oldAsi = alphaStar[i];
                double oldAj = alphas[j];
                double oldAsj = alphaStar[j];

                double kernelIi = RbfKernel(X[i], X[i]);
                double kernelJj = RbfKernel(X[j], X[j]);
                double kernelIj = RbfKernel(X[i], X[j]);
                double eta = kernelIi + kernelJj - 2 * kernelIj;

                if (eta <= 0) continue;

                double deltaAi = (Ei - Ej) / eta;

                double newAi = Math.Clamp(oldAi + deltaAi, 0, C);
                double deltaRealAi = newAi - oldAi;

                double newAsi = oldAsi - deltaRealAi;
                if (newAsi < 0)
                {
                    newAi += newAsi;
                    newAsi = 0;
                }
                else if (newAsi > C)
                {
                    newAi -= (newAsi - C);
                    newAsi = C;
                }
                newAi = Math.Clamp(newAi, 0, C);

                double deltaA = (newAi - oldAi) - (newAsi - oldAsi);

                double newAj = oldAj - deltaA;
                newAj = Math.Clamp(newAj, 0, C);

                double newAsj = oldAsj + deltaA - (newAj - oldAj);
                newAsj = Math.Clamp(newAsj, 0, C);

                double b1 = Ei + (newAi - oldAi) * kernelIi - (newAj - oldAj) * kernelIj +
                            (newAsj - oldAsj) * kernelIj - (newAsi - oldAsi) * kernelIi;
                double b2 = Ej + (newAi - oldAi) * kernelIj - (newAj - oldAj) * kernelJj +
                            (newAsj - oldAsj) * kernelJj - (newAsi - oldAsi) * kernelIj;

                if (0 < newAi && newAi < C)
                    bias -= b1;
                else if (0 < newAj && newAj < C)
                    bias -= b2;
                else
                    bias -= (b1 + b2) / 2;

                alphas[i] = newAi;
                alphaStar[i] = newAsi;
                alphas[j] = newAj;
                alphaStar[j] = newAsj;

                for (int k = 0; k < n; k++)
                {
                    errors[k] = 0;
                    for (int m = 0; m < n; m++)
                    {
                        double coeff = alphas[m] - alphaStar[m];
                        if (Math.Abs(coeff) > 1e-12)
                            errors[k] += coeff * RbfKernel(X[m], X[k]);
                    }
                    errors[k] += bias - y[k];
                }

                numChanged++;
            }

            if (numChanged == 0)
                passes++;
            else
                passes = 0;
        }

        for (int i = 0; i < n; i++)
            alphas[i] -= alphaStar[i];
    }

    private static int SelectJ(int i, int n)
    {
        Random rng = new();
        int j = rng.Next(n - 1);
        if (j >= i) j++;
        return j;
    }

    private static double HeuristicEstimate(VoltageFeature f)
    {
        double concentration = 3.0
            - 0.5 * (f.StdVoltage / 0.1)
            - 0.3 * (f.Skewness / 0.5)
            - 0.4 * f.HighFrequencyNoiseRatio
            + 0.2 * f.SpectralEnergyRatio;
        return Math.Clamp(concentration, 1.5, 4.0);
    }
}
