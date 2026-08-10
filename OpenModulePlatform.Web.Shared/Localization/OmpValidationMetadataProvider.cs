using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace OpenModulePlatform.Web.Shared.Localization;

/// <summary>
/// Stamps a localizable default message onto every DataAnnotations validation
/// attribute that has none. ASP.NET only routes attribute messages through
/// the configured IStringLocalizer when ErrorMessage is set; without one the
/// framework falls back to its hardcoded English texts - including the
/// implicit Required check it adds for non-nullable reference type
/// properties - regardless of the request culture. The message templates
/// live in SharedResource so every app gets localized defaults.
/// </summary>
public sealed class OmpValidationMetadataProvider : IValidationMetadataProvider
{
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        foreach (var metadata in context.ValidationMetadata.ValidatorMetadata)
        {
            if (metadata is not ValidationAttribute attribute
                || !string.IsNullOrEmpty(attribute.ErrorMessage)
                || !string.IsNullOrEmpty(attribute.ErrorMessageResourceName))
            {
                continue;
            }

            var template = attribute switch
            {
                RequiredAttribute => "The {0} field is required.",
                StringLengthAttribute { MinimumLength: > 0 } => "The field {0} must be a string with a minimum length of {2} and a maximum length of {1}.",
                StringLengthAttribute => "The field {0} must be a string with a maximum length of {1}.",
                MinLengthAttribute => "The field {0} must be a string or array type with a minimum length of '{1}'.",
                MaxLengthAttribute => "The field {0} must be a string or array type with a maximum length of '{1}'.",
                RangeAttribute => "The field {0} must be between {1} and {2}.",
                RegularExpressionAttribute => "The field {0} must match the regular expression '{1}'.",
                EmailAddressAttribute => "The {0} field is not a valid e-mail address.",
                PhoneAttribute => "The {0} field is not a valid phone number.",
                UrlAttribute => "The {0} field is not a valid fully-qualified http, https, or ftp URL.",
                CompareAttribute => "'{0}' and '{1}' do not match.",
                _ => null
            };

            if (template is not null)
            {
                attribute.ErrorMessage = template;
            }
        }
    }
}
