using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;
using SnowShotApi.Controllers;

namespace SnowShotApi.RequestValidations;

public class ChatModelInfo(string modelName, decimal inputPrice, decimal outputPrice, bool supportThinking, bool supportVision)
{
    public string ModelName { get; set; } = modelName;
    public bool SupportThinking { get; set; } = supportThinking;
    /// <summary>
    /// 输入价格, (元/1K tokens)
    /// </summary>
    /// <value></value>
    public decimal PromptTokenPrice { get; set; } = inputPrice;
    /// <summary>
    /// 输出价格, (元/1K tokens)
    /// </summary>
    /// <value></value>
    public decimal CompletionTokenPrice { get; set; } = outputPrice;

    /// <summary>
    /// 是否支持视觉理解
    /// </summary>
    /// <value></value>
    public bool SupportVision { get; set; } = supportVision;
}


public class ChatModelAttribute : ValidationAttribute
{
    public static readonly Dictionary<string, ChatModelInfo> ValidModels =
    new()
    {
        { "qwen-flash", new ChatModelInfo("qwen-flash", 0.0012M, 0.012M, true, false) },
        { "qwen-plus-latest", new ChatModelInfo("qwen-plus-latest", 0.0024M, 0.024M, true, false) },
        { "qwen3-vl-flash", new ChatModelInfo("qwen3-vl-flash", 0.0006M, 0.006M, true, true) }
    };

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return new ValidationResult("Model cannot be empty");
        }

        var model = value.ToString();
        if (string.IsNullOrEmpty(model))
        {
            return new ValidationResult("Model cannot be empty");
        }

        if (!ValidModels.ContainsKey(model))
        {
            return new ValidationResult($"Unsupported model: {model}");
        }

        return ValidationResult.Success;
    }

    public static string ConvertToText(string model, IStringLocalizer<AppControllerBase> localizer)
    {
        return model switch
        {
            "qwen-flash" => $"{localizer["Qwen"]} Flash",
            "qwen-plus-latest" => $"{localizer["Qwen"]} Plus",
            "qwen3-vl-flash" => $"{localizer["Qwen"]} VL Flash",
            _ => "Unknown",
        };
    }
}
