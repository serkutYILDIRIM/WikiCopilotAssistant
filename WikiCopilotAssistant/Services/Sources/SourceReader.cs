using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WikiCopilotAssistant.Services.Sources;

/// <summary>Reads approved, anonymously public sources without sharing credentials with them.</summary>
public sealed class SourceReader : IAsyncDisposable
{
    private readonly SourceAccessPolicy policy;
    private readonly SourceTransport transport;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> apiBackoff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (byte[] Body, DateTimeOffset Received)> apiCache = new(StringComparer.Ordinal);
    private DateTimeOffset apiUnavailableUntil;
    private int disposed;

    public SourceReader(Uri initialUrl)
    {
        policy = new SourceAccessPolicy(initialUrl);
        transport = new SourceTransport(policy);
    }

    public void ApproveHost(string host)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        policy.ApproveHost(host);
    }

    public async Task<SourceReadResult> ReadAsync(string url, bool useBrowser = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Failure("unsupported", "Supply an absolute public HTTP(S) source URL of at most 4096 characters.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(90));
        var entered = false;
        try
        {
            // Approval is checked before DNS, browser launch, or any source network operation.
            policy.Check(uri);
            await readGate.WaitAsync(operation.Token);
            entered = true;
            if (SourceAccessPolicy.NormalizeHost(uri.IdnHost) == "stackoverflow.com")
                return await ReadStackOverflowAsync(uri, operation.Token);

            var response = await transport.GetAsync(uri, operation.Token);
            RequireSuccessfulResponse(response);
            if (IsHtml(response.ContentType))
            {
                var html = response.Text;
                if (SourceHtml.AccessBarrier(html) is { } barrier)
                    return Failure("unavailable", barrier);
                var document = SourceHtml.Extract(html, response.Url, "http_html");
                if (useBrowser || SourceHtml.LooksJavaScriptOnly(html, document))
                    return await SourceBrowser.ReadAsync(response, transport, operation.Token);
                if (document.Text.Length == 0)
                    return Failure("unavailable", "The source returned HTML but no readable source text.");
                return Success([document]);
            }
            if (response.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ||
                response.ContentType.StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(response.Text)
                    ? Failure("unavailable", "The source returned an empty text document.")
                    : Success([SourceHtml.PlainText(response.Text, response.Url, "http_text")]);
            return Failure("unsupported", "Unsupported source content type. Choose an HTML or plain-text source.");
        }
        catch (SourceAccessException error)
        {
            return Failure(error.Status, error.Message, error.RequiredHost);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("unavailable", lifetime.IsCancellationRequested
                ? "The source reader was disposed."
                : "The source read exceeded its 90-second operation deadline.");
        }
        finally
        {
            if (entered)
                readGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        await lifetime.CancelAsync();
        await readGate.WaitAsync();
        apiCache.Clear();
        apiBackoff.Clear();
        readGate.Release();
        // The gate and token source remain valid for callers already waiting during disposal.
    }

    private async Task<SourceReadResult> ReadStackOverflowAsync(Uri uri, CancellationToken cancellationToken)
    {
        var path = uri.AbsolutePath;
        var answerMatch = Regex.Match(path, @"^/(?:a|answers)/([1-9][0-9]*)(?:/|$)", RegexOptions.CultureInvariant);
        var questionMatch = Regex.Match(path, @"^/(?:q|questions)/([1-9][0-9]*)(?:/|$)", RegexOptions.CultureInvariant);
        if (!answerMatch.Success && !questionMatch.Success)
            return Failure("unsupported", "For Stack Overflow, use native web search to choose a specific question (/questions/id or /q/id) or answer (/a/id). Question indexes are not source evidence.");

        var page = ReadPage(uri);
        var messages = new List<string>();
        var documents = new List<SourceDocument>();
        string? answerId = answerMatch.Success ? answerMatch.Groups[1].Value : null;
        if (answerId is null && Regex.IsMatch(uri.Fragment, @"^#[1-9][0-9]*$", RegexOptions.CultureInvariant))
            answerId = uri.Fragment[1..];
        if (answerId is null)
        {
            var answerPath = Regex.Match(path, @"^/questions/[1-9][0-9]*/[^/]+/([1-9][0-9]*)(?:/|$)", RegexOptions.CultureInvariant);
            if (answerPath.Success)
                answerId = answerPath.Groups[1].Value;
        }

        JsonElement? selectedAnswer = null;
        var questionId = questionMatch.Success ? questionMatch.Groups[1].Value : "";
        if (answerId is not null)
        {
            using var result = await GetApiAsync($"answers/{answerId}?site=stackoverflow&filter=withbody", "answers", messages, cancellationToken);
            var items = GetItems(result.RootElement);
            if (items.GetArrayLength() == 0)
                return Failure("unavailable", "The official API did not return a public answer for this URL.");
            selectedAnswer = items[0].Clone();
            if (GetId(selectedAnswer.Value, "answer_id") != answerId)
                throw new SourceAccessException("unavailable", "The official API returned an unexpected answer identifier.");
            var actualQuestionId = GetId(selectedAnswer.Value, "question_id");
            if (questionMatch.Success && actualQuestionId != questionId)
                return Failure("unsupported", "The answer fragment does not belong to the selected question.");
            questionId = actualQuestionId;
        }

        using var question = await GetApiAsync($"questions/{questionId}?site=stackoverflow&filter=withbody", "questions", messages, cancellationToken);
        var questions = GetItems(question.RootElement);
        if (questions.GetArrayLength() == 0)
            return Failure("unavailable", "The official API did not return a public question for this URL.");
        var questionItem = questions[0];
        if (GetId(questionItem, "question_id") != questionId)
            throw new SourceAccessException("unavailable", "The official API returned an unexpected question identifier.");
        var title = WebUtility.HtmlDecode(GetString(questionItem, "title") ?? "");
        var questionUrl = ApiPermalink(questionItem, $"https://stackoverflow.com/questions/{questionId}");
        documents.Add(ApiDocument(questionItem, questionUrl, title));

        if (selectedAnswer is { } answer)
        {
            documents.Add(ApiDocument(answer, new Uri($"https://stackoverflow.com/a/{answerId}"), $"Answer to: {title}"));
        }
        else
        {
            using var answers = await GetApiAsync(
                $"questions/{questionId}/answers?site=stackoverflow&filter=withbody&sort=votes&order=desc&pagesize=10&page={page}",
                "questions/answers", messages, cancellationToken);
            foreach (var item in GetItems(answers.RootElement).EnumerateArray())
            {
                var id = GetId(item, "answer_id");
                if (GetId(item, "question_id") != questionId)
                    throw new SourceAccessException("unavailable", "The official API returned an answer for a different question.");
                documents.Add(ApiDocument(item, new Uri($"https://stackoverflow.com/a/{id}"), $"Answer to: {title}"));
            }
            if (answers.RootElement.TryGetProperty("has_more", out var hasMore) && hasMore.GetBoolean())
            {
                if (page == int.MaxValue)
                    messages.Add("The API reports more answers, but its page-number range has been exhausted.");
                else
                    messages.Add($"More answers are available. Read https://stackoverflow.com/questions/{questionId}?page={page + 1} to continue; only answer page {page} was read.");
            }
            else
                messages.Add($"Answer page {page} was read; the API reports no further pages.");
        }
        return Success(documents, string.Join(" ", messages));
    }

    private async Task<JsonDocument> GetApiAsync(string path, string method, List<string> messages, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (apiCache.TryGetValue(path, out var cached) && now - cached.Received < TimeSpan.FromMinutes(1))
        {
            messages.Add("Reused the official API response from a short-lived in-memory cache.");
            return JsonDocument.Parse(cached.Body);
        }
        var until = apiBackoff.GetValueOrDefault(method);
        if (apiUnavailableUntil > until)
            until = apiUnavailableUntil;
        if (until > now)
            throw new SourceAccessException("unavailable", $"The official Stack Exchange API requires waiting until {until:O} before this request. No retry or HTML/browser bypass was attempted.");
        var response = await transport.GetStackExchangeAsync(path, cancellationToken);
        if (response.StatusCode == 429)
        {
            var wait = now.AddMinutes(1);
            if (response.Headers.TryGetValue("Retry-After", out var retry))
            {
                if (int.TryParse(retry, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    wait = now.AddSeconds(seconds);
                else if (DateTimeOffset.TryParse(retry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    wait = date > now ? date : wait;
            }
            apiUnavailableUntil = wait;
        }
        if (response.StatusCode is < 200 or >= 300 &&
            !response.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            RequireSuccessfulResponse(response);
        JsonDocument result;
        var keepResult = false;
        try
        {
            result = JsonDocument.Parse(response.Body);
        }
        catch (JsonException)
        {
            throw new SourceAccessException("unavailable", $"The official Stack Exchange API returned an invalid JSON response (HTTP {response.StatusCode}).");
        }
        try
        {
            var root = result.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new SourceAccessException("unavailable", "The official API returned an unexpected response format.");
            if (root.TryGetProperty("backoff", out var backoff) && backoff.ValueKind == JsonValueKind.Number &&
                backoff.TryGetInt32(out var backoffSeconds) && backoffSeconds > 0)
            {
                apiBackoff[method] = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds);
                messages.Add($"The API requested a {backoffSeconds}-second backoff for {method}; this reader will honor it.");
            }
            if (root.TryGetProperty("quota_remaining", out var quota) && quota.ValueKind == JsonValueKind.Number &&
                quota.TryGetInt32(out var remaining))
            {
                messages.Add($"API quota remaining: {remaining}.");
                if (remaining <= 0)
                    apiUnavailableUntil = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);
            }
            if (root.TryGetProperty("error_id", out _))
            {
                var category = GetString(root, "error_name") switch
                {
                    "throttle_violation" => "api_throttle_violation",
                    "temporarily_unavailable" => "api_temporarily_unavailable",
                    "access_denied" => "api_access_denied",
                    "bad_parameter" => "api_bad_parameter",
                    "internal_error" => "api_internal_error",
                    _ => "api_error"
                };
                throw new SourceAccessException("unavailable", $"The official Stack Exchange API rejected the request ({category}). Any API backoff is enforced; no retry or browser bypass was attempted.");
            }
            RequireSuccessfulResponse(response);
            _ = GetItems(root);
            apiCache[path] = (response.Body, DateTimeOffset.UtcNow);
            while (apiCache.Count > 32 || apiCache.Values.Sum(entry => (long)entry.Body.Length) > 16 * 1024 * 1024)
                apiCache.Remove(apiCache.MinBy(entry => entry.Value.Received).Key);
            keepResult = true;
            return result;
        }
        finally
        {
            if (!keepResult)
                result.Dispose();
        }
    }

    private SourceDocument ApiDocument(JsonElement item, Uri url, string title)
    {
        policy.Check(url);
        var body = GetString(item, "body");
        if (body is null)
            throw new SourceAccessException("unavailable", "The official API omitted the source body; no snippet was substituted.");
        var author = item.TryGetProperty("owner", out var owner) ? GetString(owner, "display_name") : null;
        return SourceHtml.Extract(body, url, "stackoverflow_api", title,
            author is null ? null : WebUtility.HtmlDecode(author), GetString(item, "content_license"));
    }

    private static Uri ApiPermalink(JsonElement item, string fallback)
    {
        if (GetString(item, "link") is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && SourceAccessPolicy.NormalizeHost(uri.IdnHost) == "stackoverflow.com")
            return uri;
        // This is the documented question permalink, constructed only from an actual API question ID.
        return new Uri(fallback);
    }

    private static JsonElement GetItems(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new SourceAccessException("unavailable", "The official API response has no source items array.");
        return items;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string GetId(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var id) || id <= 0)
            throw new SourceAccessException("unavailable", $"The official API returned an invalid {name}.");
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static int ReadPage(Uri uri)
    {
        var pages = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => Uri.UnescapeDataString(parts[0]) == "page").ToArray();
        if (pages.Length == 0)
            return 1;
        if (pages.Length != 1 || pages[0].Length != 2 ||
            !int.TryParse(Uri.UnescapeDataString(pages[0][1]), NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1)
            throw new SourceAccessException("unsupported", "Stack Overflow answer continuation requires a positive integer ?page=n.");
        return page;
    }

    internal static bool IsHtml(string contentType) =>
        contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
        contentType.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    internal static void RequireSuccessfulResponse(SourceResponse response)
    {
        if (response.StatusCode is < 200 or >= 300)
            throw new SourceAccessException("unavailable", $"The source returned HTTP {response.StatusCode}. Access denial, rate limits, login, and paywall restrictions will not be bypassed.");
    }

    internal static SourceReadResult Success(IReadOnlyList<SourceDocument> documents, string? message = null)
    {
        if (documents.Any(document => document.Truncated))
            message = string.Join(" ", new[] { message, "Source text and/or links were truncated to response safety limits; these are not complete documents." }.Where(part => !string.IsNullOrEmpty(part)));
        return new SourceReadResult("success", documents, message);
    }

    internal static SourceReadResult Failure(string status, string message, string? requiredHost = null) =>
        new(status, [], message, requiredHost);
}
