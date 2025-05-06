using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SnowShotApi.Models;

public enum UserChatOrderStatus
{
    /// <summary>
    /// 已创建
    /// </summary>
    Created,

    /// <summary>
    /// 已完成
    /// </summary>
    Completed,

    /// <summary>
    /// 失败
    /// </summary>
    Failed,
}

[PrimaryKey(nameof(Id))]
[Index(nameof(UserId))]
[Index(nameof(Model))]
[Index(nameof(CreatedAt))]
public class UserChatOrder
{
    [Key]
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    public required long UserId { get; set; }

    [Required]
    public required string Model { get; set; }

    [Required]
    public required UserChatOrderStatus Status { get; set; }

    [Required]
    [DefaultValue(0)]
    public required int PromptTokens { get; set; }

    [Required]
    [DefaultValue(0)]
    public required int CompletionTokens { get; set; }

    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
}