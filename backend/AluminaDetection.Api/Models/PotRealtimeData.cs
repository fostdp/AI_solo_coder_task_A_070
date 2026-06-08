using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AluminaDetection.Api.Models;

[Table("PotRealtimeData")]
public class PotRealtimeData
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int PotId { get; set; }

    public double Voltage { get; set; }

    [MaxLength(500)]
    public string AnodeCurrentDistribution { get; set; } = string.Empty;

    public double PotTemperature { get; set; }

    public double BathTemperature { get; set; }

    public double AluminumLevel { get; set; }

    public double BathLevel { get; set; }

    public double AluminaConcentration { get; set; }

    public double EstimatedConcentration { get; set; }

    public double AnodeEffectProbability { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(PotId))]
    public PotInfo? Pot { get; set; }
}
