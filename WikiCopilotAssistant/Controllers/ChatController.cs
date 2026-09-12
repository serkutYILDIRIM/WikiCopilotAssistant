using Microsoft.AspNetCore.Mvc;
using WikiCopilotAssistant.Models;
using WikiCopilotAssistant.Services;
using WikiCopilotAssistant.Services.Sources;

namespace WikiCopilotAssistant.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ChatController(CopilotConnectionService connection, ConversationStore conversations) : Controller
{
    [HttpPost("/chat/prepare")]
    [RequestSizeLimit(64 * 1024)]
    public IActionResult Prepare([Bind("SourceUrl,Question")] ChatPageModel model)
    {
        model.SourceUrl = model.SourceUrl?.Trim() ?? "";
        model.Question = model.Question?.Trim() ?? "";
        if (Uri.TryCreate(model.SourceUrl, UriKind.Absolute, out var source))
        {
            try
            {
                _ = new SourceAccessPolicy(source);
            }
            catch (SourceAccessException)
            {
                ModelState.AddModelError(nameof(model.SourceUrl), "Yalnızca herkese açık HTTP(S) siteleri desteklenir. Yerel adresler, özel portlar ve giriş bilgisi içeren URL'ler kullanılamaz.");
            }
            catch (ArgumentException)
            {
                ModelState.AddModelError(nameof(model.SourceUrl), "Geçerli bir wiki, doküman veya soru sayfası URL'si girin.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(model.SourceUrl))
        {
            ModelState.AddModelError(nameof(model.SourceUrl), "https:// ile başlayan geçerli bir kaynak URL'si girin.");
        }

        if (!connection.Status.IsReady)
            ModelState.AddModelError("", "Sohbeti hazırlamadan önce Copilot bağlantısını tamamlayın.");
        if (!ModelState.IsValid)
        {
            model.Connection = connection.Status;
            model.Draft = conversations.Get();
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return View("~/Views/Home/Index.cshtml", model);
        }

        if (!conversations.Save(new ConversationDraft(Guid.NewGuid(), source!.AbsoluteUri, model.Question)))
        {
            model.Connection = connection.Status;
            model.Draft = conversations.Get();
            ModelState.AddModelError("", "Önce etkin araştırmayı durdurun ve temizliğin tamamlanmasını bekleyin.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View("~/Views/Home/Index.cshtml", model);
        }
        return RedirectToAction("Index", "Home");
    }

    [HttpPost("/chat/reset")]
    public async Task<IActionResult> Reset()
    {
        await conversations.ResetAsync();
        return RedirectToAction("Index", "Home");
    }
}
