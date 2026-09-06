using Microsoft.AspNetCore.Mvc;

namespace WikiCopilotAssistant.IntegrationTests;

public sealed class FailureController : Controller
{
    [Route("/test/failure")]
    public IActionResult Fail() =>
        throw new InvalidOperationException("Sensitive test failure: internal details must not reach the response.");
}
