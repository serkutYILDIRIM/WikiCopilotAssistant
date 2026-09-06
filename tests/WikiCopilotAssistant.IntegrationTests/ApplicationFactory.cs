using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WikiCopilotAssistant.IntegrationTests;

public sealed class ApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(FailureController).Assembly));
    }
}
