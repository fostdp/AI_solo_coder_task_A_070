using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AluminaDetection.Api.Models;

[Table("AlarmRecord")]
public class AlarmRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int PotId { get; set; }

    [Required]
    [MaxLength(50)]
    public string AlarmType { get; set; } = string.Empty;

    public int AlarmLevel { get; set; }

    [Required]
    [MaxLength(500)]
    public string Message { get; set; } = string.Empty;

    public bool IsHandled { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? HandledAt { get; set; }

    [MaxLength(50)]
    public string? HandledBy { get; set; }

    [ForeignKey(nameof(PotId))]
    public PotInfo? Pot { get; set; }
}
