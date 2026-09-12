namespace WikiCopilotAssistant.Models;

public sealed record ResearchSnapshot(
    Guid Id,
    string State,
    string Message,
    bool IsActive,
    int ToolCalls,
    string? Answer,
    IReadOnlyList<ResearchSource> Sources,
    ResearchApproval? Approval,
    IReadOnlyList<string> ApprovedHosts)
{
    public static ResearchSnapshot Idle { get; } = new(
        Guid.Empty, "idle", "Araştırma başlatılmadı.", false, 0, null, [], null, []);
}

public sealed record ResearchSource(
    string Id, string Url, string Title, string Method, string Text,
    string? Author, string? License, bool Truncated);

public sealed record ResearchApproval(Guid Id, string Host, string Message);
