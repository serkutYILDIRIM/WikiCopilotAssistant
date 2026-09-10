using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace WikiCopilotAssistant.Services.Sources;

internal static class SourceBrowser
{
    private const long MaxOperationBytes = 16 * 1024 * 1024;
    private static readonly string[] NonContentHosts =
        ["googletagmanager.com", "google-analytics.com", "clarity.ms", "static.cloudflareinsights.com",
            "fonts.googleapis.com", "fonts.gstatic.com"];

    internal static async Task<SourceReadResult> ReadAsync(SourceResponse initial, SourceTransport transport,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        var errors = new ConcurrentQueue<SourceAccessException>();
        var routes = new ConcurrentBag<Task>();
        var stopping = 0;
        long transferred = 0;
        var initialUsed = 0;
        var blockedAnalytics = 0;
        var finalUrl = initial.Url;
        string? html = null;
        var settled = true;
        var stage = "startup";
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPlaywright? playwright = null;
        IPage? mainPage = null;

        // A bound, non-listening loopback socket reserves a fail-closed proxy destination.
        // Requests not intercepted by Playwright cannot fall through to the real network.
        using var blockedProxy = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true
        };
        blockedProxy.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var proxyPort = ((IPEndPoint)blockedProxy.LocalEndPoint!).Port;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "msedge",
                Headless = true,
                ChromiumSandbox = true,
                Timeout = 20_000,
                Proxy = new Proxy { Server = $"http://127.0.0.1:{proxyPort}", Bypass = "<-loopback>" },
                Args =
                [
                    "--enable-automation",
                    "--disable-background-networking",
                    "--disable-background-mode",
                    "--disable-component-update",
                    "--disable-extensions",
                    "--disable-domain-reliability",
                    "--disable-sync",
                    "--disable-quic",
                    "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                    "--no-first-run"
                ]
            });
            deadline.Token.ThrowIfCancellationRequested();
            stage = "profile_verification";
            await VerifyTemporaryProfileAsync(browser, deadline.Token);
            stage = "context_creation";
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                AcceptDownloads = false,
                ServiceWorkers = ServiceWorkerPolicy.Block,
                IgnoreHTTPSErrors = false,
                Permissions = []
            });
            context.SetDefaultTimeout(15_000);
            context.SetDefaultNavigationTimeout(25_000);

            void ConsumeBytes(int count)
            {
                if (Interlocked.Add(ref transferred, count) > MaxOperationBytes)
                    throw new SourceAccessException("unavailable", "The browser read exceeded its 16 MiB aggregate response safety limit.");
            }

            async Task AbortAsync(IRoute route)
            {
                try
                {
                    await route.AbortAsync("blockedbyclient");
                }
                catch (PlaywrightException)
                {
                    if (Volatile.Read(ref stopping) == 0)
                        errors.Enqueue(new SourceAccessException("unavailable", "Browser request blocking failed (browser_route_abort_failed)."));
                }
            }

            async Task RouteAsync(IRoute route)
            {
                try
                {
                    var request = route.Request;
                    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        throw new SourceAccessException("unsupported", "A browser request used a non-HTTP(S) protocol; it was blocked.");
                    if (IsNonContentHost(uri.IdnHost))
                    {
                        Interlocked.Increment(ref blockedAnalytics);
                        await AbortAsync(route);
                        return;
                    }
                    if (request.ResourceType is "image" or "media" or "font")
                    {
                        await AbortAsync(route);
                        return;
                    }
                    if (request.Method is not ("GET" or "HEAD"))
                        throw new SourceAccessException("unsupported", $"The public page requires a {request.Method} request. Only anonymous, read-only GET/HEAD requests are supported.");

                    var useInitial = request.IsNavigationRequest && request.Frame.Page == mainPage &&
                                   request.Frame == mainPage.MainFrame &&
                                   SameNetworkUrl(uri, initial.Url) && Interlocked.Exchange(ref initialUsed, 1) == 0;
                    if (useInitial)
                        ConsumeBytes(initial.Body.Length);
                    var response = useInitial
                        ? initial
                        : await transport.GetAsync(uri, deadline.Token, ConsumeBytes);
                    SourceReader.RequireSuccessfulResponse(response);
                    if (SourceReader.IsHtml(response.ContentType) && SourceHtml.AccessBarrier(response.Text) is { } barrier)
                        throw new SourceAccessException("unavailable", barrier);
                    if (response.Headers.TryGetValue("Content-Disposition", out var disposition) &&
                        disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase))
                        throw new SourceAccessException("unsupported", "Downloads are not supported by the public source reader.");
                    if (request.IsNavigationRequest && request.Frame.Page == mainPage && request.Frame == mainPage.MainFrame)
                        finalUrl = response.Url;

                    var headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase);
                    headers.Remove("Refresh");
                    // Keep any server CSP and add a second policy to disallow forms, frames,
                    // workers and plugin content. Scripts and public fetches still pass our route.
                    const string isolationPolicy = "default-src http: https: 'unsafe-inline' 'unsafe-eval'; object-src 'none'; frame-src 'none'; worker-src 'none'; form-action 'none'";
                    headers["Content-Security-Policy"] = headers.TryGetValue("Content-Security-Policy", out var existingPolicy)
                        ? existingPolicy + ", " + isolationPolicy
                        : isolationPolicy;
                    var body = response.Body;
                    if (SourceReader.IsHtml(response.ContentType))
                    {
                        // Manual, policy-checked redirects are never delegated back to Chromium.
                        // A base URL preserves relative HTML references at their final source.
                        var source = response.Text;
                        if (!SameNetworkUrl(uri, response.Url))
                        {
                            var baseElement = $"<base href=\"{WebUtility.HtmlEncode(response.Url.AbsoluteUri)}\">";
                            var head = source.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
                            var headEnd = head >= 0 ? source.IndexOf('>', head) : -1;
                            source = headEnd >= 0 ? source.Insert(headEnd + 1, baseElement) : baseElement + source;
                        }
                        body = Encoding.UTF8.GetBytes(source);
                        headers["Content-Type"] = "text/html; charset=utf-8";
                    }
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = response.StatusCode,
                        Headers = headers,
                        BodyBytes = request.Method == "HEAD" ? [] : body
                    });
                }
                catch (SourceAccessException error)
                {
                    errors.Enqueue(error);
                    await AbortAsync(route);
                }
                catch (OperationCanceledException)
                {
                    if (Volatile.Read(ref stopping) == 0)
                        errors.Enqueue(new SourceAccessException("unavailable", "The browser source request was cancelled or timed out."));
                    await AbortAsync(route);
                }
                catch (PlaywrightException)
                {
                    if (Volatile.Read(ref stopping) == 0)
                        errors.Enqueue(new SourceAccessException("unavailable", "The browser could not fulfill a guarded request (browser_route_fulfillment_failed)."));
                    await AbortAsync(route);
                }
            }

            async Task CloseWebSocketAsync(IWebSocketRoute socket)
            {
                errors.Enqueue(new SourceAccessException("unsupported", "The page requested a WebSocket. WebSockets are blocked by the public source reader."));
                try
                {
                    await socket.CloseAsync();
                }
                catch (PlaywrightException)
                {
                    if (Volatile.Read(ref stopping) == 0)
                        errors.Enqueue(new SourceAccessException("unavailable", "WebSocket blocking failed (browser_websocket_close_failed)."));
                }
            }

            Func<IRoute, Task> routeHandler = route =>
            {
                var task = RouteAsync(route);
                routes.Add(task);
                return task;
            };
            await context.RouteAsync("**/*", routeHandler);
            await context.RouteWebSocketAsync(new System.Text.RegularExpressions.Regex(".*"), socket => routes.Add(CloseWebSocketAsync(socket)));
            mainPage = await context.NewPageAsync();
            mainPage.Download += (_, _) => errors.Enqueue(new SourceAccessException("unsupported", "The page attempted a download; downloads are disabled."));
            stage = "navigation";
            await mainPage.GotoAsync(initial.Url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
                .WaitAsync(deadline.Token);
            stage = "page_settling";
            try
            {
                await mainPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 8_000 })
                    .WaitAsync(deadline.Token);
            }
            catch (System.TimeoutException)
            {
                settled = false;
            }
            if (!Uri.TryCreate(mainPage.Url, UriKind.Absolute, out var browserUrl) ||
                browserUrl.Scheme is not ("http" or "https"))
                throw new SourceAccessException("unsupported", "The browser left its public HTTP(S) document.");
            stage = "snapshot";
            html = await mainPage.EvaluateAsync<string>(
                "limit => document.documentElement.outerHTML.slice(0, limit)", SourceTransport.MaxResponseBytes)
                .WaitAsync(deadline.Token);
        }
        catch (PlaywrightException)
        {
            errors.Enqueue(new SourceAccessException("unavailable", browser is null
                ? "A fresh installed Microsoft Edge (msedge) browser could not be launched (browser_launch_failed). Install/configure Edge separately; this application never downloads browsers or uses an existing profile."
                : $"The isolated browser read failed (browser_{stage}_failed). No existing browser session was used."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            errors.Enqueue(new SourceAccessException("unavailable", "The isolated browser exceeded its 50-second deadline."));
        }
        finally
        {
            Interlocked.Exchange(ref stopping, 1);
            await deadline.CancelAsync();
            try
            {
                if (context is not null)
                    await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (PlaywrightException)
            {
                errors.Enqueue(new SourceAccessException("unavailable", "Browser context cleanup failed (browser_context_close_failed)."));
            }
            catch (System.TimeoutException)
            {
                errors.Enqueue(new SourceAccessException("unavailable", "Browser context cleanup exceeded its deadline."));
            }
            finally
            {
                if (browser is not null)
                {
                    try
                    {
                        await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    }
                    catch (PlaywrightException)
                    {
                        errors.Enqueue(new SourceAccessException("unavailable", "Browser cleanup failed (browser_close_failed)."));
                    }
                    catch (System.TimeoutException)
                    {
                        errors.Enqueue(new SourceAccessException("unavailable", "Browser cleanup exceeded its deadline."));
                    }
                }
            }
            try
            {
                await Task.WhenAll(routes.ToArray()).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (System.TimeoutException)
            {
                errors.Enqueue(new SourceAccessException("unavailable", "Guarded browser requests did not finish within the cleanup deadline."));
            }
            finally
            {
                playwright?.Dispose();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var failure = errors.FirstOrDefault(error => error.Status == "needs_approval") ?? errors.FirstOrDefault();
        if (failure is not null)
            return SourceReader.Failure(failure.Status, failure.Message, failure.RequiredHost);
        if (html is null)
            return SourceReader.Failure("unavailable", "The isolated browser did not produce a readable document.");
        if (SourceHtml.AccessBarrier(html) is { } finalBarrier)
            return SourceReader.Failure("unavailable", finalBarrier);
        var document = SourceHtml.Extract(html, finalUrl, "browser_edge",
            alreadyTruncated: html.Length >= SourceTransport.MaxResponseBytes);
        if (document.Text.Length == 0 || SourceHtml.LooksJavaScriptOnly(html, document))
            return SourceReader.Failure("unavailable", "The isolated browser did not produce readable public source content.");
        var message = settled
            ? "Read a fresh anonymous Edge DOM snapshot. Images, media, fonts, frames, workers, downloads and WebSockets are disabled; lazy or interactive content was not expanded."
            : "Read a fresh anonymous Edge DOM snapshot before network-idle was reached. Rendering may be incomplete; lazy or interactive content was not expanded.";
        if (blockedAnalytics > 0)
            message += " Known analytics and remote-font hosts were blocked without contacting them.";
        return SourceReader.Success([document], message);
    }

    private static bool IsNonContentHost(string host) =>
        NonContentHosts.Any(blocked => string.Equals(host, blocked, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase));

    private static async Task VerifyTemporaryProfileAsync(IBrowser browser, CancellationToken cancellationToken)
    {
        var session = await browser.NewBrowserCDPSessionAsync().WaitAsync(cancellationToken);
        try
        {
            var response = await session.SendAsync("Browser.getBrowserCommandLine").WaitAsync(cancellationToken);
            const string prefix = "--user-data-dir=";
            var profile = response is { } value &&
                value.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array
                ? arguments.EnumerateArray()
                    .Where(argument => argument.ValueKind == JsonValueKind.String)
                    .Select(argument => argument.GetString())
                    .FirstOrDefault(argument => argument?.StartsWith(prefix, StringComparison.Ordinal) == true)?[prefix.Length..]
                : null;
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + Path.DirectorySeparatorChar;
            if (profile is null || !Path.GetFullPath(profile).StartsWith(tempRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(Path.TrimEndingDirectorySeparator(profile))
                    .StartsWith("playwright_", StringComparison.Ordinal))
                throw new SourceAccessException("unavailable", "The browser's fresh temporary profile could not be verified. Source navigation was not started.");
        }
        catch (ArgumentException)
        {
            throw new SourceAccessException("unavailable", "The browser returned an invalid temporary-profile location. Source navigation was not started.");
        }
        catch (PlaywrightException error)
        {
            var category = error.Message.Contains("enable-automation", StringComparison.OrdinalIgnoreCase)
                ? "automation_metadata_disabled"
                : error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? "profile_metadata_unsupported"
                    : "profile_metadata_unavailable";
            throw new SourceAccessException("unavailable", $"The browser's temporary profile could not be verified ({category}). Source navigation was not started.");
        }
        finally
        {
            try
            {
                await session.DetachAsync();
            }
            catch (PlaywrightException)
            {
                throw new SourceAccessException("unavailable", "The temporary-profile verification session could not be detached safely (profile_verification_cleanup_failed).");
            }
        }
    }

    private static bool SameNetworkUrl(Uri first, Uri second) =>
        Uri.Compare(first, second, UriComponents.HttpRequestUrl, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;
}
