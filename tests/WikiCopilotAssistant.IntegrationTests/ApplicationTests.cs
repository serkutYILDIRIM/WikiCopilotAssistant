using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using WikiCopilotAssistant.Web;

namespace WikiCopilotAssistant.IntegrationTests;

public sealed class ApplicationTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    [Fact]
    public void Startup_ProductionConfiguration_ResolvesMvcAndTurkishLocalization()
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;

        Assert.NotNull(services.GetRequiredService<ICompositeViewEngine>());
        Assert.NotNull(services.GetRequiredService<IStringLocalizer<SharedResource>>());
        Assert.Equal("tr-TR", options.DefaultRequestCulture.Culture.Name);
        Assert.Equal("tr-TR", options.DefaultRequestCulture.UICulture.Name);
        Assert.Equal("tr-TR", Assert.Single(options.SupportedCultures!).Name);
        Assert.Equal("tr-TR", Assert.Single(options.SupportedUICultures!).Name);
        Assert.Empty(options.RequestCultureProviders);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Home/Index")]
    [InlineData("/?culture=en-US&ui-culture=en-US")]
    public async Task Home_RegardlessOfRequestedCulture_ReturnsTurkishPage(string path)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        using var response = await client.GetAsync(path);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("tr-TR", response.Content.Headers.ContentLanguage);
        Assert.Contains("<html lang=\"tr-TR\">", html);
        Assert.Contains("<title>Ana sayfa - WikiCopilotAssistant</title>", html);
        Assert.Contains("Dokümantasyonda aramak yerine sorunuzu sorun.", html);
        Assert.Contains("Wiki veya dokümantasyon adresi", html);
        Assert.DoesNotContain("Hello World!", html);
    }

    [Fact]
    public async Task Home_FoundationPhase_ShowsDisabledFormAndHonestFeatureStatus()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync("/");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Matches("<fieldset\\s+disabled", html);
        Assert.Matches("<button[^>]+type=\"button\"[^>]+disabled", html);
        Assert.Contains("şu anda hiçbir adres indirilmiyor.", html);
        Assert.Contains("Sayfayı indeksle (yakında)", html);
        Assert.Contains("Sonraki aşamalarda neler olacak?", html);
        Assert.Contains("Copilot bağlantısı kurulmaz.", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public async Task Home_PostedSourceUrl_HasNoIngestionEndpoint()
    {
        using var client = CreateClient();
        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["sourceUrl"] = "https://example.com/docs" });

        using var response = await client.PostAsync("/", content);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("/css/site.css", "--brand-color")]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css", "Bootstrap")]
    public async Task StaticAsset_LocalStylesheet_IsAvailableWithoutCdn(string path, string expected)
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(path);
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expected, content);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task UnhandledException_Production_ReturnsTurkishErrorWithoutInternalDetails(string method)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/test/failure");

        using var response = await client.SendAsync(request);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("İşlem tamamlanamadı", html);
        Assert.Contains("Lütfen daha sonra yeniden deneyin.", html);
        Assert.Contains("Ana sayfaya dön", html);
        Assert.Contains("tr-TR", response.Content.Headers.ContentLanguage);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("Sensitive test failure", html);
        Assert.DoesNotContain("InvalidOperationException", html);
        Assert.DoesNotContain("FailureController", html);
        Assert.DoesNotContain("Stack Trace", html);
    }

    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });
}
