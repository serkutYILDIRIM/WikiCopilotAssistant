using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace WikiCopilotAssistant.Web.Localization;

/// <summary>
/// Provides the request localization configuration for the application.
/// </summary>
public static class LocalizationSetup
{
    /// <summary>
    /// The default culture used for all user-facing content.
    /// </summary>
    public const string DefaultCultureName = "tr-TR";

    /// <summary>
    /// Creates request localization options with Turkish as the default and only supported culture.
    /// </summary>
    public static RequestLocalizationOptions CreateRequestLocalizationOptions()
    {
        var supportedCultures = new[] { new CultureInfo(DefaultCultureName) };

        return new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(DefaultCultureName),
            SupportedCultures = supportedCultures,
            SupportedUICultures = supportedCultures,
        };
    }
}
