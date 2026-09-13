namespace WikiCopilotAssistant.Models;

public sealed record ResearchSnapshot(
    Guid Id,
    string State,
    string Message,
    bool IsActive,
    int ToolCalls,
    ResearchAnswer? Answer,
    IReadOnlyList<ResearchSource> Sources,
    ResearchApproval? Approval,
    IReadOnlyList<string> ApprovedHosts,
    string ValidationState = "pending",
    IReadOnlyList<string>? ValidationIssues = null,
    int RepairAttempts = 0)
{
    public static ResearchSnapshot Idle { get; } = new(
        Guid.Empty, "idle", "Araştırma başlatılmadı.", false, 0, null, [], null, []);
}

public sealed record ResearchSource(
    string Id, string Url, string Title, string Method, string Text,
    string? Author, string? License, bool Truncated, bool External = false);

public sealed record ResearchApproval(Guid Id, string Host, string Message);
