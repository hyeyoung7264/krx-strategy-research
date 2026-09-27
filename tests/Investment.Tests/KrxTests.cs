using Investment.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Investment.Tests;

public sealed class KrxTests
{
    private const string Response = """
        {"OutBlock_1":[{"BAS_DD":"20260925","ISU_CD":"005930","ISU_NM":"sample","MKT_NM":"KOSPI","SECT_TP_NM":"우량기업부",
        "TDD_OPNPRC":"10,000","TDD_HGPRC":"11,000","TDD_LWPRC":"9,000","TDD_CLSPRC":"10,500","ACC_TRDVOL":"1,000,000","ACC_TRDVAL":"10,500,000,000","MKTCAP":"100,000,000,000","LIST_SHRS":"10,000,000"}]}
        """;
    private static readonly DateOnly Date = new(2026, 9, 25);
    private static KrxSnapshot Snapshot()
    {
        var observed = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(9));
        return new("test", "KOSPI", Date, observed, "official-fixture", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Response))), Response, KrxClient.Parse(Response, Date, "KOSPI"));
    }
    [Fact] public void ParsesPricesSharesAndPreservesLeadingZeroTicker()
    {
        var row = Assert.Single(KrxClient.Parse(Response, Date, "KOSPI"));
        Assert.Equal("005930", row.Ticker); Assert.Equal(10000, row.Open); Assert.Equal(100000000000, row.MarketCap); Assert.Equal(10000000, row.SharesOutstanding);
    }
    [Fact] public void PlaceholderIsMissingNotZero()
    {
        var row = Assert.Single(KrxClient.Parse(Response.Replace("\"10,000\"", "\"-\"", StringComparison.Ordinal), Date, "KOSPI"));
        Assert.Null(row.Open);
        Assert.Throws<ArgumentException>(() => KrxClient.Parse(Response, Date.AddDays(-1), "KOSPI"));
        Assert.Throws<InvalidOperationException>(() => KrxClient.Parse("{\"error\":\"authentication\"}", Date, "KOSPI"));
    }
    [Fact] public void UnreviewedHistoryIsNotBackdatedAndListingSectionIsNotIndustry()
    {
        var snapshot = Snapshot(); var manifest = new KrxManifest("test", [], [Date], [new("005930", Date, "historical-industry", true, true, Clock.Close(Date))]);
        var dataset = KrxDatasetBuilder.Build([snapshot], manifest);
        Assert.False(dataset.PointInTimeCertified); Assert.Equal(snapshot.ObservedAt, Assert.Single(dataset.Bars).AvailableAt);
        Assert.Equal("historical-industry", dataset.Bars[0].Sector);
    }
    [Fact] public void MissingSelectedSecurityOrMutatedRawEvidenceIsRejected()
    {
        var snapshot = Snapshot(); var manifest = new KrxManifest("test", [], [Date], [new("000001", Date, "s", true, true, Clock.Close(Date))]);
        Assert.Throws<ArgumentException>(() => KrxDatasetBuilder.Build([snapshot], manifest));
        Assert.Throws<ArgumentException>(() => KrxDatasetBuilder.Build([snapshot with { RawHash = "bad" }], manifest));
    }
    [Fact] public async Task RequestUsesOfficialHttpsEndpointAndHeaderAuthentication()
    {
        var handler = new FakeHandler(); using var http = new HttpClient(handler);
        var result = await new KrxClient(http, () => Snapshot().ObservedAt).Daily("fake-test-key", "KOSPI", Date);
        Assert.Equal("data-dbg.krx.co.kr", handler.Uri!.Host); Assert.Equal("https", handler.Uri.Scheme);
        Assert.Equal("/svc/apis/sto/stk_bydd_trd", handler.Uri.AbsolutePath); Assert.Equal("?basDd=20260925", handler.Uri.Query);
        Assert.True(handler.HasAuthHeader); Assert.Equal(HttpMethod.Get, handler.Method); Assert.Single(result.Rows);
    }
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; } public bool HasAuthHeader { get; private set; } public HttpMethod? Method { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Uri = request.RequestUri; HasAuthHeader = request.Headers.Contains("AUTH_KEY"); Method = request.Method; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response) }); }
    }
}
