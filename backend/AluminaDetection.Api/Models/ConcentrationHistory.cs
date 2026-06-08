using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AluminaDetection.Api.Models;

[Table("ConcentrationHistory")]
public class ConcentrationHistory
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int PotId { get; set; }

    public double Concentration { get; set; }

    [Required]
    [MaxLength(50)]
    public string Source { get; set; } = string.Empty;

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(PotId))]
    public PotInfo? Pot { get; set; }
}
