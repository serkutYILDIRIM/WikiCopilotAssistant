using Microsoft.AspNetCore.Mvc;

namespace WikiCopilotAssistant.Web.Controllers;

public sealed class HomeController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var result = View("Error");
        result.StatusCode = StatusCodes.Status500InternalServerError;
        return result;
    }
}
