using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SnowShotApi.Models;

public enum UserOrderType
{
    /// <summary>
    /// 翻译
    /// </summary>
    Translation,
}

[PrimaryKey(nameof(Id))]
[Index(nameof(UserId))]
[Index(nameof(Type), nameof(AssoId), IsUnique = true)]
[Index(nameof(CreatedAt))]
public class UserOrder
{
    [Key]
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    public required long UserId { get; set; }

    [Required]
    public required UserOrderType Type { get; set; }

    [Required]
    public required long AssoId { get; set; }

    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
} 