using System.Globalization;
using WikiCopilotAssistant.Web.Localization;

namespace WikiCopilotAssistant.UnitTests.Localization;

public class LocalizationSetupTests
{
    [Fact]
    public void CreateRequestLocalizationOptions_DefaultConfiguration_UsesTurkishAsDefaultCulture()
    {
        var options = LocalizationSetup.CreateRequestLocalizationOptions();

        Assert.Equal("tr-TR", options.DefaultRequestCulture.Culture.Name);
        Assert.Equal("tr-TR", options.DefaultRequestCulture.UICulture.Name);
    }

    [Fact]
    public void CreateRequestLocalizationOptions_DefaultConfiguration_SupportsOnlyTurkishCulture()
    {
        var options = LocalizationSetup.CreateRequestLocalizationOptions();

        var supportedCulture = Assert.Single(options.SupportedCultures!);
        var supportedUiCulture = Assert.Single(options.SupportedUICultures!);
        Assert.Equal(new CultureInfo("tr-TR"), supportedCulture);
        Assert.Equal(new CultureInfo("tr-TR"), supportedUiCulture);
    }
}
