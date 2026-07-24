using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SnowShotApi.Models;

public enum UserTranslationType
{
    AI = 0,
}

public enum UserTranslationOrderStatus
{
    Created = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
}

[PrimaryKey(nameof(Id))]
[Index(nameof(UserId))]
[Index(nameof(Type))]
[Index(nameof(CreatedAt))]
public sealed class UserTranslationOrder
{
    [Key]
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    public required long UserId { get; set; }

    [Required]
    public required UserTranslationType Type { get; set; }

    [Required]
    public required UserTranslationOrderStatus Status { get; set; }

    [Required]
    [DefaultValue("")]
    [MaxLength(16)]
    public required string From { get; set; }

    [Required]
    [DefaultValue("")]
    [MaxLength(16)]
    public required string To { get; set; }

    [Required]
    [DefaultValue("")]
    [MaxLength(16)]
    public required string Domain { get; set; }

    [Required]
    public required int ContentLength { get; set; }

    [Required]
    public required int QuotaDate { get; set; }

    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
}
