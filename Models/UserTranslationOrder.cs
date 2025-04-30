using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SnowShotApi.Models;

public enum UserTranslationType
{
    /// <summary>
    /// 有道翻译
    /// </summary>
    Youdao,
}

[PrimaryKey(nameof(Id))]
[Index(nameof(Type))]
[Index(nameof(CreatedAt))]
public class UserTranslationOrder
{
    [Key]
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    public required UserTranslationType Type { get; set; }

    [Required]
    public required int ContentLength { get; set; }

    [Required]
    public required int ContentByteCount { get; set; }

    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
}