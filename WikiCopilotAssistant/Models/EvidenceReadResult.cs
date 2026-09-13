using WikiCopilotAssistant.Services.Sources;

namespace WikiCopilotAssistant.Models;

internal sealed record EvidenceReadResult(
    string Status,
    IReadOnlyList<EvidenceDocument> Documents,
    string? Message,
    string? RequiredHost)
{
    public static EvidenceReadResult Failure(string message) => new("unavailable", [], message, null);
}

internal sealed record EvidenceDocument(
    string? SourceId, string Url, string Title, string Text, string Method,
    IReadOnlyList<SourceLink> Links, string? Author, string? License,
    bool Truncated, bool External);
