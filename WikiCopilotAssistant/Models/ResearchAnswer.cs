namespace WikiCopilotAssistant.Models;

public sealed record SourceEvidence(
    string Id, string Url, string Title, string Text,
    string? Author, string? License, bool External);

public sealed record AnswerCitation(
    string SourceId, string Url, string Title, string Quote,
    string? Author, string? License, bool External);

public sealed record AnswerStatement(string Text, IReadOnlyList<AnswerCitation> Citations);

public sealed record ResearchAnswer(
    IReadOnlyList<AnswerStatement> SourceFacts,
    IReadOnlyList<string> Commentary,
    IReadOnlyList<AnswerStatement> SuggestedSteps,
    IReadOnlyList<string> Uncertainties,
    IReadOnlyList<AnswerCitation> SimilarSources);

public sealed record AnswerValidationResult(ResearchAnswer? Answer, IReadOnlyList<string> Errors)
{
    public bool IsValid => Answer is not null && Errors.Count == 0;
}
