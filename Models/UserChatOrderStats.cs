using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SnowShotApi.Models;

[PrimaryKey(nameof(Id))]
[Index(nameof(UserId))]
[Index(nameof(Date), nameof(Model), IsUnique = true)]
[Index(nameof(CreatedAt))]
public class UserChatOrderStats
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
    public required string Model { get; set; }

    [Required]
    public required int PromptTokensSum { get; set; }

    [Required]
    public required int CompletionTokensSum { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; }

    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
}