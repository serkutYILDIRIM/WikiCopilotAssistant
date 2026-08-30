using Microsoft.AspNetCore.Mvc.Testing;

namespace WikiCopilotAssistant.IntegrationTests;

public class HomePageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HomePageTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHomePage_ApplicationStarted_ReturnsSuccessHtmlResponse()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task GetHomePage_DefaultCulture_ContainsTurkishContent()
    {
        var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("<html lang=\"tr\">", html);
        Assert.Contains("Wiki veya dokümantasyon adresi", html);
        Assert.Contains("İndeksleme özelliği sonraki aşamada eklenecektir", html);
    }

    [Fact]
    public async Task GetHomePage_ApplicationStarted_DisplaysDisabledWikiUrlForm()
    {
        var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("<fieldset disabled>", html);
        Assert.Contains("İndeksle", html);
    }
}
