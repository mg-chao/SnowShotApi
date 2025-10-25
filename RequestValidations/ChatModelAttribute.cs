using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;
using SnowShotApi.Controllers;

namespace SnowShotApi.RequestValidations;

public class ChatModelInfo(string modelName, bool supportThinking, decimal inputPrice, decimal outputPrice)
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
}


public class ChatModelAttribute : ValidationAttribute
{
    public static readonly Dictionary<string, ChatModelInfo> ValidModels =
    new()
    {
        { "qwen-flash", new ChatModelInfo("qwen-flash", true, 0.0012M, 0.012M) },
        { "qwen-plus-latest", new ChatModelInfo("qwen-plus-latest", true, 0.0024M, 0.024M) }
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
            _ => "Unknown",
        };
    }
}
