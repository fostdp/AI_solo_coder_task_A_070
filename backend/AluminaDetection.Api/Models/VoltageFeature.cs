using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AluminaDetection.Api.Models;

[Table("VoltageFeature")]
public class VoltageFeature
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int PotId { get; set; }

    public double MeanVoltage { get; set; }

    public double StdVoltage { get; set; }

    public double Skewness { get; set; }

    public double Kurtosis { get; set; }

    public double FrequencyPeak { get; set; }

    public double NoisePower { get; set; }

    public DateTime WindowStart { get; set; }

    public DateTime WindowEnd { get; set; }

    public int SampleCount { get; set; }

    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(PotId))]
    public PotInfo? Pot { get; set; }
}
