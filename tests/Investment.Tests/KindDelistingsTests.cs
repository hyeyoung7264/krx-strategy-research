using System.Net;
using System.Globalization;
using System.Text;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KindDelistingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 1, 0, 0, TimeSpan.FromHours(9));
    private static readonly KindDelistingQuery Query = new(new(2026, 9, 1), new(2026, 9, 28), "ALL", 15);

    [Fact] public async Task PublicGetPreservesEveryPageAndVerifiesCompleteSnapshot()
    {
        using var handler = new Handler(Html(16, 1), Html(16, 2));
        using var client = new KindDelistingClient(handler, () => Now);
        var preserved = new List<KindDelistingPage>();
        var snapshot = await client.Fetch(Query, interval: TimeSpan.FromMilliseconds(250),
            preservePage: page => { preserved.Add(page); return Task.CompletedTask; });
        snapshot.Validate(Now);
        Assert.Equal(2, preserved.Count);
        Assert.Equal(16, snapshot.Pages.Sum(p => p.Rows.Length));
        Assert.Equal(Html(16, 1), snapshot.Pages[0].RawHtml);
        Assert.Equal(preserved, snapshot.Pages);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.StartsWith("https://kind.krx.co.kr/investwarn/delcompany.do?method=searchDelCompanySub&", request.Uri);
            Assert.Equal("KrxStrategyResearch/1.0", request.Agent);
            Assert.False(request.Authenticated);
        });
        Assert.Contains("currentPageSize=15&pageIndex=2&fromDate=2026-09-01&toDate=2026-09-28&marketType=", handler.Requests[1].Uri);
        var row = snapshot.Pages[0].Rows[0];
        Assert.Equal("00001", row.IssuerId);
        Assert.Equal("가상 회사 1", row.CompanyName);
        Assert.Equal("합성 사유 & 확인", row.Reason);
        Assert.Equal("KOSDAQ", row.Market);
    }

    [Fact] public void SyntheticAlphanumericCompanyIdIsPreservedWithoutTickerInference()
    {
        var parsed = KindDelistingClient.ParsePage(Html(1, 1).Replace("'00001'", "'0197V'"), Query, 1);
        Assert.Equal("0197V", Assert.Single(parsed.Rows).IssuerId);
    }

    [Theory]
    [InlineData("<html>LOGIN</html>")]
    [InlineData("{\"error\":\"unavailable\"}")]
    [InlineData("")]
    public void ErrorDocumentsAreNotEmptyResults(string html)
        => Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(html, Query, 1));

    [Fact] public void EmptyUnverifiedStructureIsRejected()
        => Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(Html(0, 1), Query, 1));

    [Fact] public void StructuralMetadataAndRangeMismatchesFailClosed()
    {
        var html = Html(1, 1);
        var malformed = new[]
        {
            html.Replace("번호, 회사명, 폐지일자, 폐지사유, 비고", "other"),
            html.Replace("<em>1</em>", "<em>2</em>"),
            html.Replace("<strong>1</strong>/1", "<strong>2</strong>/1"),
            html.Replace("value='15' selected", "value='30' selected"),
            html.Replace("2026-09-03", "2026-08-31"),
            html.Replace("alt='코스닥'", "alt='OTHER'"),
            html.Replace("'00001'", "'0001'"),
            html.Replace("title='가상 회사 1'", "title='다른 회사'"),
            html.Replace("합성 사유 &amp; 확인", ""),
            html.Replace("<td>1</td>", "<td>2</td>"),
            html.Replace("</tbody>", "<div>unexpected</div></tbody>"),
            html + html
        };
        Assert.All(malformed, value => Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(value, Query, 1)));
        Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(html, Query with { Market = "KOSPI" }, 1));
    }

    [Fact] public void NestedStructuresAndAttributeSpoofingCannotForgeCompanyIdentity()
    {
        var html = Html(1, 1);
        Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(html.Replace("합성 사유", "<script>x</script>합성 사유"), Query, 1));
        Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(html.Replace("title='가상 회사 1'", "title='가상 회사 1' title='가상 회사 1'"), Query, 1));
        Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(html.Replace("onclick=\"companysummary_open('00001'); return false;\"", "data-note=\"onclick='companysummary_open(00001)'\""), Query, 1));
    }

    [Fact] public void DuplicateRowsAreRejected()
    {
        var html = Html(2, 1).Replace("'00002'", "'00001'");
        Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(html, Query, 1));
    }

    [Fact] public void HistoricalTransferRowKeepsHistoricalMarketWithoutCurrentDelistingBadge()
    {
        var html = RelatedListingHtml().Replace("합성 사유 &amp; 확인", "코스닥시장 이전상장");
        var row = Assert.Single(KindDelistingClient.ParsePage(html, Query, 1).Rows);
        Assert.Equal("KONEX", row.Market);
        Assert.Equal("00001", row.IssuerId);
        Assert.Equal("가상 회사 1", row.Remarks);
        Assert.Equal("코스닥시장 이전상장", row.Reason);
        Assert.Equal(new DateOnly(2026, 9, 3), row.DelistingDate);
    }

    [Fact] public void HistoricalMergerRowDoesNotRequireCurrentBadgeOrRelatedListingReference()
    {
        var html = Html(1, 1).Replace("<img alt='상장폐지' src='/status.gif'>", "")
            .Replace("합성 사유 &amp; 확인", "가상기업과의 스팩소멸합병");
        var row = Assert.Single(KindDelistingClient.ParsePage(html, Query, 1).Rows);
        Assert.Equal("KOSDAQ", row.Market);
        Assert.Equal("가상기업과의 스팩소멸합병", row.Reason);
        Assert.Empty(row.Remarks);
    }

    [Fact] public void MissingCurrentBadgeStillRequiresValidHistoricalEventFields()
    {
        var html = Html(1, 1).Replace("<img alt='상장폐지' src='/status.gif'>", "");
        var malformed = new[]
        {
            html.Replace("<img alt='코스닥' src='/market.gif'>", ""),
            html.Replace("alt='코스닥'", "alt='OTHER'"),
            html.Replace("<img alt='코스닥' src='/market.gif'>", "<img alt='코스닥'><img alt='유가증권'>"),
            html.Replace("2026-09-03", ""),
            html.Replace("2026-09-03", "2026-08-31"),
            html.Replace("합성 사유 &amp; 확인", ""),
            html.Replace("'00001'", "'0001'"),
            html.Replace("title='가상 회사 1'", "title='다른 회사'")
        };
        Assert.All(malformed, value => Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(value, Query, 1)));
    }

    [Fact] public void DuplicateCurrentDelistingBadgesRemainAmbiguous()
    {
        var html = Html(1, 1).Replace("<img alt='상장폐지' src='/status.gif'>",
            "<img alt='상장폐지' src='/status.gif'><img alt='상장폐지' src='/second.gif'>");
        Assert.Throws<InvalidOperationException>(() => KindDelistingClient.ParsePage(html, Query, 1));
    }

    private static string RelatedListingHtml() => Html(1, 1)
        .Replace("alt='코스닥'", "alt='코넥스'")
        .Replace("<img alt='상장폐지' src='/status.gif'>", "")
        .Replace("<td></td></tr>", """
            <td><img alt='코스닥' src='/related.gif'>
            <a data-related='yes' onclick="companysummary_open('00001'); return false;" title='가상 회사 1'>가상 회사 1</a>
            </td></tr>
            """);

    [Fact] public async Task FirstPageSurvivesSecondPageFailureAndNoRetryOccurs()
    {
        using var handler = new Handler(Html(16, 1), "<html>service unavailable</html>");
        using var client = new KindDelistingClient(handler, () => Now);
        var preserved = new List<KindDelistingPage>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Query, interval: TimeSpan.FromMilliseconds(250),
            preservePage: page => { preserved.Add(page); return Task.CompletedTask; }));
        Assert.Single(preserved);
        Assert.Equal(2, handler.Requests.Count);
        KindDelistingClient.ValidatePage(preserved[0], Now);
    }

    [Fact] public async Task BudgetAndPersistenceFailureStopBeforeNextRequest()
    {
        using var handler = new Handler(Html(16, 1));
        using var client = new KindDelistingClient(handler, () => Now);
        var preserved = new List<KindDelistingPage>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Query, maxPages: 1,
            preservePage: page => { preserved.Add(page); return Task.CompletedTask; }));
        Assert.Single(preserved); Assert.Single(handler.Requests);
        using var failedStoreHandler = new Handler(Html(16, 1));
        using var failedStoreClient = new KindDelistingClient(failedStoreHandler, () => Now);
        await Assert.ThrowsAsync<IOException>(() => failedStoreClient.Fetch(Query,
            preservePage: _ => throw new IOException("synthetic storage failure")));
        Assert.Single(failedStoreHandler.Requests);
    }

    [Fact] public async Task CancellationAfterFirstPreservedPageStopsCollection()
    {
        using var cancel = new CancellationTokenSource();
        using var handler = new Handler(Html(16, 1));
        using var client = new KindDelistingClient(handler, () => Now);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Fetch(Query,
            preservePage: _ => { cancel.Cancel(); return Task.CompletedTask; }, ct: cancel.Token));
        Assert.Single(handler.Requests);
    }

    [Fact] public async Task PageCountDriftAndCrossPageDuplicatesDoNotReturnSnapshot()
    {
        foreach (var second in new[] { Html(17, 2), Html(16, 2).Replace("'00016'", "'00001'") })
        {
            using var handler = new Handler(Html(16, 1), second);
            using var client = new KindDelistingClient(handler, () => Now);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Query, interval: TimeSpan.FromMilliseconds(250)));
            Assert.Equal(2, handler.Requests.Count);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "text/html")]
    [InlineData(HttpStatusCode.Forbidden, "text/html")]
    [InlineData(HttpStatusCode.TooManyRequests, "text/html")]
    [InlineData(HttpStatusCode.OK, "application/json")]
    public async Task UnexpectedHttpResponsesStopWithoutRetry(HttpStatusCode status, string type)
    {
        using var handler = new Handler(Html(1, 1)) { Status = status, ContentType = type };
        using var client = new KindDelistingClient(handler, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Query));
        Assert.Single(handler.Requests);
    }

    [Fact] public async Task OversizeBodyAndInvalidBudgetFail()
    {
        using var handler = new Handler(new string('x', KindDelistingClient.MaximumResponseBytes + 1));
        using var client = new KindDelistingClient(handler, () => Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Fetch(Query));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Fetch(Query, maxPages: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Fetch(Query, interval: TimeSpan.Zero));
        Assert.Single(handler.Requests);
        using var unsafeHandler = new HttpClientHandler();
        Assert.Throws<ArgumentException>(() => new KindDelistingClient(unsafeHandler));
    }

    [Fact] public async Task SnapshotRejectsTamperingFutureObservationAndIncompletePages()
    {
        using var handler = new Handler(Html(16, 1), Html(16, 2));
        using var client = new KindDelistingClient(handler, () => Now);
        var snapshot = await client.Fetch(Query, interval: TimeSpan.FromMilliseconds(250));
        var first = snapshot.Pages[0];
        Assert.Throws<ArgumentException>(() => (snapshot with { ObservedAt = Now.AddMinutes(1) }).Validate(Now));
        Assert.Throws<ArgumentException>(() => (snapshot with { Pages = [first] }).Validate(Now));
        Assert.Throws<ArgumentException>(() => KindDelistingClient.ValidatePage(first with { RawHash = "modified" }, Now));
        Assert.Throws<ArgumentException>(() => KindDelistingClient.ValidatePage(first with { Source = "https://example.com/" }, Now));
        Assert.Throws<ArgumentException>(() => KindDelistingClient.ValidatePage(first with { ObservedAt = Now.AddMinutes(1) }, Now));
        Assert.Throws<ArgumentException>(() => KindDelistingClient.ValidatePage(first with { ObservedAt = Now.AddMonths(-1) }, Now));
        Assert.Throws<ArgumentException>(() => KindDelistingClient.ValidatePage(first with { Rows = [] }, Now));
        Assert.Throws<ArgumentException>(() => KindDelistingClient.ValidatePage(first with { Query = Query with { Market = "KOSPI" } }, Now));
    }

    [Fact] public void RequestDatesUseGregorianInvariantCalendarUnderThaiCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Contains("fromDate=2026-09-01&toDate=2026-09-28", KindDelistingClient.RequestSource(Query, 1));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static string Html(int total, int page)
    {
        var rows = new StringBuilder();
        for (var index = (page - 1) * 15; index < Math.Min(total, page * 15); index++)
            rows.Append($"""
                <tr><td>{total - index}</td><td><img alt='코스닥' src='/market.gif'>
                <a onclick="companysummary_open('{index + 1:00000}'); return false;" title='가상 회사 {index + 1}'>가상 회사 {index + 1}</a>
                <img alt='상장폐지' src='/status.gif'></td><td>2026-09-03</td><td>합성 사유 &amp; 확인</td><td></td></tr>
                """);
        return $"""
            <section><table class="list type-00 tmt30" summary="번호, 회사명, 폐지일자, 폐지사유, 비고">
            <thead><tr></tr></thead><tbody>{rows}</tbody></table></section>
            <section class="paging-group"><div class="info type-00">
            전체 <em>{total}</em>건 : <strong>{page}</strong>/{Math.Max(1, (total + 14) / 15)}&nbsp;
            <select name='currentPageSize'><option value='15' selected='selected'>15건</option></select>
            </div></section>
            """;
    }

    private sealed record Request(string Method, string Uri, string Agent, bool Authenticated);
    private sealed class Handler(params string[] pages) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string ContentType { get; init; } = "text/html";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = Requests.Count;
            Requests.Add(new(request.Method.Method, request.RequestUri!.ToString(), request.Headers.UserAgent.ToString(),
                request.Headers.Authorization is not null || request.Headers.Contains("AUTH_KEY") || request.Headers.Contains("Cookie")));
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                RequestMessage = request,
                Content = new StringContent(pages[index], Encoding.UTF8, ContentType)
            });
        }
    }
}
