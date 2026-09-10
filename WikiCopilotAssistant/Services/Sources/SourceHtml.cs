using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace WikiCopilotAssistant.Services.Sources;

internal static class SourceHtml
{
    internal const int MaxTextCharacters = 32_000;
    private const int MaxLinks = 128;
    private static readonly HashSet<string> Blocks =
        ["p", "div", "section", "article", "main", "li", "ul", "ol", "blockquote", "table", "tr", "dl", "dt", "dd"];

    internal static SourceDocument Extract(string html, Uri url, string method, string? title = null,
        string? author = null, string? license = null, bool alreadyTruncated = false)
    {
        // A parser alone has no browsing context, resource loader, or script execution.
        using var document = new HtmlParser().ParseDocument(html);
        title ??= document.Title;
        var linkBase = url;
        if (document.QuerySelector("base[href]")?.GetAttribute("href") is { } baseHref &&
            Uri.TryCreate(url, baseHref, out var declaredBase) &&
            declaredBase.Scheme is "http" or "https" && declaredBase.UserInfo.Length == 0 && declaredBase.IsDefaultPort)
            linkBase = declaredBase;
        foreach (var noise in document.QuerySelectorAll(
                     "script,style,noscript,nav,header,footer,aside,template,svg,form,[hidden],[aria-hidden=true]"))
            noise.Remove();
        var root = document.QuerySelector("main") ?? document.QuerySelector("article") ?? document.Body ?? document.DocumentElement;
        if (url.Fragment.Length > 1)
        {
            var anchor = document.GetElementById(Uri.UnescapeDataString(url.Fragment[1..]));
            if (anchor is null || root is null || !root.Contains(anchor))
                throw new SourceAccessException("unsupported", "The requested section anchor was not found in the readable document. Read the page without its fragment or choose one of its actual section links.");
            var heading = anchor.Closest("h1,h2,h3,h4,h5,h6");
            if (heading is null)
            {
                root = anchor;
            }
            else
            {
                var nextHeading = root.QuerySelectorAll("h1,h2,h3,h4,h5,h6")
                    .SkipWhile(element => element != heading).Skip(1)
                    .FirstOrDefault(element => element.LocalName[1] <= heading.LocalName[1]);
                var range = document.CreateRange();
                range.StartBefore(heading);
                if (nextHeading is not null)
                    range.EndBefore(nextHeading);
                else
                    range.EndAfter(root);
                var section = document.CreateElement("section");
                section.AppendChild(range.CopyContent());
                root = section;
            }
        }
        var builder = new StringBuilder();
        var truncated = alreadyTruncated;
        if (root is not null)
            Append(root, builder, ref truncated);
        var text = builder.ToString().Trim();
        var links = new List<SourceLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (root is not null)
        {
            foreach (var anchor in root.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href) || href.Length > 4096 ||
                    !Uri.TryCreate(linkBase, href, out var destination) ||
                    (destination.Scheme != Uri.UriSchemeHttps && destination.Scheme != Uri.UriSchemeHttp) ||
                    !destination.IsDefaultPort || destination.UserInfo.Length != 0 ||
                    !seen.Add(destination.AbsoluteUri))
                    continue;
                if (links.Count == MaxLinks)
                {
                    truncated = true;
                    break;
                }
                var label = Regex.Replace(anchor.TextContent, @"\s+", " ").Trim();
                links.Add(new SourceLink(Limit(label.Length == 0 ? destination.AbsoluteUri : label, 256),
                    destination.AbsoluteUri));
            }
        }
        return new SourceDocument(url.AbsoluteUri, Limit(title ?? "", 512), text, method, links, author, license, truncated);
    }

    internal static SourceDocument PlainText(string text, Uri url, string method) =>
        new(url.AbsoluteUri, "", Limit(text, MaxTextCharacters), method, [], Truncated: text.Length > MaxTextCharacters);

    internal static string? AccessBarrier(string html)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var title = (document.Title ?? "").ToLowerInvariant();
        var text = (document.Body?.TextContent ?? "").ToLowerInvariant();
        if (title.Contains("just a moment") || title.Contains("access denied") ||
            title.Contains("attention required") || title.Contains("security check") ||
            title.Contains("verify you are human") || title.Contains("captcha") ||
            document.QuerySelector("#challenge-form, #cf-challenge-running, iframe[src*='captcha'], .g-recaptcha, .h-captcha") is not null ||
            (text.Length < 12_000 && (text.Contains("verify you are human") ||
                                     text.Contains("checking your browser before accessing") ||
                                     text.Contains("enable javascript and cookies to continue"))))
            return "The source presents an access challenge or CAPTCHA. It will not be bypassed.";
        if (document.QuerySelector("input[type=password], [data-testid=paywall], #paywall, .paywall") is not null ||
            (text.Length < 4_000 && (text.Contains("sign in to continue") || text.Contains("log in to continue") ||
                                   text.Contains("subscribe to continue reading"))))
            return "The source presents a login or paywall. Only anonymously public content is supported.";
        return null;
    }

    internal static bool LooksJavaScriptOnly(string html, SourceDocument extracted)
    {
        using var document = new HtmlParser().ParseDocument(html);
        return extracted.Text.Length < 250 && document.QuerySelector("script") is not null &&
            (document.QuerySelector("#root, #app, #__next, app-root") is not null ||
             extracted.Text.Length < 100 ||
             html.Contains("enable JavaScript", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("requires JavaScript", StringComparison.OrdinalIgnoreCase));
    }

    private static void Append(INode node, StringBuilder builder, ref bool truncated, bool preformatted = false, int depth = 0)
    {
        if (builder.Length >= MaxTextCharacters || depth > 128)
        {
            truncated = true;
            return;
        }
        if (node is IText text)
        {
            Write(preformatted ? text.Data : Regex.Replace(text.Data, @"\s+", " "), builder, ref truncated);
            return;
        }
        var name = (node as IElement)?.LocalName ?? "";
        var heading = name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6';
        if (name == "br")
            Write("\n", builder, ref truncated);
        if (Blocks.Contains(name) || heading)
            Write("\n\n", builder, ref truncated);
        if (heading)
            Write(new string('#', name[1] - '0') + " ", builder, ref truncated);
        if (name == "li")
            Write("- ", builder, ref truncated);
        if (name == "pre")
            Write("\n```\n", builder, ref truncated);
        if (name == "code" && !preformatted)
            Write("`", builder, ref truncated);
        foreach (var child in node.ChildNodes)
        {
            Append(child, builder, ref truncated, preformatted || name == "pre", depth + 1);
            if (builder.Length >= MaxTextCharacters)
            {
                truncated = true;
                break;
            }
        }
        if (name == "pre")
            Write("\n```\n", builder, ref truncated);
        if (name == "code" && !preformatted)
            Write("`", builder, ref truncated);
        if (name is "td" or "th")
            Write("\t", builder, ref truncated);
        if (Blocks.Contains(name) || heading)
            Write("\n\n", builder, ref truncated);
    }

    private static void Write(string value, StringBuilder builder, ref bool truncated)
    {
        var remaining = MaxTextCharacters - builder.Length;
        if (value.Length > remaining)
            truncated = true;
        builder.Append(value.AsSpan(0, Math.Min(remaining, value.Length)));
    }

    private static string Limit(string value, int maximum) => value[..Math.Min(value.Length, maximum)];
}
