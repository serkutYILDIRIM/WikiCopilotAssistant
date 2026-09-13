namespace WikiCopilotAssistant.Services;

public sealed class ResearchOptions
{
    public int TimeoutSeconds { get; set; } = 300;
    public int ApprovalTimeoutSeconds { get; set; } = 60;
    public int RetentionMinutes { get; set; } = 30;
    public int CleanupTimeoutSeconds { get; set; } = 15;

    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 3600 ||
            ApprovalTimeoutSeconds is < 1 or > 300 ||
            RetentionMinutes is < 1 or > 120 ||
            CleanupTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException(
                "Research configuration is invalid: timeout 1–3600s, approval 1–300s, retention 1–120min, cleanup 1–60s.");
    }
}
