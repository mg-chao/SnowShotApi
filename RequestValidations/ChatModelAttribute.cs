using System.ComponentModel.DataAnnotations;

namespace SnowShotApi.RequestValidations;
public class ChatModelAttribute : ValidationAttribute
{
    public static readonly HashSet<string> ValidModels =
    [
        "deepseek-chat",
        "deepseek-reasoner",
        "claude-3-5-haiku-latest",
        "claude-3-7-sonnet-latest",
        "claude-3-7-sonnet-latest_thinking",
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

    public static string ConvertToText(string model)
    {
        return model switch
        {
            "deepseek-chat" => "DeepSeek Chat",
            "deepseek-reasoner" => "DeepSeek Reasoner",
            "claude-3-5-haiku-latest" => "Claude 3.5 Haiku",
            "claude-3-7-sonnet-latest" => "Claude 3.7 Sonnet",
            "claude-3-7-sonnet-latest_thinking" => "Claude 3.7 Sonnet",
            _ => "DeepSeek Chat",
        };
    }
}
