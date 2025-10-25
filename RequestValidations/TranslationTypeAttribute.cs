using System.ComponentModel.DataAnnotations;
using SnowShotApi.Models;

namespace SnowShotApi.RequestValidations;

public class TranslationTypeAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return new ValidationResult("Translation type cannot be null");
        }

        if (value is not UserTranslationType)
        {
            return new ValidationResult("Invalid translation type");
        }

        var translationType = (UserTranslationType)value;

        // 验证枚举值是否在定义的范围内
        if (!Enum.IsDefined(translationType))
        {
            return new ValidationResult($"Unsupported translation type: {translationType}");
        }

        return ValidationResult.Success;
    }
}

