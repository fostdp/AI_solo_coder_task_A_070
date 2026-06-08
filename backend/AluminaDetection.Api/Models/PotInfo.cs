using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AluminaDetection.Api.Models;

[Table("PotInfo")]
public class PotInfo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int PotId { get; set; }

    [Required]
    [MaxLength(50)]
    public string PotCode { get; set; } = string.Empty;

    public int RowIndex { get; set; }

    public int ColIndex { get; set; }

    public int Status { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
