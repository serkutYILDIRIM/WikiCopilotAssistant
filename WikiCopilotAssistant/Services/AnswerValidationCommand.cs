using System.Text;
using WikiCopilotAssistant.Models;
using WikiCopilotAssistant.Services.Sources;

namespace WikiCopilotAssistant.Services;

internal static class AnswerValidationCommand
{
    public static async Task<int> RunAsync(string sourceUrl)
    {
        if (!Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Pipe the answer JSON to stdin. This command reads the public source URL, not local files or model prompts.");
            return 1;
        }
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source))
        {
            Console.Error.WriteLine("Supply an absolute public source URL.");
            return 1;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var input = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await Console.In.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                if (input.Length + count > 64_000)
                {
                    Console.Error.WriteLine("Answer JSON exceeds the 64000-character safety limit.");
                    return 1;
                }
                input.Append(buffer, 0, count);
            }

            await using var reader = new SourceReader(source);
            var result = await reader.ReadAsync(source.AbsoluteUri, cancellationToken: timeout.Token);
            if (result.Status != "success")
            {
                Console.Error.WriteLine($"Source read status: {result.Status}. No validation was performed.");
                return 1;
            }

            var evidence = new Dictionary<string, SourceEvidence>(StringComparer.Ordinal);
            foreach (var document in result.Documents)
            {
                var id = $"S{evidence.Count + 1}";
                evidence.Add(id, new SourceEvidence(id, document.Url, document.Title, document.Text,
                    document.Author, document.License, false));
            }
            var validation = AnswerValidator.Validate(input.ToString(), evidence);
            Console.WriteLine($"Source documents: {evidence.Count}; answer accepted: {validation.IsValid}");
            foreach (var error in validation.Errors)
                Console.Error.WriteLine(error);
            if (validation.IsValid)
                Console.WriteLine("Source references and quote text matched. Semantic correctness is not guaranteed.");
            return validation.IsValid ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Answer validation was cancelled or timed out.");
            return 1;
        }
        catch (SourceAccessException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (ArgumentException error) when (error.ParamName == "initialUrl")
        {
            Console.Error.WriteLine("This URL cannot be used as the public source.");
            return 1;
        }
    }
}
