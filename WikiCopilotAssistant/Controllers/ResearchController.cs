using Microsoft.AspNetCore.Mvc;
using WikiCopilotAssistant.Models;
using WikiCopilotAssistant.Services;
using WikiCopilotAssistant.Services.Sources;

namespace WikiCopilotAssistant.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(64 * 1024)]
public sealed class ResearchController(ResearchService research, ConversationStore conversations) : Controller
{
    [HttpPost("/research/start")]
    public IActionResult Start([Bind("SourceUrl,Question")] ChatPageModel model)
    {
        model.SourceUrl = model.SourceUrl?.Trim() ?? "";
        model.Question = model.Question?.Trim() ?? "";
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(model.SourceUrl) ||
            string.IsNullOrWhiteSpace(model.Question))
            return UnprocessableEntity(new { message = "Geçerli bir kaynak URL'si (en fazla 4096 karakter) ve soru (en fazla 8000 karakter) girin." });
        if (!Uri.TryCreate(model.SourceUrl, UriKind.Absolute, out var source))
            return UnprocessableEntity(new { message = "http:// veya https:// ile başlayan geçerli bir kaynak URL'si girin." });
        try
        {
            _ = new SourceAccessPolicy(source);
        }
        catch (Exception error) when (error is SourceAccessException or ArgumentException)
        {
            return UnprocessableEntity(new { message = "Yalnızca herkese açık HTTP(S) kaynakları desteklenir. Yerel adresler, özel portlar ve giriş bilgisi içeren URL'ler kullanılamaz." });
        }
        var result = research.Start(conversations.Owner, new ConversationDraft(Guid.NewGuid(), source.AbsoluteUri, model.Question));
        return result.StatusCode switch
        {
            StatusCodes.Status202Accepted => StatusCode(result.StatusCode, result.Snapshot),
            StatusCodes.Status409Conflict => Conflict(new { message = "Bu sohbette etkin araştırma veya devam eden temizlik var. Tamamlanmasını bekleyin." }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Copilot bağlantısı hazır değil. Bağlantıyı kontrol edip yeniden deneyin." })
        };
    }

    [HttpGet("/research/status")]
    public IActionResult Status() => Json(research.Status(conversations.Owner));

    [HttpPost("/research/stop")]
    public IActionResult Stop([FromForm] Guid id)
    {
        if (!ModelState.IsValid || id == Guid.Empty)
            return BadRequest(new { message = "Geçerli araştırma kimliği gerekli." });
        return research.Stop(conversations.Owner, id) is { } snapshot
            ? Json(snapshot)
            : Conflict(new { message = "Araştırma artık etkin değil veya bu sohbete ait değil. Durumu yenileyin." });
    }

    [HttpPost("/research/approve")]
    public IActionResult Approve([FromForm] Guid id, [FromForm] Guid approvalId, [FromForm] bool? approve)
    {
        if (!ModelState.IsValid || id == Guid.Empty || approvalId == Guid.Empty || approve is null)
            return BadRequest(new { message = "Araştırma kimliği, onay kimliği ve karar gerekli." });
        return research.Approve(conversations.Owner, id, approvalId, approve.Value) is { } snapshot
            ? Json(snapshot)
            : Conflict(new { message = "Bu kaynak onayı artık geçerli değil. Araştırma durumunu yenileyin." });
    }
}
