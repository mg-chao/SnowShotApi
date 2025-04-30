using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SnowShotApi.Models;

public enum UserType
{
    /// <summary>
    /// IP 用户
    /// </summary>
    Ip,
}

[PrimaryKey(nameof(Id))]
[Index(nameof(Type), nameof(AssoId), IsUnique = true)]
public class User
{
    [Key]
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    public required UserType Type { get; set; }

    [Required]
    public required long AssoId { get; set; }

    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
} 