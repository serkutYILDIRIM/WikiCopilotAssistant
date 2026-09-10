using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using WikiCopilotAssistant.Services;

var sourceIndex = Array.IndexOf(args, "--read-source");
if (sourceIndex >= 0)
{
    var scopeIndex = Array.IndexOf(args, "--source-scope");
    var timeoutIndex = Array.IndexOf(args, "--read-timeout-ms");
    var timeoutMilliseconds = 180_000;
    if (sourceIndex + 1 >= args.Length || (scopeIndex >= 0 && scopeIndex + 1 >= args.Length))
    {
        Console.Error.WriteLine("--read-source and --source-scope require a URL value.");
        Environment.ExitCode = 1;
        return;
    }
    if (timeoutIndex >= 0 && (timeoutIndex + 1 >= args.Length ||
        !int.TryParse(args[timeoutIndex + 1], out timeoutMilliseconds) ||
        timeoutMilliseconds is < 1 or > 180_000))
    {
        Console.Error.WriteLine("--read-timeout-ms must be between 1 and 180000.");
        Environment.ExitCode = 1;
        return;
    }

    Environment.ExitCode = await SourceReadCommand.RunAsync(
        args[sourceIndex + 1],
        args.Contains("--render-source", StringComparer.Ordinal),
        scopeIndex >= 0 ? args[scopeIndex + 1] : null,
        timeoutMilliseconds);
    return;
}

var checkWebTools = args.Contains("--check-copilot-web", StringComparer.Ordinal);
if (args.Contains("--check-copilot", StringComparer.Ordinal) || checkWebTools)
{
    Environment.ExitCode = await CopilotConnectionCheck.RunAsync(checkWebTools);
    return;
}

var builder = WebApplication.CreateBuilder(args);
// The application logs safe error categories, never raw request exceptions or local paths.
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
var urls = builder.Configuration["urls"] ?? "http://localhost:5242";
foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        uri.Scheme is not ("http" or "https") || !LocalAccess.IsLoopbackHost(uri.Host) ||
        uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        throw new InvalidOperationException("The application may listen only on localhost HTTP(S) addresses.");
}
builder.WebHost.UseUrls(urls);
builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "WikiCopilot.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
});
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.Name = "WikiCopilot.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.IsEssential = true;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ConversationStore>();
builder.Services.AddSingleton<CopilotConnectionService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CopilotConnectionService>());
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!LocalAccess.IsAllowed(context.Request, context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("This application accepts only same-origin localhost requests.");
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    await next(context);
});
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    app.Logger.LogError("Local request failed ({ErrorType}); request content and exception details are not logged.",
        error?.GetType().Name ?? "unknown");
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsync("İşlem tamamlanamadı. Sayfayı yenileyin veya bağlantıyı yeniden kontrol edin.");
}));
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.MapControllers();

app.Run();
