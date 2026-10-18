using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GibiOMatic.Conversion.Reading;

/// <summary>
/// Makes a chapter of an EPUB safe to show: scripts, frames, plugins, refreshes and event
/// handlers are removed, and the page gets a Content-Security-Policy that blocks scripts and any
/// request outside the book (the same policy is also sent as an HTTP header).
/// </summary>
public static partial class EpubHtml
{
    /// <summary>Only the book's own files ('self' is the local reader server) and inline styles.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; media-src 'self'; " +
        "script-src 'none'; object-src 'none'; frame-src 'none'; connect-src 'none'; form-action 'none'; base-uri 'none'";

    private static readonly HashSet<string> RemovedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "base", "portal",
    };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "action", "formaction", "data", "background", "poster", "srcset",
    };

    /// <summary>The sanitized chapter and its media type (XHTML when it parses as XML, HTML otherwise).</summary>
    /// <param name="readerCss">Style sheet linked last, so the reader's font size and colors win.</param>
    public static (string Html, string ContentType) Sanitize(byte[] data, string readerCss)
    {
        var text = Decode(data);
        try
        {
            var doc = Parse(text);
            Clean(doc, readerCss);
            return (doc.ToString(SaveOptions.DisableFormatting), "application/xhtml+xml; charset=utf-8");
        }
        catch (XmlException)
        {
            return (CleanAsText(text, readerCss), "text/html; charset=utf-8");
        }
    }

    private static string Decode(byte[] data)
    {
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static XDocument Parse(string text)
    {
        // XHTML named entities (&nbsp;, &eacute;…) are not defined without the DTD: use their numbers.
        var withNumbers = NamedEntity().Replace(text, m => m.Groups[1].Value switch
        {
            "amp" or "lt" or "gt" or "quot" or "apos" => m.Value,
            _ => WebUtility.HtmlDecode(m.Value) is var decoded && decoded != m.Value
                ? string.Concat(decoded.EnumerateRunes().Select(r => $"&#{r.Value};"))
                : m.Value,
        });
        using var reader = XmlReader.Create(new StringReader(withNumbers),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static void Clean(XDocument doc, string readerCss)
    {
        var root = doc.Root ?? throw new XmlException("Empty document.");
        foreach (var element in root.DescendantsAndSelf().ToList())
        {
            var name = element.Name.LocalName;
            if (RemovedElements.Contains(name)
                || (name.Equals("meta", StringComparison.OrdinalIgnoreCase) && element.Attributes().Any(a => a.Name.LocalName.Equals("http-equiv", StringComparison.OrdinalIgnoreCase)))
                || (name.Equals("link", StringComparison.OrdinalIgnoreCase) && !IsStyleSheet(element)))
            {
                if (element != root)
                {
                    element.Remove();
                }

                continue;
            }

            foreach (var attribute in element.Attributes().ToList())
            {
                var attributeName = attribute.Name.LocalName;
                if (attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || (UrlAttributes.Contains(attributeName) && IsScriptUrl(attribute.Value)))
                {
                    attribute.Remove();
                }
            }
        }

        var ns = root.Name.Namespace;
        var head = root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("head", StringComparison.OrdinalIgnoreCase));
        if (head is null)
        {
            head = new XElement(ns + "head");
            root.AddFirst(head);
        }

        head.AddFirst(new XElement(ns + "meta", new XAttribute("http-equiv", "Content-Security-Policy"), new XAttribute("content", ContentSecurityPolicy)));
        head.Add(new XElement(ns + "link", new XAttribute("rel", "stylesheet"), new XAttribute("type", "text/css"), new XAttribute("href", readerCss)));
    }

    private static bool IsStyleSheet(XElement link) =>
        ((string?)link.Attribute("rel"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(r => r.Equals("stylesheet", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool IsScriptUrl(string value)
    {
        var compact = string.Concat(value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c))).ToLowerInvariant();
        return compact.StartsWith("javascript:", StringComparison.Ordinal)
               || compact.StartsWith("vbscript:", StringComparison.Ordinal)
               || compact.StartsWith("data:text/html", StringComparison.Ordinal)
               || compact.StartsWith("data:application/xhtml", StringComparison.Ordinal)
               || compact.StartsWith("data:image/svg", StringComparison.Ordinal);
    }

    /// <summary>For chapters that are not well-formed XML; the CSP header still blocks whatever this misses.</summary>
    private static string CleanAsText(string html, string readerCss)
    {
        var clean = DangerousBlock().Replace(html, string.Empty);
        clean = DangerousTag().Replace(clean, string.Empty);
        clean = EventAttribute().Replace(clean, string.Empty);
        clean = ScriptUrl().Replace(clean, "$1=\"#\"");
        var extra = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{ContentSecurityPolicy}\"><link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(readerCss)}\">";
        var headEnd = clean.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return headEnd >= 0 ? clean.Insert(headEnd, extra) : extra + clean;
    }

    [GeneratedRegex("&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex NamedEntity();

    [GeneratedRegex(@"<(script|iframe|object|applet|frameset)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousBlock();

    [GeneratedRegex(@"<\s*/?\s*(script|iframe|frame|frameset|object|embed|applet|base|portal|meta\s+http-equiv)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousTag();

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttribute();

    [GeneratedRegex(@"\b(href|src|action|formaction|data)\s*=\s*[""']?\s*(?:javascript|vbscript|data:text/html)[^""'\s>]*[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrl();
}
