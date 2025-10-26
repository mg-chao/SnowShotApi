using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SnowShotApi.Models;


[PrimaryKey(nameof(Id))]
[Index(nameof(UserId))]
[Index(nameof(UserId), nameof(Date), nameof(Type), IsUnique = true)]
[Index(nameof(CreatedAt))]
public class UserTranslationOrderStats
{
    [Key]
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    public required long UserId { get; set; }

    [Required]
    public required int Date { get; set; }

    [Required]
    public required UserTranslationType Type { get; set; }

    [Required]
    public required int ContentLengthSum { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; }

    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
}