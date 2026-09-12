namespace WikiCopilotAssistant.Models;

public sealed record CopilotStatus(string State, string Message)
{
    public bool IsReady => State == "ready";
}
