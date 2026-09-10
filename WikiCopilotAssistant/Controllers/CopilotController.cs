using Microsoft.AspNetCore.Mvc;
using WikiCopilotAssistant.Services;

namespace WikiCopilotAssistant.Controllers;

[Route("copilot")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CopilotController(CopilotConnectionService connection) : Controller
{
    [HttpGet("status")]
    public IActionResult Status() => Json(connection.Status);

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken) =>
        Json(await connection.RefreshAsync(cancellationToken));
}
