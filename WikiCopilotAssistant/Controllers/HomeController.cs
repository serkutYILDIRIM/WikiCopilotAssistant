using Microsoft.AspNetCore.Mvc;
using WikiCopilotAssistant.Models;
using WikiCopilotAssistant.Services;

namespace WikiCopilotAssistant.Controllers;

public sealed class HomeController(CopilotConnectionService connection, ConversationStore conversations) : Controller
{
    [HttpGet("/")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Index()
    {
        var draft = conversations.Get();
        return View(new ChatPageModel
        {
            Connection = connection.Status,
            Draft = draft,
            SourceUrl = draft?.SourceUrl ?? "",
            Question = draft?.Question ?? ""
        });
    }
}
