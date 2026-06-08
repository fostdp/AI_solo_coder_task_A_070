using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AluminaDetection.Api.Models;

[Table("FeedingRecord")]
public class FeedingRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int PotId { get; set; }

    public double FeedAmount { get; set; }

    [Required]
    [MaxLength(50)]
    public string FeedType { get; set; } = string.Empty;

    public DateTime FeedTime { get; set; }

    [MaxLength(50)]
    public string Operator { get; set; } = string.Empty;

    public double? EstimatedConcentration { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "Pending";

    [ForeignKey(nameof(PotId))]
    public PotInfo? Pot { get; set; }
}
