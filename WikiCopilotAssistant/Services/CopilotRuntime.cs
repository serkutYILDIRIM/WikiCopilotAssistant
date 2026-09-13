using GitHub.Copilot;

namespace WikiCopilotAssistant.Services;

internal static class CopilotRuntime
{
    public static CopilotClient CreateClient(string workingDirectory)
    {
        var cliPath = Environment.GetEnvironmentVariable("WIKICOPILOT_CLI_PATH");
        if (cliPath is not null &&
            (!Path.IsPathFullyQualified(cliPath) || !File.Exists(cliPath)))
            throw new ArgumentException("WIKICOPILOT_CLI_PATH must identify an existing absolute CLI path.", "cliPath");

        return new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(
                path: cliPath, args: cliPath is null ? null : ["--no-auto-update"]),
            WorkingDirectory = workingDirectory,
            UseLoggedInUser = true,
            EnableRemoteSessions = false
        });
    }
}
