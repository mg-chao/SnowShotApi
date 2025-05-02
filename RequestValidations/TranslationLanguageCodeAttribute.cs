using System.ComponentModel.DataAnnotations;

namespace SnowShotApi.RequestValidations;
public class TranslationLanguageCodeAttribute(bool SupportAuto = false) : ValidationAttribute
{
    public bool SupportAuto { get; set; } = SupportAuto;

    private static readonly HashSet<string> ValidLanguageCodes =
    [
        "en",
        "zh-CHS",
        "zh-CHT",
        "es",
        "fr",
        "ar",
        "de",
        "it",
        "ja",
        "pt",
        "ru",
        "tr"
    ];

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return new ValidationResult("Language code cannot be empty[1]");
        }

        var languageCode = value.ToString();
        if (string.IsNullOrEmpty(languageCode))
        {
            return new ValidationResult("Language code cannot be empty[2]");
        }

        if (languageCode == "auto" && SupportAuto)
        {
            return ValidationResult.Success;
        }

        if (!ValidLanguageCodes.Contains(languageCode))
        {
            return new ValidationResult($"Unsupported language code: {languageCode}");
        }

        return ValidationResult.Success;
    }
}
