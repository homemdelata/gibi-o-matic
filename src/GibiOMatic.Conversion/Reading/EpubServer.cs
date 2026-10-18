using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace GibiOMatic.Conversion.Reading;

/// <summary>
/// Serves one text EPUB to the reader's web view, straight from the archive, on 127.0.0.1 (never
/// reachable from the network). Every address carries a random token, so other programs on the
/// machine cannot guess it; only GET is answered; chapters are sanitized and every response
/// carries a Content-Security-Policy that blocks scripts and anything outside the book.
/// </summary>
public sealed class EpubServer : IDisposable
{
    /// <summary>Address of the reader's own style sheet (font size and colors).</summary>
    public const string ReaderCssPath = "__gibiomatic/reader.css";

    private readonly EpubBook _book;
    private readonly Func<string> _readerCss;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _token;

    public EpubServer(EpubBook book, Func<string> readerCss)
    {
        _book = book;
        _readerCss = readerCss;
        _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/{_token}/");
        _ = AcceptLoopAsync(_stop.Token);
    }

    /// <summary>"http://127.0.0.1:port/token/"; a book file is at its path below it.</summary>
    public Uri BaseUri { get; }

    public Uri ChapterUri(int chapter, string? fragment = null)
    {
        var path = string.Join('/', _book.Chapters[chapter].Path.Split('/').Select(Uri.EscapeDataString));
        return new Uri(BaseUri, path + (string.IsNullOrEmpty(fragment) ? string.Empty : "#" + Uri.EscapeDataString(fragment)));
    }

    /// <summary>The path of a book file inside the EPUB for an address of this server, or <c>null</c>.</summary>
    public string? BookPathOf(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !BaseUri.IsBaseOf(uri) || uri.Port != BaseUri.Port)
        {
            return null;
        }

        var relative = uri.AbsolutePath[BaseUri.AbsolutePath.Length..];
        return Uri.UnescapeDataString(relative);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10_000;
                var stream = client.GetStream();
                var request = await ReadRequestLineAsync(stream, ct);
                var response = request is null ? Response(400, "text/plain", "Bad request"u8.ToArray()) : Answer(request.Value);
                await stream.WriteAsync(response, ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The web view went away or the reader closed.
            }
        }
    }

    private (string Method, string Target)? ParseRequestLine(string line)
    {
        var parts = line.Split(' ');
        return parts.Length == 3 && parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) ? (parts[0], parts[1]) : null;
    }

    /// <summary>Reads the request head (up to 16 KB) and returns its first line.</summary>
    private async Task<(string Method, string Target)?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var end = head.IndexOf("\r\n", StringComparison.Ordinal);
        return end <= 0 ? null : ParseRequestLine(head[..end]);
    }

    internal byte[] Answer((string Method, string Target) request)
    {
        if (request.Method != "GET")
        {
            return Response(405, "text/plain", "Method not allowed"u8.ToArray());
        }

        var target = request.Target.Split('?', '#')[0];
        var prefix = "/" + _token + "/";
        if (!target.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Response(404, "text/plain", "Not found"u8.ToArray());
        }

        var path = Uri.UnescapeDataString(target[prefix.Length..]);
        if (path == ReaderCssPath)
        {
            return Response(200, "text/css; charset=utf-8", Encoding.UTF8.GetBytes(_readerCss()));
        }

        if (path.Split('/').Any(p => p is ".." or ".") || _book.Read(path) is not { } data)
        {
            return Response(404, "text/plain", "Not found"u8.ToArray());
        }

        var type = ContentTypeOf(path);
        if (type.StartsWith("application/xhtml", StringComparison.Ordinal) || type.StartsWith("text/html", StringComparison.Ordinal))
        {
            var depth = path.Count(c => c == '/');
            var css = string.Concat(Enumerable.Repeat("../", depth)) + ReaderCssPath;
            var (html, htmlType) = EpubHtml.Sanitize(data, css);
            return Response(200, htmlType, Encoding.UTF8.GetBytes(html));
        }

        return Response(200, type, data);
    }

    private static byte[] Response(int status, string contentType, byte[] body)
    {
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   $"Content-Security-Policy: {EpubHtml.ContentSecurityPolicy}\r\n" +
                   "X-Content-Type-Options: nosniff\r\n" +
                   "Referrer-Policy: no-referrer\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "Connection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(head);
        var response = new byte[bytes.Length + body.Length];
        bytes.CopyTo(response, 0);
        body.CopyTo(response, bytes.Length);
        return response;
    }

    internal static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xhtml" or ".xht" => "application/xhtml+xml; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".xml" or ".opf" or ".ncx" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
