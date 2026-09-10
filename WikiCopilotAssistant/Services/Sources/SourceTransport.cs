using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WikiCopilotAssistant.Services.Sources;

internal sealed record SourceResponse(Uri Url, int StatusCode, string ContentType, byte[] Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public string Text
    {
        get
        {
            var charset = ContentType.Split(';').Select(part => part.Trim())
                .FirstOrDefault(part => part.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))?[8..].Trim('"', '\'');
            try
            {
                return (charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset)).GetString(Body);
            }
            catch (ArgumentException)
            {
                throw new SourceAccessException("unsupported", "The source declares an unsupported text encoding.");
            }
        }
    }
}

/// <summary>All source sockets, including browser requests, are connected to validated, pinned addresses.</summary>
internal sealed class SourceTransport(SourceAccessPolicy policy)
{
    internal const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxRedirects = 10;

    internal Task<SourceResponse> GetAsync(Uri uri, CancellationToken cancellationToken, Action<int>? consumeBytes = null) =>
        GetCoreAsync(uri, false, cancellationToken, consumeBytes);

    internal Task<SourceResponse> GetStackExchangeAsync(string relativePath, CancellationToken cancellationToken) =>
        GetCoreAsync(new Uri($"https://{SourceAccessPolicy.StackExchangeHost}/2.3/{relativePath}"), true, cancellationToken);

    private async Task<SourceResponse> GetCoreAsync(Uri uri, bool stackExchangeApi, CancellationToken cancellationToken,
        Action<int>? consumeBytes = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                var addresses = await policy.ResolveAsync(uri, stackExchangeApi, deadline.Token);
                using var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false,
                    Credentials = null,
                    PreAuthenticate = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                    MaxResponseHeadersLength = 32,
                    ConnectCallback = async (context, token) =>
                    {
                        if (SourceAccessPolicy.NormalizeHost(context.DnsEndPoint.Host) != SourceAccessPolicy.NormalizeHost(uri.IdnHost) ||
                            context.DnsEndPoint.Port != uri.Port)
                            throw new SourceAccessException("unavailable", "The transport attempted to connect to an unexpected destination.");
                        SocketException? lastError = null;
                        foreach (var address in addresses)
                        {
                            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                            var transferredOwnership = false;
                            try
                            {
                                await socket.ConnectAsync(new IPEndPoint(address, uri.Port), token);
                                var stream = new NetworkStream(socket, ownsSocket: true);
                                transferredOwnership = true;
                                return stream;
                            }
                            catch (SocketException error)
                            {
                                lastError = error;
                            }
                            finally
                            {
                                if (!transferredOwnership)
                                    socket.Dispose();
                            }
                        }
                        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
                    }
                };
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                // No browser or Copilot headers (especially cookies and authorization) are forwarded.
                request.Headers.UserAgent.ParseAdd("WikiCopilotAssistant/0.1");
                if (stackExchangeApi)
                    request.Headers.Accept.ParseAdd("application/json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects >= MaxRedirects)
                        throw new SourceAccessException("unavailable", "The source exceeded the ten-hop redirect safety limit.");
                    if (response.Headers.Location is not { } location)
                        throw new SourceAccessException("unavailable", "The source returned a redirect without a destination.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    policy.Check(uri, stackExchangeApi);
                    continue;
                }

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    throw new SourceAccessException("unavailable", $"The source response exceeds the {MaxResponseBytes / 1024 / 1024} MiB safety limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var body = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), deadline.Token)) != 0)
                    {
                        if (body.Length + read > MaxResponseBytes)
                            throw new SourceAccessException("unavailable", "The decompressed source response exceeds the 4 MiB safety limit.");
                        consumeBytes?.Invoke(read);
                        body.Write(buffer, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    // Decompressed bodies are fulfilled without wire encoding, cookies, auth or reporting side effects.
                    if (header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Alt-Svc", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Report-To", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("NEL", StringComparison.OrdinalIgnoreCase))
                        continue;
                    headers[header.Key] = string.Join(", ", header.Value);
                }
                return new SourceResponse(uri, status, response.Content.Headers.ContentType?.ToString() ?? "",
                    body.ToArray(), headers);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceAccessException("unavailable", "The source request exceeded its 25-second network deadline.");
        }
        catch (HttpRequestException error)
        {
            throw new SourceAccessException("unavailable", $"Public source transport failure ({error.HttpRequestError}). No credentials or browser session were used.");
        }
        catch (SocketException error)
        {
            throw new SourceAccessException("unavailable", $"Public source DNS or connection failed: {error.SocketErrorCode}.");
        }
    }
}
