using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SnowShotApi.Models;

[PrimaryKey(nameof(Id))]
[Index(nameof(IpAddress))]
public class IpUser
{
    [Key]
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    [MaxLength(64)]
    public required string IpAddress { get; set; }
} 