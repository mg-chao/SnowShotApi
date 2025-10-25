using System.ComponentModel.DataAnnotations;

namespace SnowShotApi.RequestValidations;

/// <summary>
/// Validates the maximum length of each element in a collection
/// </summary>
public class MaxLengthElementAttribute(int maxLength) : ValidationAttribute
{
    private readonly int _maxLength = maxLength;

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return ValidationResult.Success;
        }

        if (value is IEnumerable<string> stringList)
        {
            var index = 0;
            foreach (var item in stringList)
            {
                if (item != null && item.Length > _maxLength)
                {
                    return new ValidationResult(
                        ErrorMessage ?? $"Element [{index}] length cannot exceed {_maxLength} characters. Current length is {item.Length}.",
                        [validationContext.MemberName ?? "Content"]
                    );
                }
                index++;
            }
        }

        return ValidationResult.Success;
    }
}

