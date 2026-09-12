using WikiCopilotAssistant.Models;

namespace WikiCopilotAssistant.Services;

public sealed class ConversationStore(IHttpContextAccessor contextAccessor, ResearchService research)
{
    private const string OwnerKey = "research-owner";
    private ISession Session => contextAccessor.HttpContext!.Session;

    public Guid Owner
    {
        get
        {
            if (Guid.TryParse(Session.GetString(OwnerKey), out var owner))
                return owner;
            owner = Guid.NewGuid();
            Session.SetString(OwnerKey, owner.ToString("D"));
            return owner;
        }
    }

    public ConversationDraft? Get() => research.GetDraft(Owner);

    public bool Save(ConversationDraft draft) => research.Prepare(Owner, draft);

    // The owner survives reset; changing it would leave an orphaned active operation.
    public Task ResetAsync() => research.ResetAsync(Owner);
}
