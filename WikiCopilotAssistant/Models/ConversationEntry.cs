using System.ComponentModel.DataAnnotations;

namespace WikiCopilotAssistant.Models;

public sealed record ConversationEntry(
    Guid Id, string Question, string State, string Message,
    ResearchAnswer? Answer, string ValidationState, bool MoreResearch);

public sealed class FollowUpRequest
{
    public Guid Id { get; set; }

    [StringLength(8000)]
    public string? Question { get; set; }

    public bool MoreResearch { get; set; }
}
