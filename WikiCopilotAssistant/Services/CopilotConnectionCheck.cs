using System.Text.Json;
using GitHub.Copilot;

namespace WikiCopilotAssistant.Services;

internal static class CopilotConnectionCheck
{
    public static async Task<int> RunAsync(bool checkWebTools = false)
    {
        var cliPath = Environment.GetEnvironmentVariable("WIKICOPILOT_CLI_PATH");
        if (cliPath is not null &&
            (!Path.IsPathFullyQualified(cliPath) || !File.Exists(cliPath)))
        {
            Console.Error.WriteLine(
                "WIKICOPILOT_CLI_PATH must point to an existing CLI executable using an absolute path.");
            return 1;
        }

        var workingDirectory = Directory.CreateTempSubdirectory("wiki-copilot-connection-");
        try
        {
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                Connection = RuntimeConnection.ForStdio(
                    path: cliPath,
                    args: cliPath is null ? null : ["--no-auto-update"]),
                WorkingDirectory = workingDirectory.FullName,
                UseLoggedInUser = true,
                EnableRemoteSessions = false
            });
            Console.WriteLine(cliPath is null ? "Runtime source: bundled" : "Runtime source: configured local CLI");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await client.StartAsync(timeout.Token);

            var status = await client.GetStatusAsync(timeout.Token);
            Console.WriteLine($"Copilot runtime: {status.Version}; protocol: {status.ProtocolVersion}");

            var auth = await client.GetAuthStatusAsync(timeout.Token);
            Console.WriteLine($"Authenticated: {auth.IsAuthenticated}");
            if (!auth.IsAuthenticated)
            {
                Console.Error.WriteLine(
                    "This runtime cannot use your Copilot login. If you already signed in, check the selected CLI before logging in again.");
                return 1;
            }

            var models = await client.ListModelsAsync(timeout.Token);
            Console.WriteLine($"Available models: {models.Count}");
            if (models.Count == 0)
            {
                Console.Error.WriteLine(
                    "No models are available. Check your Copilot access and organization policy.");
                return 1;
            }

            if (checkWebTools)
            {
                return await CopilotWebCheck.RunAsync(client, workingDirectory.FullName);
            }

            Console.WriteLine(
                "Connection is ready. No prompt was sent and web-tool access has not been verified.");
            return 0;
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine(
                "The Copilot runtime is missing. Install a compatible runtime before retrying; no download was started.");
            return 1;
        }
        catch (InvalidOperationException exception) when (
            exception.Message.StartsWith("Copilot runtime wrapper not found at ", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "The bundled runtime wrapper is missing from the build output. Rebuild with an existing CopilotCliBinaryPath; no download was started.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The Copilot connection check timed out.");
            return 1;
        }
        catch (JsonException exception) when (exception.Path == "$.timestamp")
        {
            Console.Error.WriteLine(
                "The selected CLI returned a timestamp format incompatible with this SDK. Select a compatible CLI version; no update was started.");
            return 1;
        }
        finally
        {
            workingDirectory.Delete(recursive: true);
        }
    }
}
