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
var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
