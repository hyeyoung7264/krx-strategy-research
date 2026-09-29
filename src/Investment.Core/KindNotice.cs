using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Investment.Core;

/// <summary>
/// Exact response-body bytes observed at Source. ObservedAt is capture time, not publication time.
/// A valid capture does not certify the HTML's meaning, legal effect or point-in-time availability.
/// </summary>
public sealed record KindNoticeSnapshot(string Source, DateTimeOffset ObservedAt, string MediaType,
    string? Charset, string RawHash, string RawBase64)
{
    public void Validate(DateTimeOffset? now = null) => KindNoticeClient.Validate(this, now ?? DateTimeOffset.UtcNow);
}

/// <summary>
/// Bounded read-only capture of a public KIND external notice, not an Open API or an HTML terms parser.
/// Does not follow links, frames or redirects, decode the body, or infer publication timestamps.
/// </summary>
public sealed class KindNoticeClient : IDisposable
{
    public const int MaximumResponseBytes = 2_000_000;
    private const int MaximumBase64Length = ((MaximumResponseBytes + 2) / 3) * 4;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;

    public KindNoticeClient(HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        ValidateTransport(handler);
        http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KrxStrategyResearch/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<KindNoticeSnapshot> Fetch(string source, CancellationToken ct = default)
    {
        var uri = ValidateSource(source);
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri?.AbsoluteUri != source)
                throw new InvalidOperationException($"KIND notice HTTP status {(int)response.StatusCode} or redirect; no automatic retry.");
            var type = response.Content.Headers.ContentType;
            if (type?.MediaType is not { } mediaType ||
                !response.Content.Headers.TryGetValues("Content-Type", out var contentTypes) || contentTypes.Count() != 1 ||
                type.Parameters.Count(parameter => parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) > 1)
                throw new InvalidOperationException("KIND notice requires one HTML content type.");
            try { ValidateContentType(mediaType, type.CharSet); }
            catch (ArgumentException) { throw new InvalidOperationException("Unexpected KIND notice content type or charset declaration."); }
            // No decompression is applied: the saved hash covers the exact received HTML entity bytes.
            if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("KIND notice returned unsupported content encoding.");
            var expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength is > MaximumResponseBytes or < 0)
                throw new InvalidOperationException("KIND notice response too large or invalid length.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    throw new InvalidOperationException("KIND notice response too large.");
                buffer.Write(chunk, 0, count);
            }
            if (buffer.Length == 0 || expectedLength is { } length && buffer.Length != length)
                throw new InvalidOperationException("KIND notice body is empty or incomplete.");
            ct.ThrowIfCancellationRequested();
            var raw = buffer.ToArray();
            var snapshot = new KindNoticeSnapshot(source, clock(), mediaType, type.CharSet,
                Convert.ToHexString(SHA256.HashData(raw)), Convert.ToBase64String(raw));
            snapshot.Validate(clock());
            return snapshot;
        }
        catch (HttpRequestException) { throw new InvalidOperationException("KIND notice network failure; no automatic retry."); }
        catch (IOException) { throw new InvalidOperationException("KIND notice response stream failed; no automatic retry."); }
        catch (FormatException) { throw new InvalidOperationException("Malformed KIND notice response headers."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("KIND notice request timed out; no automatic retry."); }
    }

    public static Uri ValidateSource(string source)
    {
        if (string.IsNullOrEmpty(source) || source.Length > 160)
            throw new ArgumentException("Invalid KIND notice source.", nameof(source));
        // Match the original string before URI normalization can hide escaped paths, ports or dot segments.
        var match = Regex.Match(source,
            @"\Ahttps://kind\.krx\.co\.kr/external/(?<date>[0-9]{4}/[0-9]{2}/[0-9]{2})/[0-9]{6}/[0-9]{14}/[0-9]{5}\.htm\z",
            RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success || !DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy/MM/dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.AbsoluteUri != source)
            throw new ArgumentException("KIND notice source must be a fixed official HTTPS external numeric HTML path.", nameof(source));
        // The path date is only a path component; it is never used as publication or availability time.
        return uri;
    }

    public static void Validate(KindNoticeSnapshot snapshot, DateTimeOffset now)
    {
        _ = ValidateSource(snapshot.Source);
        ValidateContentType(snapshot.MediaType, snapshot.Charset);
        if (snapshot.ObservedAt == default || snapshot.ObservedAt > now ||
            string.IsNullOrEmpty(snapshot.RawBase64) || snapshot.RawBase64.Length > MaximumBase64Length ||
            snapshot.RawHash is null || !Regex.IsMatch(snapshot.RawHash, @"\A[0-9A-F]{64}\z", RegexOptions.CultureInvariant, RegexTimeout))
            throw new ArgumentException("Invalid KIND notice observation, body or hash.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(snapshot.RawBase64); }
        catch (FormatException) { throw new ArgumentException("Invalid KIND notice base64 body."); }
        if (bytes.Length is < 1 or > MaximumResponseBytes || Convert.ToBase64String(bytes) != snapshot.RawBase64 ||
            Convert.ToHexString(SHA256.HashData(bytes)) != snapshot.RawHash)
            throw new ArgumentException("KIND notice body/hash mismatch or exceeds capture bound.");
    }

    private static void ValidateContentType(string mediaType, string? charset)
    {
        if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("KIND notice content type must be HTML.");
        // Retain a declared encoding without interpreting or re-encoding the response bytes.
        if (charset is not null && (charset.Length > 66 ||
            !Regex.IsMatch(charset, "\\A(?:[A-Za-z0-9][A-Za-z0-9._-]{0,63}|\"[A-Za-z0-9][A-Za-z0-9._-]{0,63}\")\\z",
                RegexOptions.CultureInvariant, RegexTimeout)))
            throw new ArgumentException("Invalid KIND notice charset declaration.");
    }

    private static void ValidateTransport(HttpMessageHandler? handler)
    {
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            if (current is HttpClientHandler client && (client.AllowAutoRedirect || client.UseCookies ||
                client.AutomaticDecompression != DecompressionMethods.None || client.UseDefaultCredentials || client.Credentials is not null) ||
                current is SocketsHttpHandler sockets && (sockets.AllowAutoRedirect || sockets.UseCookies ||
                sockets.AutomaticDecompression != DecompressionMethods.None || sockets.Credentials is not null))
                throw new ArgumentException("KIND notice transport must disable redirects, cookies, decompression and credentials.", nameof(handler));
        }
    }

    public void Dispose() => http.Dispose();
}
