using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WikiCopilotAssistant.Services.Sources;

public sealed record SourceLink(string Title, string Url);

public sealed record SourceDocument(
    string Url,
    string Title,
    string Text,
    string Method,
    IReadOnlyList<SourceLink> Links,
    string? Author = null,
    string? License = null,
    bool Truncated = false);

public sealed record SourceReadResult(
    string Status,
    IReadOnlyList<SourceDocument> Documents,
    string? Message = null,
    string? RequiredHost = null);

internal sealed class SourceAccessException(
    string status, string message, string? requiredHost = null) : Exception(message)
{
    public string Status { get; } = status;
    public string? RequiredHost { get; } = requiredHost;
}

/// <summary>Exact-host consent and public-address validation; consent never authorizes private networks.</summary>
public sealed class SourceAccessPolicy
{
    private readonly ConcurrentDictionary<string, byte> approvedHosts = new(StringComparer.Ordinal);
    internal const string StackExchangeHost = "api.stackexchange.com";

    public SourceAccessPolicy(Uri initialUrl)
    {
        ArgumentNullException.ThrowIfNull(initialUrl);
        var host = ValidateUrl(initialUrl);
        if (host == StackExchangeHost)
            throw new ArgumentException("The Stack Exchange API is available only through a Stack Overflow source URL.", nameof(initialUrl));
        approvedHosts.TryAdd(host, 0);
    }

    public void ApproveHost(string host)
    {
        var normalized = NormalizeHost(host);
        if (normalized == StackExchangeHost)
            throw new ArgumentException("The Stack Exchange API cannot be approved as an arbitrary source.", nameof(host));
        ValidateHost(normalized);
        approvedHosts.TryAdd(normalized, 0);
    }

    internal void Check(Uri uri, bool stackExchangeApi = false)
    {
        var host = ValidateUrl(uri);
        if (stackExchangeApi)
        {
            if (host != StackExchangeHost || uri.Scheme != Uri.UriSchemeHttps ||
                !approvedHosts.ContainsKey("stackoverflow.com"))
                throw new SourceAccessException("unsupported", "The internal API is restricted to approved Stack Overflow sources.");
            return;
        }

        if (host == StackExchangeHost)
            throw new SourceAccessException("unsupported", "Choose a specific stackoverflow.com question or answer URL, not an API URL.");
        if (!approvedHosts.ContainsKey(host))
            throw new SourceAccessException("needs_approval", $"Approval is required for the exact host '{host}'.", host);
    }

    internal async Task<IPAddress[]> ResolveAsync(Uri uri, bool stackExchangeApi, CancellationToken cancellationToken)
    {
        Check(uri, stackExchangeApi);
        var host = NormalizeHost(uri.IdnHost);
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new SourceAccessException("unavailable", $"'{host}' does not resolve exclusively to public IP addresses.");
        return addresses;
    }

    internal static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        host = host.Trim().TrimEnd('.');
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];
        if (host.Contains('%'))
            throw new ArgumentException("Scoped IP addresses are not supported.", nameof(host));
        if (IPAddress.TryParse(host, out var address))
            return address.ToString().ToLowerInvariant();
        var normalized = new IdnMapping().GetAscii(host).ToLowerInvariant();
        if (Uri.CheckHostName(normalized) != UriHostNameType.Dns || normalized.Contains(':') ||
            normalized.Contains('/') || normalized.Contains('\\'))
            throw new ArgumentException("Supply only a hostname, without a scheme, port or path.", nameof(host));
        return normalized;
    }

    private static string ValidateUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            throw new SourceAccessException("unsupported", "Only public HTTP(S) URLs on default ports, without user information, are supported.");
        string host;
        try
        {
            host = NormalizeHost(uri.IdnHost);
        }
        catch (ArgumentException)
        {
            throw new SourceAccessException("unsupported", "The URL has an invalid or scoped hostname.");
        }
        ValidateHost(host);
        return host;
    }

    private static void ValidateHost(string host)
    {
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal) ||
            host.EndsWith(".internal", StringComparison.Ordinal) ||
            host.EndsWith(".home.arpa", StringComparison.Ordinal) ||
            (!host.Contains('.') && !host.Contains(':')) ||
            (IPAddress.TryParse(host, out var address) && !IsPublicAddress(address)))
            throw new SourceAccessException("unsupported", "Local, private, reserved and metadata network destinations are not supported.");
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsPublicAddress(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            return !(a is 0 or 10 or 127 || a >= 224 ||
                (a == 100 && b is >= 64 and <= 127) ||
                (a == 169 && b == 254) ||
                (a == 168 && b == 63 && c == 129 && bytes[3] == 16) ||
                (a == 172 && b is >= 16 and <= 31) ||
                (a == 192 && (b == 168 || (b == 0 && c is 0 or 2) || (b == 88 && c == 99))) ||
                (a == 198 && (b is 18 or 19 || (b == 51 && c == 100))) ||
                (a == 203 && b == 0 && c == 113));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0)
            return false;
        // Only global unicast; also reject transition mechanisms and documentation ranges.
        return (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 0x01) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }
}
