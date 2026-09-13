using System.Net;

namespace WikiCopilotAssistant.Services;

internal static class LocalAccess
{
    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    public static bool IsAllowed(HttpRequest request, IPAddress? remoteAddress)
    {
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress) || !IsLoopbackHost(request.Host.Host))
            return false;

        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
             !string.Equals(uri.GetLeftPart(UriPartial.Authority),
                 $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)))
            return false;

        return request.Headers["Sec-Fetch-Site"].ToString() != "cross-site";
    }
}
