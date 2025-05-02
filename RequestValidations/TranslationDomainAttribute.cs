using System.ComponentModel.DataAnnotations;

namespace SnowShotApi.RequestValidations;
public class TranslationDomainAttribute : ValidationAttribute
{
    private static readonly HashSet<string> ValidDomains =
    [
        "general",
        "computers",
        "medicine",
        "finance",
        "game",
    ];

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return new ValidationResult("Domain cannot be empty");
        }

        var domain = value.ToString();
        if (string.IsNullOrEmpty(domain))
        {
            return new ValidationResult("Domain cannot be empty");
        }

        if (!ValidDomains.Contains(domain))
        {
            return new ValidationResult($"Unsupported domain: {domain}");
        }

        return ValidationResult.Success;
    }
}
