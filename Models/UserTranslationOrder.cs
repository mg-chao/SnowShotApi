using System.ComponentModel;
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

    /// <summary>
    /// Deepseek 翻译
    /// </summary>
    Deepseek,
}

public enum UserTranslationOrderStatus
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
[Index(nameof(Type))]
[Index(nameof(CreatedAt))]
public class UserTranslationOrder
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

    /// <summary>
    /// 记录普通翻译的内容长度
    /// </summary>
    [Required]
    public required int ContentLength { get; set; }

    /// <summary>
    /// 大模型翻译的 PromptTokens
    /// </summary>
    [Required]
    public required int PromptTokens { get; set; }

    /// <summary>
    /// 大模型翻译的 CompletionTokens
    /// </summary>
    [Required]
    public required int CompletionTokens { get; set; }

    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime CreatedAt { get; set; }
}