using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KindNoticeTests
{
    private const string Source = "https://kind.krx.co.kr/external/2024/11/15/000107/20241115000180/68155.htm";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 1, 0, 0, TimeSpan.FromHours(9));
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("<html><body>합성 공시</body></html>\r\n");

    [Fact] public async Task CapturePreservesExactBytesWithoutDecodingOrPublicationInference()
    {
        byte[] raw = [0xef, 0xbb, 0xbf, 0x3c, 0x68, 0x74, 0x6d, 0x6c, 0x3e, 0xff, 0x00, 0x0d, 0x0a];
        using var handler = new Handler(raw) { ContentType = "text/html; charset=\"EUC-KR\"" };
        using var client = new KindNoticeClient(handler, () => Now);
        var snapshot = await client.Fetch(Source);
        snapshot.Validate(Now);
        Assert.Equal(raw, Convert.FromBase64String(snapshot.RawBase64));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(raw)), snapshot.RawHash);
        Assert.Equal(Now, snapshot.ObservedAt);
        Assert.Equal(Source, snapshot.Source);
        Assert.Equal("text/html", snapshot.MediaType);
        Assert.Equal("\"EUC-KR\"", snapshot.Charset);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(Source, request.Source);
        Assert.Equal("KrxStrategyResearch/1.0", request.Agent);
        Assert.Equal("identity", request.Encoding);
        Assert.False(request.Authenticated);
        var replay = JsonSerializer.Deserialize<KindNoticeSnapshot>(JsonSerializer.Serialize(snapshot))!;
        replay.Validate(Now);
        Assert.Equal(snapshot, replay);
    }

    [Fact] public async Task CaptureCanPreserveHtmlErrorShellButDoesNotInterpretItAsAnAction()
    {
        var raw = Encoding.UTF8.GetBytes("<html><body>Service unavailable <iframe src='https://other.invalid/'></iframe></body></html>");
        using var handler = new Handler(raw) { ContentType = "text/html" };
        using var client = new KindNoticeClient(handler, () => Now);
        var snapshot = await client.Fetch(Source);
        snapshot.Validate(Now);
        Assert.Equal(raw, Convert.FromBase64String(snapshot.RawBase64));
        Assert.Null(snapshot.Charset);
        Assert.Single(handler.Requests);
    }

    [Fact] public async Task UnsafeOrNoncanonicalSourcePathsFailBeforeAnyRequest()
    {
        var invalid = new[]
        {
            Source.Replace("https:", "http:"), Source.Replace("kind.krx.co.kr", "kind.krx.co.kr.evil.invalid"),
            Source.Replace("kind.krx.co.kr", "user@kind.krx.co.kr"), Source.Replace("kind.krx.co.kr", "kind.krx.co.kr:443"),
            Source.Replace("kind.krx.co.kr", "kind.krx.co.kr:8443"), Source + "?x=1", Source + "#fragment",
            Source + "?", Source + "#", Source + "\r\n", " " + Source,
            Source.Replace("/external/", "/other/../external/"), Source.Replace("/external/", "/external/./"),
            Source.Replace("/external/", "/%65xternal/"), Source.Replace("/external/", "\\external/"),
            Source.Replace("2024/11/15", "2024/02/30"), Source.Replace("000107", "00010a"),
            Source.Replace("20241115000180", "2024111500018"), Source.Replace("68155.htm", "notice.htm"),
            Source.Replace(".htm", ".html"), Source.Replace(".htm", ".pdf"), "", "//kind.krx.co.kr/"
        };
        using var handler = new Handler(Body);
        using var client = new KindNoticeClient(handler, () => Now);
        foreach (var source in invalid) await Assert.ThrowsAsync<ArgumentException>(() => client.Fetch(source));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsAndRedirectsStopWithoutRetry(HttpStatusCode status)
    {
        using var handler = new Handler(Body) { Status = status };
        using var client = new KindNoticeClient(handler, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Source));
        Assert.Single(handler.Requests);
    }

    [Fact] public async Task ChangedOrMissingResponseSourceIsRejected()
    {
        foreach (var uri in new[] { "https://kind.krx.co.kr/error.html", "https://other.invalid/", null })
        {
            using var handler = new Handler(Body) { OverrideResponseSource = true, ResponseSource = uri };
            using var client = new KindNoticeClient(handler, () => Now);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Source));
            Assert.Single(handler.Requests);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("application/octet-stream")]
    [InlineData("text/html; charset=\"bad encoding\"")]
    [InlineData("text/html; charset=UTF-8; charset=EUC-KR")]
    public async Task UnexpectedContentTypeOrCharsetFails(string? contentType)
    {
        using var handler = new Handler(Body) { ContentType = contentType };
        using var client = new KindNoticeClient(handler, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Source));
        Assert.Single(handler.Requests);
    }

    [Fact] public async Task CompressedBodiesAreNotSilentlyDecompressed()
    {
        using var handler = new Handler(Body) { ContentEncoding = "gzip" };
        using var client = new KindNoticeClient(handler, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Source));
        Assert.Single(handler.Requests);
    }

    [Fact] public async Task HeaderAndStreamingSizeBoundsAreEnforced()
    {
        using var advertised = new Handler(Body) { DeclaredLength = KindNoticeClient.MaximumResponseBytes + 1L };
        using var firstClient = new KindNoticeClient(advertised, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstClient.Fetch(Source));
        using var stream = new UnseekableStream(new byte[KindNoticeClient.MaximumResponseBytes + 1]);
        using var streaming = new Handler(Body) { ContentFactory = () => new StreamContent(stream) };
        using var secondClient = new KindNoticeClient(streaming, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondClient.Fetch(Source));
        Assert.InRange(stream.BytesRead, KindNoticeClient.MaximumResponseBytes + 1L, KindNoticeClient.MaximumResponseBytes + 8192L);
        Assert.Single(advertised.Requests);
        Assert.Single(streaming.Requests);
    }

    [Fact] public async Task ExactMaximumSizeIsAcceptedAndRevalidated()
    {
        var raw = new byte[KindNoticeClient.MaximumResponseBytes];
        using var handler = new Handler(raw);
        using var client = new KindNoticeClient(handler, () => Now);
        var snapshot = await client.Fetch(Source);
        snapshot.Validate(Now);
        Assert.Equal(raw.Length, Convert.FromBase64String(snapshot.RawBase64).Length);
    }

    [Fact] public async Task EmptyOrTruncatedBodiesFail()
    {
        using var empty = new Handler([]);
        using var emptyClient = new KindNoticeClient(empty, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => emptyClient.Fetch(Source));
        using var truncated = new Handler(Body) { DeclaredLength = Body.Length + 1L };
        using var truncatedClient = new KindNoticeClient(truncated, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => truncatedClient.Fetch(Source));
    }

    [Fact] public async Task NetworkTimeoutAndStreamFailuresDoNotRetry()
    {
        foreach (var failure in new Exception[] { new HttpRequestException("synthetic network"), new OperationCanceledException("synthetic timeout") })
        {
            using var handler = new Handler(Body) { Failure = failure };
            using var client = new KindNoticeClient(handler, () => Now);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Source));
            Assert.Single(handler.Requests);
        }
        using var failedBody = new Handler(Body) { ContentFactory = () => new StreamContent(new FailedStream()) };
        using var failedClient = new KindNoticeClient(failedBody, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedClient.Fetch(Source));
        Assert.Single(failedBody.Requests);
    }

    [Fact] public async Task CallerCancellationStopsBeforeRequestAndDuringBodyRead()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var handler = new Handler(Body);
        using var client = new KindNoticeClient(handler, () => Now);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Fetch(Source, cancelled.Token));
        Assert.Empty(handler.Requests);

        using var bodyCancel = new CancellationTokenSource();
        using var blocking = new Handler(Body) { ContentFactory = () => new StreamContent(new BlockingStream(bodyCancel)) };
        using var bodyClient = new KindNoticeClient(blocking, () => Now);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bodyClient.Fetch(Source, bodyCancel.Token));
        Assert.Single(blocking.Requests);
    }

    [Fact] public void UnsafeBuiltInTransportOptionsAreRejectedIncludingDelegatingHandlers()
    {
        using var defaults = new HttpClientHandler();
        Assert.Throws<ArgumentException>(() => new KindNoticeClient(defaults));
        using var cookies = new HttpClientHandler { AllowAutoRedirect = false };
        Assert.Throws<ArgumentException>(() => new KindNoticeClient(cookies));
        using var decompression = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip };
        Assert.Throws<ArgumentException>(() => new KindNoticeClient(decompression));
        using var credentials = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = new NetworkCredential("synthetic", "synthetic") };
        Assert.Throws<ArgumentException>(() => new KindNoticeClient(credentials));
        using var sockets = new SocketsHttpHandler { AllowAutoRedirect = false };
        Assert.Throws<ArgumentException>(() => new KindNoticeClient(sockets));
        using var wrapper = new Wrapper(new HttpClientHandler());
        Assert.Throws<ArgumentException>(() => new KindNoticeClient(wrapper));
        using var safe = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var safeClient = new KindNoticeClient(safe);
    }

    [Fact] public async Task ReplayRejectsTamperedBytesHashSourceObservationAndMetadata()
    {
        using var handler = new Handler(Body);
        using var client = new KindNoticeClient(handler, () => Now);
        var snapshot = await client.Fetch(Source);
        var malformed = new[]
        {
            snapshot with { Source = "https://other.invalid/" },
            snapshot with { ObservedAt = Now.AddTicks(1) }, snapshot with { ObservedAt = default },
            snapshot with { MediaType = "application/json" }, snapshot with { Charset = "utf 8" },
            snapshot with { RawHash = new string('0', 64) }, snapshot with { RawHash = "abc" },
            snapshot with { RawBase64 = "invalid base64!" }, snapshot with { RawBase64 = "" },
            snapshot with { RawBase64 = snapshot.RawBase64 + "\n" },
            snapshot with { RawBase64 = Convert.ToBase64String([1, 2, 3]) },
            snapshot with { RawBase64 = Convert.ToBase64String(new byte[KindNoticeClient.MaximumResponseBytes + 1]) }
        };
        Assert.All(malformed, value => Assert.Throws<ArgumentException>(() => value.Validate(Now)));
    }

    private sealed record Request(string Method, string Source, string Agent, string Encoding, bool Authenticated);
    private sealed class Handler(byte[] raw) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? ContentType { get; init; } = "text/html; charset=UTF-8";
        public string? ContentEncoding { get; init; }
        public long? DeclaredLength { get; init; }
        public Func<HttpContent>? ContentFactory { get; init; }
        public bool OverrideResponseSource { get; init; }
        public string? ResponseSource { get; init; }
        public Exception? Failure { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.UserAgent.ToString(),
                request.Headers.AcceptEncoding.ToString(), request.Headers.Authorization is not null ||
                request.Headers.Contains("AUTH_KEY") || request.Headers.Contains("Cookie")));
            if (Failure is not null) return Task.FromException<HttpResponseMessage>(Failure);
            var content = ContentFactory?.Invoke() ?? new ByteArrayContent(raw);
            if (ContentType is not null) content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
            if (ContentEncoding is not null) content.Headers.ContentEncoding.Add(ContentEncoding);
            if (DeclaredLength is not null) content.Headers.ContentLength = DeclaredLength;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                RequestMessage = OverrideResponseSource ? ResponseSource is null ? null : new(HttpMethod.Get, ResponseSource) : request,
                Content = content
            });
        }
    }
    private sealed class Wrapper(HttpMessageHandler inner) : DelegatingHandler(inner);

    private class UnseekableStream(byte[] raw) : Stream
    {
        private readonly MemoryStream inner = new(raw);
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = inner.Read(buffer, offset, count); BytesRead += read; return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); var read = inner.Read(buffer.Span); BytesRead += read; return ValueTask.FromResult(read); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class FailedStream : UnseekableStream
    {
        public FailedStream() : base([]) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromException<int>(new IOException("synthetic stream failure"));
    }
    private sealed class BlockingStream(CancellationTokenSource cancel) : UnseekableStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); return 0; }
    }
}
