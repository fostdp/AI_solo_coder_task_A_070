using System.Collections.Concurrent;
using AluminaDetection.Api.Data;
using AluminaDetection.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminaDetection.Api.Services;

public class VoltageFeatureExtractor : IVoltageFeatureExtractor
{
    private readonly AppDbContext _db;

    public VoltageFeatureExtractor(AppDbContext db)
    {
        _db = db;
    }

    public async Task<VoltageFeature> ExtractFeaturesAsync(int potId, int windowMinutes)
    {
        var windowEnd = DateTime.UtcNow;
        var windowStart = windowEnd.AddMinutes(-windowMinutes);
        var readings = await _db.PotRealtimeData
            .Where(r => r.PotId == potId && r.RecordedAt >= windowStart && r.RecordedAt <= windowEnd)
            .OrderBy(r => r.RecordedAt)
            .Select(r => r.Voltage)
            .ToListAsync();

        if (readings.Count < 4)
        {
            var empty = new VoltageFeature
            {
                PotId = potId,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                SampleCount = readings.Count,
                MeanVoltage = readings.Count > 0 ? readings.Average() : 0,
                StdVoltage = 0,
                Skewness = 0,
                Kurtosis = 0,
                FrequencyPeak = 0,
                NoisePower = 0,
                DominantFrequencyAmplitude = 0,
                SpectralEnergyRatio = 0,
                HighFrequencyNoiseRatio = 0,
                SpectralCentroid = 0,
                SpectralBandwidth = 0,
                ExtractedAt = DateTime.UtcNow
            };
            _db.VoltageFeatures.Add(empty);
            await _db.SaveChangesAsync();
            return empty;
        }

        double mean = readings.Average();
        double variance = readings.Sum(v => Math.Pow(v - mean, 2)) / readings.Count;
        double std = Math.Sqrt(variance);

        double skewness = ComputeSkewness(readings, mean, std);
        double kurtosis = ComputeKurtosis(readings, mean, std);
        double noisePower = ComputeNoisePower(readings, mean);

        int fftSize = NextPowerOfTwo(readings.Count);
        double[] real = new double[fftSize];
        double[] imag = new double[fftSize];

        for (int i = 0; i < readings.Count; i++)
            real[i] = readings[i] - mean;

        Fft(real, imag, fftSize);

        double[] magnitudes = new double[fftSize / 2];
        for (int i = 0; i < fftSize / 2; i++)
            magnitudes[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

        int frequencyPeak = 1;
        double maxMag = 0;
        for (int i = 1; i < fftSize / 2; i++)
        {
            if (magnitudes[i] > maxMag)
            {
                maxMag = magnitudes[i];
                frequencyPeak = i;
            }
        }

        double dominantFrequencyAmplitude = maxMag / readings.Count;

        double totalEnergy = 0;
        double lowFreqEnergy = 0;
        double highFreqEnergy = 0;
        int lowFreqCutoff = Math.Max(1, fftSize / 8);

        for (int i = 1; i < fftSize / 2; i++)
        {
            double energy = magnitudes[i] * magnitudes[i];
            totalEnergy += energy;
            if (i <= lowFreqCutoff)
                lowFreqEnergy += energy;
            else
                highFreqEnergy += energy;
        }

        double spectralEnergyRatio = totalEnergy > 0 ? lowFreqEnergy / totalEnergy : 0;
        double highFrequencyNoiseRatio = totalEnergy > 0 ? highFreqEnergy / totalEnergy : 0;

        double spectralCentroid = ComputeSpectralCentroid(magnitudes, fftSize);
        double spectralBandwidth = ComputeSpectralBandwidth(magnitudes, fftSize, spectralCentroid);

        var feature = new VoltageFeature
        {
            PotId = potId,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            SampleCount = readings.Count,
            MeanVoltage = Math.Round(mean, 6),
            StdVoltage = Math.Round(std, 6),
            Skewness = Math.Round(skewness, 6),
            Kurtosis = Math.Round(kurtosis, 6),
            FrequencyPeak = frequencyPeak,
            NoisePower = Math.Round(noisePower, 6),
            DominantFrequencyAmplitude = Math.Round(dominantFrequencyAmplitude, 6),
            SpectralEnergyRatio = Math.Round(spectralEnergyRatio, 6),
            HighFrequencyNoiseRatio = Math.Round(highFrequencyNoiseRatio, 6),
            SpectralCentroid = Math.Round(spectralCentroid, 6),
            SpectralBandwidth = Math.Round(spectralBandwidth, 6),
            ExtractedAt = DateTime.UtcNow
        };

        _db.VoltageFeatures.Add(feature);
        await _db.SaveChangesAsync();
        return feature;
    }

    private static double ComputeSpectralCentroid(double[] magnitudes, int fftSize)
    {
        double weightedSum = 0;
        double magnitudeSum = 0;
        int halfN = fftSize / 2;

        for (int i = 1; i < halfN; i++)
        {
            weightedSum += i * magnitudes[i];
            magnitudeSum += magnitudes[i];
        }

        return magnitudeSum > 0 ? weightedSum / magnitudeSum : 0;
    }

    private static double ComputeSpectralBandwidth(double[] magnitudes, int fftSize, double centroid)
    {
        double weightedSum = 0;
        double magnitudeSum = 0;
        int halfN = fftSize / 2;

        for (int i = 1; i < halfN; i++)
        {
            weightedSum += magnitudes[i] * Math.Pow(i - centroid, 2);
            magnitudeSum += magnitudes[i];
        }

        return magnitudeSum > 0 ? Math.Sqrt(weightedSum / magnitudeSum) : 0;
    }

    private static double ComputeSkewness(List<double> values, double mean, double std)
    {
        if (std == 0) return 0;
        double n = values.Count;
        double m3 = values.Sum(v => Math.Pow(v - mean, 3)) / n;
        return m3 / Math.Pow(std, 3);
    }

    private static double ComputeKurtosis(List<double> values, double mean, double std)
    {
        if (std == 0) return 0;
        double n = values.Count;
        double m4 = values.Sum(v => Math.Pow(v - mean, 4)) / n;
        return m4 / Math.Pow(std, 4) - 3.0;
    }

    private static double ComputeNoisePower(List<double> values, double mean)
    {
        double sumSq = values.Sum(v => Math.Pow(v - mean, 2));
        return sumSq / values.Count;
    }

    private static int NextPowerOfTwo(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    private static void Fft(double[] real, double[] imag, int n)
    {
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            double wReal = Math.Cos(angle);
            double wImag = Math.Sin(angle);

            for (int i = 0; i < n; i += len)
            {
                double curReal = 1.0, curImag = 0.0;
                for (int j = 0; j < len / 2; j++)
                {
                    int u = i + j;
                    int v = i + j + len / 2;
                    double tReal = curReal * real[v] - curImag * imag[v];
                    double tImag = curReal * imag[v] + curImag * real[v];
                    real[v] = real[u] - tReal;
                    imag[v] = imag[u] - tImag;
                    real[u] += tReal;
                    imag[u] += tImag;
                    double newCurReal = curReal * wReal - curImag * wImag;
                    curImag = curReal * wImag + curImag * wReal;
                    curReal = newCurReal;
                }
            }
        }
    }
}
