using WikiCopilotAssistant.Services.Sources;

namespace WikiCopilotAssistant.Services;

internal static class SourceReadCommand
{
    public static async Task<int> RunAsync(string url, bool useBrowser, string? sourceScope, int timeoutMilliseconds = 180_000)
    {
        if (!Uri.TryCreate(sourceScope ?? url, UriKind.Absolute, out var source) ||
            source.Scheme is not ("http" or "https") || !source.IsDefaultPort ||
            source.UserInfo.Length != 0)
        {
            Console.Error.WriteLine("The source scope must be a public HTTP(S) URL without credentials or a custom port.");
            return 1;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        ConsoleCancelEventHandler cancelHandler = (_, evt) =>
        {
            evt.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await using var reader = new SourceReader(source);
            var result = await reader.ReadAsync(url, useBrowser, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Console.WriteLine($"Read status: {result.Status}; documents: {result.Documents.Count}");
            if (result.Message is not null)
            {
                Console.WriteLine(result.Message);
            }

            if (result.RequiredHost is not null)
            {
                Console.WriteLine($"Additional host approval required: {result.RequiredHost}");
            }

            foreach (var document in result.Documents)
            {
                Console.WriteLine(
                    $"Method: {document.Method}; text characters: {document.Text.Length}; links: {document.Links.Count}; truncated: {document.Truncated}; author attributed: {document.Author is not null}; license attributed: {document.License is not null}");
            }

            return result.Status == "success" &&
                result.Documents.Any(document => !string.IsNullOrWhiteSpace(document.Text)) ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Source reading was cancelled or timed out.");
            return 1;
        }
        catch (SourceAccessException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (ArgumentException error) when (error.ParamName == "initialUrl")
        {
            Console.Error.WriteLine("This URL cannot be used as the initial public source.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
