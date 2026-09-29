using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KindPublicationTests
{
    private const string Ticker = "123450";
    private const string Acceptance = "20250507000011";
    // The document number deliberately has a different date and suffix from the acceptance number.
    private const string Document = "20250506000999";
    private const string External = "https://kind.krx.co.kr/external/2025/05/07/000011/20250506000999/12345.htm";
    private const string ListSource = "https://kind.krx.co.kr/disclosure/details.do";
    private const string ViewerSource = "https://kind.krx.co.kr/common/disclsviewer.do?method=search&acptno=" + Acceptance;
    private const string ContentsSource = "https://kind.krx.co.kr/common/disclsviewer.do?method=searchContents&docNo=" + Document;
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly KindPublicationQuery Query = new(Ticker, new(2025, 5, 7), new(2025, 5, 7), Acceptance);

    [Fact]
    public async Task CollectsThreeBoundedStagesWithDifferentTitlesAndIdentifiersWithoutFetchingExternalNotice()
    {
        using var handler = new Handler();
        using var client = new KindPublicationClient(handler, () => Now);
        var preserved = new List<KindPublicationCapture>();
        var receipt = await client.Fetch(Query, capture => { preserved.Add(capture); return Task.CompletedTask; });
        receipt.Validate(Now);
        Assert.Equal("COLLECTED_UNREVIEWED", receipt.Status);
        Assert.Null(receipt.FailedStage);
        Assert.Null(receipt.ErrorKind);
        Assert.Equal(new[] { "LIST", "VIEWER", "CONTENTS" }, receipt.Captures.Select(c => c.Stage));
        Assert.Equal(JsonSerializer.Serialize(receipt.Captures), JsonSerializer.Serialize(preserved));
        var publication = Assert.IsType<KindPublicationRecord>(receipt.Publication);
        Assert.Equal(Acceptance, publication.AcceptanceNo);
        Assert.Equal(Document, publication.DocumentNo);
        Assert.NotEqual(publication.AcceptanceNo, publication.DocumentNo);
        Assert.Equal("9Z9Z9", publication.IssuerId); // Opaque issuer id cannot be converted into the ticker.
        Assert.Equal(Ticker, publication.ViewerTicker);
        Assert.Equal("합성회사", publication.CompanyName);
        Assert.Equal("합성회사", publication.ViewerCompanyName);
        Assert.Equal("KOSPI", publication.Market);
        Assert.Equal("변경상장(합성 조건)", publication.Title);
        Assert.Equal("MINUTE", publication.Precision);
        Assert.Null(publication.TimeZone);
        Assert.Equal(new DateOnly(2025, 5, 7), publication.DisplayedDate);
        Assert.Equal("09:17", publication.DisplayedMinute);
        Assert.Equal(External, publication.ExternalSource);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { ListSource, ViewerSource, ContentsSource }, handler.Requests.Select(r => r.Source));
        Assert.Equal(new[] { "POST", "GET", "GET" }, handler.Requests.Select(r => r.Method));
        Assert.Equal(KindPublicationClient.ListRequestBody(Query), handler.Requests[0].Body);
        var fields = handler.Requests[0].Body!.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
        Assert.Equal("searchDetailsSub", fields["method"]);
        Assert.Equal("details_sub", fields["forward"]);
        Assert.Equal("100", fields["currentPageSize"]);
        Assert.Equal("1", fields["pageIndex"]);
        Assert.Equal("A" + Ticker, fields["repIsuSrtCd"]);
        Assert.Equal(Ticker, fields["searchCorpName"]);
        Assert.Equal("2025-05-07", fields["fromDate"]);
        Assert.Equal("2025-05-07", fields["toDate"]);
        Assert.DoesNotContain("lastReport", fields.Keys);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("identity", request.Encoding);
            Assert.False(request.Authenticated);
            Assert.DoesNotContain("/external/", request.Source);
        });
        for (var i = 1; i < handler.Requests.Count; i++)
            Assert.True(handler.Requests[i].Elapsed - handler.Requests[i - 1].Elapsed >= TimeSpan.FromMilliseconds(950));
        var replay = JsonSerializer.Deserialize<KindPublicationReceipt>(JsonSerializer.Serialize(receipt))!;
        replay.Validate(Now);
        Assert.Equal(receipt.Publication, replay.Publication);
        Assert.Equal(JsonSerializer.Serialize(receipt.Captures), JsonSerializer.Serialize(replay.Captures));
    }

    [Fact]
    public void RawReplayAcceptsConsistentDuplicateHiddenInputsAndAdjacentSelectedAttribute()
    {
        var receipt = Receipt();
        receipt.Validate(Now);
        var viewer = Encoding.UTF8.GetString(Convert.FromBase64String(receipt.Captures[1].RawBase64));
        Assert.Contains("value='" + Document + "|Y'selected=\"selected\"", viewer);
        Assert.Equal(2, viewer.Split("name=\"acptNo\"").Length - 1);
        Assert.DoesNotContain("AvailableAt", JsonSerializer.Serialize(receipt));
        Assert.DoesNotContain("PublishedAt", JsonSerializer.Serialize(receipt));
        Assert.Null(receipt.Publication!.TimeZone);
    }

    [Fact]
    public void SameMinuteRowsDoNotInventAnIntraminuteOrderOrCurrentStatus()
    {
        var second = Row("20250507000010", 1, "09:17", "다른 제목");
        var first = Row(Acceptance, 2, "09:17", "변경상장(합성 조건)");
        var receipt = Receipt(list: List(first + second, 2));
        receipt.Validate(Now);
        Assert.Equal("09:17", receipt.Publication!.DisplayedMinute);
        Assert.Null(receipt.Publication.TimeZone);
        Assert.DoesNotContain("관리종목", JsonSerializer.Serialize(receipt.Publication));
    }

    [Fact]
    public void ExactlyOneHundredRowsAndAnAlphanumericSecurityCodeAreAccepted()
    {
        var rows = Row(Acceptance, 100) + string.Concat(Enumerable.Range(1, 99)
            .Select(i => Row("20250507" + (i + 100).ToString("000000"), 100 - i)));
        var query = Query with { Ticker = "0001A0" };
        var receipt = Receipt(list: List(rows, 100), viewer: Viewer().Replace("(123450)", "(0001A0)"));
        receipt = receipt with
        {
            Query = query,
            Captures = [receipt.Captures[0] with { RequestBody = KindPublicationClient.ListRequestBody(query) },
                receipt.Captures[1], receipt.Captures[2]],
            Publication = receipt.Publication! with { ViewerTicker = query.Ticker }
        };
        receipt.Validate(Now);
    }

    [Fact]
    public async Task InvalidQueryIsRejectedBeforeAnyHttpRequest()
    {
        using var handler = new Handler();
        using var client = new KindPublicationClient(handler, () => Now);
        var invalid = new[]
        {
            Query with { Ticker = "12345" }, Query with { Ticker = "12345&" },
            Query with { AcceptanceNo = "2025050700001" }, Query with { AcceptanceNo = "20250507000011&x=1" },
            Query with { From = default }, Query with { Through = default },
            Query with { From = Query.Through.AddDays(1) }, Query with { Through = new DateOnly(2026, 10, 1) }
        };
        foreach (var query in invalid)
            await Assert.ThrowsAsync<ArgumentException>(() => client.Fetch(query));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("multipage")]
    [InlineData("zero")]
    [InlineData("page-size")]
    [InlineData("missing-target")]
    [InlineData("duplicate-target")]
    [InlineData("out-of-range")]
    [InlineData("seconds")]
    [InlineData("wrong-table")]
    [InlineData("extra-cell")]
    [InlineData("duplicate-market")]
    [InlineData("missing-issuer")]
    [InlineData("template-only")]
    [InlineData("script-only")]
    public void ListMustBeCompleteUnambiguousAndWithinRequestedScope(string mutation)
    {
        var raw = List();
        raw = mutation switch
        {
            "count" => raw.Replace("<em>1</em>", "<em>2</em>"),
            "multipage" => raw.Replace("<em>1</em>", "<em>101</em>").Replace("</strong>/1", "</strong>/2"),
            "zero" => List("", 0),
            "page-size" => raw.Replace("value='100'", "value='50'"),
            "missing-target" => raw.Replace(Acceptance, "20250507000012"),
            "duplicate-target" => List(Row(Acceptance, 2) + Row(Acceptance, 1), 2),
            "out-of-range" => raw.Replace("2025-05-07 09:17", "2025-05-06 09:17"),
            "seconds" => raw.Replace("2025-05-07 09:17", "2025-05-07 09:17:00"),
            "wrong-table" => raw.Replace("번호, 시간, 회사명, 공시제목, 제출인, 차트/주가", "회사명"),
            "extra-cell" => raw.Replace("</tr>", "<td>unexpected</td></tr>"),
            "duplicate-market" => raw.Replace("alt='유가증권'", "alt='유가증권'><img alt='코스닥'"),
            "template-only" => "<template>" + raw + "</template>",
            "script-only" => "<script type='application/json'>" + raw + "</script>",
            _ => raw.Replace("companysummary_open('9Z9Z9')", "companysummary_open('')")
        };
        Reject(Receipt(list: raw));
    }

    [Theory]
    [InlineData("wrong-ticker")]
    [InlineData("wrong-company")]
    [InlineData("wrong-acceptance")]
    [InlineData("conflicting-duplicate")]
    [InlineData("duplicate-attribute")]
    [InlineData("missing-selection")]
    [InlineData("two-selections")]
    [InlineData("selected-placeholder")]
    [InlineData("duplicate-mainDoc")]
    [InlineData("wrong-doc")]
    public void ViewerMustBindRequestedSecurityAcceptanceAndOneSelectedDocument(string mutation)
    {
        var raw = Viewer();
        raw = mutation switch
        {
            "wrong-ticker" => raw.Replace("(123450)", "(123455)"),
            "wrong-company" => raw.Replace("합성회사 (", "다른회사 ("),
            "wrong-acceptance" => raw.Replace(Acceptance, "20250507000012"),
            "conflicting-duplicate" => raw[..raw.IndexOf(Acceptance, StringComparison.Ordinal)] + "20250507000012" +
                raw[(raw.IndexOf(Acceptance, StringComparison.Ordinal) + Acceptance.Length)..],
            "duplicate-attribute" => raw.Replace("selected=\"selected\"", "selected=\"selected\" selected=\"selected\""),
            "missing-selection" => raw.Replace("selected=\"selected\"", ""),
            "two-selections" => raw.Replace("<option value=''>", "<option value='' selected='selected'>"),
            "selected-placeholder" => raw.Replace("value='" + Document + "|Y'", "value=''"),
            "duplicate-mainDoc" => raw.Replace("</form>", "<select name='mainDoc'><option value='x'>x</option></select></form>"),
            _ => raw.Replace(Document, "20250506000888")
        };
        Reject(Receipt(viewer: raw));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("foreign-host")]
    [InlineData("user-info")]
    [InlineData("explicit-port")]
    [InlineData("query")]
    [InlineData("fragment")]
    [InlineData("dot-path")]
    [InlineData("wrong-document")]
    [InlineData("comment-only")]
    [InlineData("string-only")]
    [InlineData("multiple-calls")]
    [InlineData("concatenated-url")]
    [InlineData("inert-script")]
    [InlineData("template-script")]
    public void ContentsMustContainOneLiteralCanonicalDocumentLink(string mutation)
    {
        var raw = Contents();
        raw = mutation switch
        {
            "http" => raw.Replace("https://kind", "http://kind"),
            "foreign-host" => raw.Replace("kind.krx.co.kr/external", "kind.krx.co.kr.evil.invalid/external"),
            "user-info" => raw.Replace("https://kind", "https://user@kind"),
            "explicit-port" => raw.Replace(".co.kr/external", ".co.kr:443/external"),
            "query" => raw.Replace("12345.htm", "12345.htm?x=1"),
            "fragment" => raw.Replace("12345.htm", "12345.htm#part"),
            "dot-path" => raw.Replace("/external/", "/other/../external/"),
            "wrong-document" => raw.Replace(Document, "20250506000888"),
            "comment-only" => "<html><script>/*" + Call() + "*/</script></html>",
            "string-only" => "<html><script>const example = \"" + Call() + "\";</script></html>",
            "multiple-calls" => raw.Replace(Call(), Call() + Call()),
            "inert-script" => raw.Replace("<script>//", "<script type='application/json'>//"),
            "template-script" => "<template>" + raw + "</template>",
            _ => raw.Replace(External, "https://kind.krx.co.kr/' + 'external/2025/05/07/000011/" + Document + "/12345.htm")
        };
        Reject(Receipt(contents: raw));
    }

    [Fact]
    public void ReplayRejectsEveryChangedNormalizedFieldAndInventedPublicationTime()
    {
        var receipt = Receipt();
        var original = receipt.Publication!;
        var mutations = new[]
        {
            original with { AcceptanceNo = "20250507000012" }, original with { DocumentNo = "20250506000888" },
            original with { IssuerId = "12345" }, original with { CompanyName = "다른회사" },
            original with { Market = "KOSDAQ" }, original with { Title = "다른제목" }, original with { Submitter = "다른제출인" },
            original with { DisplayedDate = Query.From.AddDays(1) }, original with { DisplayedMinute = "09:18" },
            original with { ViewerTicker = "123455" }, original with { ViewerCompanyName = "다른회사" },
            original with { ExternalSource = External.Replace("12345.htm", "54321.htm") },
            original with { Precision = "SECOND" }, original with { TimeZone = "Asia/Seoul" }
        };
        foreach (var value in mutations) Reject(receipt with { Publication = value });
        Reject(receipt with { Publication = null });
    }

    [Fact]
    public void ReplayRejectsRequestProvenanceRawHashAndObservationTampering()
    {
        var receipt = Receipt();
        var first = receipt.Captures[0];
        var mutations = new[]
        {
            first with { Stage = "VIEWER" }, first with { Method = "GET" },
            first with { Source = "https://other.invalid/" }, first with { RequestBody = first.RequestBody + "&lastReport=T" },
            first with { ObservedAt = default }, first with { ObservedAt = Now.AddTicks(1) },
            first with { RawHash = new string('0', 64) }, first with { RawBase64 = first.RawBase64 + "\n" },
            first with { RawBase64 = "not base64" }, first with { HttpStatusCode = 302 },
            first with { ContentType = "application/json" }, first with { ContentEncodings = ["gzip"] }
        };
        foreach (var value in mutations) Reject(receipt with { Captures = [value, receipt.Captures[1], receipt.Captures[2]] });
        Reject(receipt with { Captures = [receipt.Captures[1], first, receipt.Captures[2]] });
        Reject(receipt with { Captures = [first, receipt.Captures[1]] });
        Reject(receipt with { Captures = [.. receipt.Captures, receipt.Captures[2]] });
        Reject(receipt with { CreatedAt = first.ObservedAt.AddTicks(-1) });
        Reject(receipt with { Captures = [first with { ObservedAt = Now }, receipt.Captures[1], receipt.Captures[2]] });
    }

    [Fact]
    public void RawCapturePreservesExactBytesAndEnforcesByteLimitIndependentlyOfHtmlInterpretation()
    {
        var capture = Receipt().Captures[0];
        byte[] exact = [0xef, 0xbb, 0xbf, 0x3c, 0x68, 0x74, 0x6d, 0x6c, 0x3e, 0xff, 0x00, 0x0d, 0x0a];
        var rawCapture = capture with
        {
            RawHash = Convert.ToHexString(SHA256.HashData(exact)), RawBase64 = Convert.ToBase64String(exact)
        };
        KindPublicationClient.ValidateCapture(rawCapture, Now);
        Assert.Equal(exact, Convert.FromBase64String(rawCapture.RawBase64));
        var maximum = new byte[KindPublicationClient.MaximumResponseBytes];
        KindPublicationClient.ValidateCapture(capture with
        {
            RawHash = Convert.ToHexString(SHA256.HashData(maximum)), RawBase64 = Convert.ToBase64String(maximum)
        }, Now);
        var oversized = new byte[KindPublicationClient.MaximumResponseBytes + 1];
        Assert.Throws<ArgumentException>(() => KindPublicationClient.ValidateCapture(capture with
        {
            RawHash = Convert.ToHexString(SHA256.HashData(oversized)), RawBase64 = Convert.ToBase64String(oversized)
        }, Now));
    }

    [Theory]
    [InlineData(0, "LIST")]
    [InlineData(1, "VIEWER")]
    [InlineData(2, "CONTENTS")]
    public async Task CompleteRejectedResponseIsPreservedBeforeStoppingAtItsStage(int failingIndex, string stage)
    {
        using var handler = new Handler { Reply = (index, _) => index == failingIndex ? Html("<html>synthetic unavailable</html>", HttpStatusCode.ServiceUnavailable) : null };
        using var client = new KindPublicationClient(handler, () => Now);
        var preserved = new List<KindPublicationCapture>();
        var receipt = await client.Fetch(Query, capture => { preserved.Add(capture); return Task.CompletedTask; });
        Assert.Equal("RESPONSE_REJECTED", receipt.Status);
        Assert.Equal(stage, receipt.FailedStage);
        Assert.Null(receipt.Publication);
        Assert.Equal(failingIndex + 1, receipt.Captures.Length);
        Assert.Equal(JsonSerializer.Serialize(receipt.Captures), JsonSerializer.Serialize(preserved));
        Assert.Equal(failingIndex + 1, handler.Requests.Count);
        Assert.Equal(503, receipt.Captures[^1].HttpStatusCode);
        receipt.Validate(Now);
    }

    [Fact]
    public async Task HtmlParseFailureStillPreservesCompleteBodyBeforeAnyFollowingRequest()
    {
        const string raw = "<html><body>synthetic maintenance <iframe src='https://other.invalid/'></iframe></body></html>\r\n";
        using var handler = new Handler { Reply = (_, _) => Html(raw) };
        using var client = new KindPublicationClient(handler, () => Now);
        var preserved = new List<KindPublicationCapture>();
        var receipt = await client.Fetch(Query, capture => { preserved.Add(capture); return Task.CompletedTask; });
        Assert.Equal("RESPONSE_REJECTED", receipt.Status);
        Assert.Equal("LIST", receipt.FailedStage);
        Assert.Null(receipt.Publication);
        var capture = Assert.Single(preserved);
        Assert.Equal(Encoding.UTF8.GetBytes(raw), Convert.FromBase64String(capture.RawBase64));
        Assert.Single(handler.Requests);
        receipt.Validate(Now);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestIntervalAlsoAppliesAcrossCollectionsOnOneClient(bool concurrent)
    {
        using var handler = new Handler { Reply = (_, _) => Html("<html>synthetic unsupported screen</html>") };
        using var client = new KindPublicationClient(handler, () => Now);
        var receipts = concurrent ? await Task.WhenAll(client.Fetch(Query), client.Fetch(Query))
            : new[] { await client.Fetch(Query), await client.Fetch(Query) };
        Assert.All(receipts, receipt => Assert.Equal("RESPONSE_REJECTED", receipt.Status));
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(handler.Requests[1].Elapsed - handler.Requests[0].Elapsed >= TimeSpan.FromMilliseconds(950));
    }

    [Fact]
    public async Task PreservationCallbackCannotMutateTheClientsInternalCapture()
    {
        using var handler = new Handler
        {
            Reply = (index, _) =>
            {
                var response = Html(index switch { 0 => List(), 1 => Viewer(), _ => Contents() });
                response.Content.Headers.ContentEncoding.Add("identity");
                return response;
            }
        };
        using var client = new KindPublicationClient(handler, () => Now);
        var receipt = await client.Fetch(Query, capture =>
        {
            capture.ContentEncodings[0] = "gzip";
            return Task.CompletedTask;
        });
        Assert.Equal("COLLECTED_UNREVIEWED", receipt.Status);
        Assert.All(receipt.Captures, capture => Assert.Equal("identity", Assert.Single(capture.ContentEncodings)));
        receipt.Validate(Now);
    }

    [Theory]
    [InlineData(0, "LIST")]
    [InlineData(1, "VIEWER")]
    public async Task PreservationCallbackFailureReturnsEvidenceAndStopsThePipeline(int failingIndex, string stage)
    {
        using var handler = new Handler();
        using var client = new KindPublicationClient(handler, () => Now);
        var calls = 0;
        var receipt = await client.Fetch(Query, _ => calls++ == failingIndex
            ? Task.FromException(new IOException("synthetic private path must not enter receipt")) : Task.CompletedTask);
        Assert.Equal("EVIDENCE_PRESERVATION_FAILED", receipt.Status);
        Assert.Equal(stage, receipt.FailedStage);
        Assert.Equal(nameof(IOException), receipt.ErrorKind);
        Assert.Null(receipt.Publication);
        Assert.Equal(failingIndex + 1, receipt.Captures.Length);
        Assert.Equal(failingIndex + 1, handler.Requests.Count);
        Assert.DoesNotContain("synthetic private path", JsonSerializer.Serialize(receipt));
        receipt.Validate(Now);
    }

    [Fact]
    public async Task CancellationBeforeFirstRequestAndAfterFirstCaptureStopsWithoutInventingSuccess()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var firstHandler = new Handler();
        using var firstClient = new KindPublicationClient(firstHandler, () => Now);
        var first = await firstClient.Fetch(Query, ct: cancelled.Token);
        Assert.Equal("CANCELLED", first.Status);
        Assert.Empty(first.Captures);
        Assert.Empty(firstHandler.Requests);
        Assert.Null(first.Publication);
        first.Validate(Now);

        using var cancel = new CancellationTokenSource();
        using var handler = new Handler();
        using var client = new KindPublicationClient(handler, () => Now);
        var receipt = await client.Fetch(Query, _ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token);
        Assert.Equal("CANCELLED", receipt.Status);
        Assert.Single(receipt.Captures);
        Assert.Single(handler.Requests);
        Assert.Null(receipt.Publication);
        receipt.Validate(Now);
    }

    [Fact]
    public async Task CancellationDuringBodyReadRetainsOnlyEarlierCompleteCaptures()
    {
        using var cancel = new CancellationTokenSource();
        using var handler = new Handler { Reply = (index, _) => index == 1 ? new(HttpStatusCode.OK)
        { Content = WithType(new StreamContent(new BlockingStream(cancel))) } : null };
        using var client = new KindPublicationClient(handler, () => Now);
        var receipt = await client.Fetch(Query, ct: cancel.Token);
        Assert.Equal("CANCELLED", receipt.Status);
        Assert.Equal("VIEWER", receipt.FailedStage);
        Assert.Single(receipt.Captures);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(receipt.Publication);
        receipt.Validate(Now);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("stream")]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("io")]
    [InlineData("content-type-limit")]
    [InlineData("encoding-count-limit")]
    [InlineData("encoding-length-limit")]
    public async Task IncompleteOrOversizedBodiesNeverBecomeEvidenceOrTriggerRetry(string failure)
    {
        using var handler = new Handler
        {
            Reply = (_, _) => failure switch
            {
                "header" => WithLength(Html("short"), KindPublicationClient.MaximumResponseBytes + 1L),
                "stream" => new(HttpStatusCode.OK) { Content = WithType(new StreamContent(new UnseekableStream(new byte[KindPublicationClient.MaximumResponseBytes + 1]))) },
                "truncated" => WithLength(Html("short"), 100),
                "empty" => Html(""),
                "network" => throw new HttpRequestException("private network detail"),
                "timeout" => throw new OperationCanceledException("private timeout detail"),
                "content-type-limit" => ExcessMetadata("content-type"),
                "encoding-count-limit" => ExcessMetadata("encoding-count"),
                "encoding-length-limit" => ExcessMetadata("encoding-length"),
                _ => new(HttpStatusCode.OK) { Content = WithType(new StreamContent(new FailedStream())) }
            }
        };
        using var client = new KindPublicationClient(handler, () => Now);
        var receipt = await client.Fetch(Query);
        Assert.Equal("REQUEST_FAILED", receipt.Status);
        Assert.Equal("LIST", receipt.FailedStage);
        Assert.Empty(receipt.Captures);
        Assert.Null(receipt.Publication);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("private ", JsonSerializer.Serialize(receipt));
        receipt.Validate(Now);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("response-source")]
    [InlineData("missing-source")]
    [InlineData("mime")]
    [InlineData("charset")]
    [InlineData("encoding")]
    [InlineData("invalid-utf8")]
    public async Task UntrustedTransportOrRepresentationCannotCreateALink(string mutation)
    {
        using var handler = new Handler
        {
            Reply = (_, request) =>
            {
                var response = Html(List());
                if (mutation == "redirect") { response.StatusCode = HttpStatusCode.Found; response.Headers.Location = new("https://other.invalid/"); }
                if (mutation == "response-source") response.RequestMessage = new(HttpMethod.Get, "https://other.invalid/");
                if (mutation == "missing-source") response.RequestMessage = new(HttpMethod.Get, (Uri?)null);
                if (mutation == "mime") { response.Content.Headers.Remove("Content-Type"); response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json"); }
                if (mutation == "charset") { response.Content.Headers.Remove("Content-Type"); response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/html; charset=UTF-8; charset=EUC-KR"); }
                if (mutation == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
                if (mutation == "invalid-utf8") response.Content = WithType(new ByteArrayContent([0xff, 0xfe, 0x00]));
                return response;
            }
        };
        using var client = new KindPublicationClient(handler, () => Now);
        var receipt = await client.Fetch(Query);
        Assert.NotEqual("COLLECTED_UNREVIEWED", receipt.Status);
        Assert.Equal("LIST", receipt.FailedStage);
        Assert.Null(receipt.Publication);
        Assert.Single(handler.Requests);
        receipt.Validate(Now);
    }

    [Fact]
    public void UnsafeBuiltInTransportConfigurationIsRejectedThroughDelegatingHandler()
    {
        using var defaults = new HttpClientHandler();
        Assert.Throws<ArgumentException>(() => new KindPublicationClient(defaults));
        using var cookies = new HttpClientHandler { AllowAutoRedirect = false };
        Assert.Throws<ArgumentException>(() => new KindPublicationClient(cookies));
        using var credentials = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = new NetworkCredential("synthetic", "synthetic") };
        Assert.Throws<ArgumentException>(() => new KindPublicationClient(credentials));
        using var decompress = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip };
        Assert.Throws<ArgumentException>(() => new KindPublicationClient(decompress));
        using var wrapped = new Wrapper(new HttpClientHandler());
        Assert.Throws<ArgumentException>(() => new KindPublicationClient(wrapped));
        using var safe = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var safeClient = new KindPublicationClient(safe);
    }

    private static KindPublicationReceipt Receipt(string? list = null, string? viewer = null, string? contents = null) => new(
        Guid.NewGuid().ToString("N"), Query, Now, "COLLECTED_UNREVIEWED", null, null,
        [Capture("LIST", "POST", ListSource, KindPublicationClient.ListRequestBody(Query), list ?? List(), -3),
         Capture("VIEWER", "GET", ViewerSource, null, viewer ?? Viewer(), -2),
         Capture("CONTENTS", "GET", ContentsSource, null, contents ?? Contents(), -1)],
        new(Acceptance, Document, "9Z9Z9", "합성회사", "KOSPI", "변경상장(합성 조건)", "한국거래소",
            Query.From, "09:17", Ticker, "합성회사", External));

    private static KindPublicationCapture Capture(string stage, string method, string source, string? body, string html, int seconds)
    {
        var raw = Encoding.UTF8.GetBytes(html);
        return new(stage, method, source, body, Now.AddSeconds(seconds), 200, "text/html; charset=UTF-8", [],
            Convert.ToHexString(SHA256.HashData(raw)), Convert.ToBase64String(raw));
    }

    private static string List(string? rows = null, int total = 1) =>
        "<table class='list type-00 mt10' summary='번호, 시간, 회사명, 공시제목, 제출인, 차트/주가'><tbody>" +
        (rows ?? Row()) + "</tbody></table><section class='paging-group'><div class='info type-00'>전체 <em>" + total +
        "</em>건 : <strong>1</strong>/1&nbsp;&nbsp;<select id='currentPageSize' name='currentPageSize'>" +
        "<option value='15'>15건</option><option value='100' selected='selected'>100건</option></select></div></section>";

    private static string Row(string acceptance = Acceptance, int number = 1, string minute = "09:17", string title = "변경상장(합성 조건)") =>
        "<tr><td>" + number + "</td><td>2025-05-07 " + minute + "</td><td><img alt='유가증권'>" +
        "<a title='합성회사' onclick=\"companysummary_open('9Z9Z9'); return false;\">합성회사</a><img alt='관리종목'></td>" +
        "<td><a href='#viewer' title='" + title + "' onclick=\"openDisclsViewer('" + acceptance + "','')\">" + title +
        "</a></td><td>한국거래소</td><td><a href='#'>차트</a></td></tr>";

    private static string Viewer() =>
        "<html><head><title>[합성회사] 변경상장</title></head><body>" +
        "<form name='docdownloadform' id='docdownloadform'><input type='hidden' name=\"acptNo\" id=\"acptNo\" value=\"" + Acceptance + "\">" +
        "<input type='hidden' name='docNo' value=''></form>" +
        "<form name='docpathfrm' id='docpathfrm' action='/common/disclsviewer.do'><input type='hidden' name='method' value='searchContents'>" +
        "<input type='hidden' name='docNo' value=''></form>" +
        "<form name='frm' id='frm'><input type='hidden' name=\"acptNo\" id=\"acptNo\" value=\"" + Acceptance + "\">" +
        "<h1 class='ttl type-99 fleft'>합성회사 (123450)</h1><select name='mainDoc' id='mainDoc'>" +
        "<option value=''>본문선택</option><option value='" + Document + "|Y'selected=\"selected\">변경상장 (2025.05.07)</option>" +
        "</select><iframe src='https://other.invalid/never-fetch'></iframe></form></body></html>";

    private static string Contents() => "<html><head><meta http-equiv='Content-Type' content='text/html; charset=euc-kr'>" +
        "<script src='../js/jquery/jquery.js'></script><script>//<![CDATA[\r\n" + Call() +
        "\r\nparent.$(\"#dialog-loading\").dialog(\"close\");\r\n//]]></script></head><body></body></html>\r\n";

    private static string Call() => "parent.setPath('','" + External + "','/external/2025/05/07/000011/" + Document + "/12345','03','11');";

    private static void Reject(KindPublicationReceipt receipt) => Assert.Throws<ArgumentException>(() => receipt.Validate(Now));
    private static HttpResponseMessage Html(string raw, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = WithType(new ByteArrayContent(Encoding.UTF8.GetBytes(raw))) };
    private static HttpContent WithType(HttpContent content)
    { content.Headers.TryAddWithoutValidation("Content-Type", "text/html; charset=UTF-8"); return content; }
    private static HttpResponseMessage WithLength(HttpResponseMessage response, long length)
    { response.Content.Headers.ContentLength = length; return response; }
    private static HttpResponseMessage ExcessMetadata(string field)
    {
        var response = Html(List());
        if (field == "content-type")
        {
            response.Content.Headers.Remove("Content-Type");
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/html; synthetic=" + new string('a', 4096));
        }
        else if (field == "encoding-count")
            for (var i = 0; i < 17; i++) response.Content.Headers.ContentEncoding.Add("identity");
        else response.Content.Headers.ContentEncoding.Add(new string('a', 257));
        return response;
    }

    private sealed record Request(string Method, string Source, string? Body, string Encoding, bool Authenticated, TimeSpan Elapsed);
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public List<Request> Requests { get; } = [];
        public Func<int, HttpRequestMessage, HttpResponseMessage?>? Reply { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = Requests.Count;
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
                request.Headers.AcceptEncoding.ToString(), request.Headers.Authorization is not null ||
                request.Headers.Contains("Cookie") || request.Headers.Contains("AUTH_KEY"), watch.Elapsed));
            var response = Reply?.Invoke(index, request) ?? Html(index switch { 0 => List(), 1 => Viewer(), 2 => Contents(), _ => throw new InvalidOperationException("More than three requests.") });
            response.RequestMessage ??= request;
            return response;
        }
    }
    private sealed class Wrapper(HttpMessageHandler inner) : DelegatingHandler(inner);
    private class UnseekableStream(byte[] raw) : Stream
    {
        private readonly MemoryStream inner = new(raw);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(inner.Read(buffer.Span)); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class BlockingStream(CancellationTokenSource cancel) : UnseekableStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); return 0; }
    }
    private sealed class FailedStream() : UnseekableStream([])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromException<int>(new IOException("synthetic private stream path"));
    }
}
