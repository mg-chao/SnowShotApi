using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;
using SnowShotApi.Controllers;

namespace SnowShotApi.RequestValidations;
public class ChatModelAttribute : ValidationAttribute
{
    public static readonly HashSet<string> ValidModels =
    [
        "deepseek-chat",
        "deepseek-reasoner",
        "qwen-turbo-latest",
        "qwen-plus-latest",
        "qwen-max-latest",
        "qwen-turbo-latest_thinking",
        "qwen-plus-latest_thinking",
        // "qwen-max-latest_thinking",
        // "claude-3-5-haiku-latest",
        // "claude-3-7-sonnet-latest",
        // "claude-3-7-sonnet-latest_thinking",
    ];

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

        if (!ValidModels.Contains(model))
        {
            return new ValidationResult($"Unsupported model: {model}");
        }

        return ValidationResult.Success;
    }

    public static string ConvertToText(string model, IStringLocalizer<AppControllerBase> localizer)
    {
        return model switch
        {
            "deepseek-chat" => "DeepSeek V3",
            "deepseek-reasoner" => "DeepSeek R1",
            "qwen-max-latest" => $"{localizer["Qwen"]} Max",
            "qwen-plus-latest" => $"{localizer["Qwen"]} Plus",
            "qwen-turbo-latest" => $"{localizer["Qwen"]} Turbo",
            "qwen-max-latest_thinking" => $"{localizer["Qwen"]} Max",
            "qwen-plus-latest_thinking" => $"{localizer["Qwen"]} Plus",
            "qwen-turbo-latest_thinking" => $"{localizer["Qwen"]} Turbo",
            "claude-3-5-haiku-latest" => "Claude 3.5 Haiku",
            "claude-3-7-sonnet-latest" => "Claude 3.7 Sonnet",
            "claude-3-7-sonnet-latest_thinking" => "Claude 3.7 Sonnet",
            _ => "DeepSeek Chat",
        };
    }
}
