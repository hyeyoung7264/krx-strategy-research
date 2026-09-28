using System.Net;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KrxReferenceTests
{
    private static readonly DateOnly Date = new(2026, 9, 23);
    private static readonly DateTimeOffset Observed = new(2026, 9, 29, 8, 0, 0, TimeSpan.FromHours(9));
    private const string Basic = """
        {"OutBlock_1":[{"ISU_CD":"KR7005930003","ISU_SRT_CD":"005930","ISU_NM":"sample",
        "LIST_DD":"19750611","MKT_TP_NM":"KOSPI","SECUGRP_NM":"주권","SECT_TP_NM":"우량기업부",
        "KIND_STKCERT_TP_NM":"보통주","LIST_SHRS":"5,000,000,000"}]}
        """;
    private const string Index = """
        {"OutBlock_1":[{"BAS_DD":"20260923","IDX_CLSS":"KOSPI","IDX_NM":"코스피",
        "OPNPRC_IDX":"2,600.10","HGPRC_IDX":"2,610.20","LWPRC_IDX":"2,590.30",
        "CLSPRC_IDX":"2,605.40","ACC_TRDVOL":"100,000","ACC_TRDVAL":"300,000,000"}]}
        """;

    [Fact] public void BasicInfoUsesShortCodeAndPreservesListingSectionWithoutCallingItIndustry()
    {
        var row = Assert.Single(KrxReferenceClient.ParseBasicInfo(Basic));
        Assert.Equal("KR7005930003", row.StandardCode);
        Assert.Equal("005930", row.Ticker);
        Assert.Equal("19750611", row.ListingDate);
        Assert.Equal("우량기업부", row.ListingSection);
        Assert.Equal(5000000000, row.ListedShares);
        Assert.Throws<ArgumentException>(() => KrxReferenceClient.ParseBasicInfo(Basic.Replace("005930", "5930", StringComparison.Ordinal)));
    }

    [Fact] public void IndexKeepsNamedSeriesAndRejectsDateMismatch()
    {
        var row = Assert.Single(KrxReferenceClient.ParseIndex(Index, Date));
        Assert.Equal("코스피", row.IndexName);
        Assert.Equal(2605.40m, row.Close);
        Assert.Throws<ArgumentException>(() => KrxReferenceClient.ParseIndex(Index, Date.AddDays(-1)));
        Assert.Throws<InvalidOperationException>(() => KrxReferenceClient.ParseIndex("{\"error\":\"unauthorized\"}", Date));
    }

    [Theory]
    [InlineData("KOSPI", "sto/stk_isu_base_info", "idx/kospi_dd_trd")]
    [InlineData("KOSDAQ", "sto/ksq_isu_base_info", "idx/kosdaq_dd_trd")]
    public async Task ReferenceRequestsUseSeparateOfficialEndpointsAndHeader(string market, string basicEndpoint, string indexEndpoint)
    {
        using var handler = new Handler(Basic, Index);
        using var http = new HttpClient(handler);
        var client = new KrxReferenceClient(http, () => Observed);
        var basic = await client.BasicInfo("fixture-key", market, Date);
        var index = await client.IndexDaily("fixture-key", market, Date);
        Assert.Single(basic.Rows); Assert.Single(index.Rows);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://data-dbg.krx.co.kr/svc/apis/" + basicEndpoint + "?basDd=20260923", handler.Requests[0]);
        Assert.Equal("https://data-dbg.krx.co.kr/svc/apis/" + indexEndpoint + "?basDd=20260923", handler.Requests[1]);
        Assert.True(handler.AllAuthenticated);
        Assert.Equal(Observed, basic.ObservedAt);
        Assert.Equal(Observed, index.ObservedAt);
        await Assert.ThrowsAsync<ArgumentException>(() => client.BasicInfo("fixture-key", "OTHER", Date));
    }

    [Fact] public async Task ReferenceClientRejectsCrossMarketAndFutureListings()
    {
        using var wrongMarket = new Handler(Basic.Replace("\"KOSPI\"", "\"OTHER\"", StringComparison.Ordinal), Index);
        using var wrongMarketHttp = new HttpClient(wrongMarket);
        await Assert.ThrowsAsync<ArgumentException>(() => new KrxReferenceClient(wrongMarketHttp, () => Observed).BasicInfo("fixture-key", "KOSPI", Date));
        using var future = new Handler(Basic.Replace("19750611", "20260924", StringComparison.Ordinal), Index);
        using var futureHttp = new HttpClient(future);
        await Assert.ThrowsAsync<ArgumentException>(() => new KrxReferenceClient(futureHttp, () => Observed).BasicInfo("fixture-key", "KOSPI", Date));
        using var wrongIndex = new Handler(Basic, Index.Replace("\"KOSPI\"", "\"OTHER\"", StringComparison.Ordinal));
        using var wrongIndexHttp = new HttpClient(wrongIndex);
        await Assert.ThrowsAsync<ArgumentException>(() => new KrxReferenceClient(wrongIndexHttp, () => Observed).IndexDaily("fixture-key", "KOSPI", Date));
    }

    [Fact] public async Task UnauthorizedReferenceRequestStopsWithoutLeakingKeyOrRetrying()
    {
        using var handler = new Handler(Basic, Index, HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var client = new KrxReferenceClient(http, () => Observed);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.BasicInfo("private-fixture-key", "KOSPI", Date));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("private-fixture-key", error.Message);
        Assert.Single(handler.Requests);
    }

    private sealed class Handler(string basic, string index, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool AllAuthenticated { get; private set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            AllAuthenticated &= request.Method == HttpMethod.Get && request.Headers.Contains("AUTH_KEY");
            var content = request.RequestUri.AbsolutePath.Contains("_isu_base_info", StringComparison.Ordinal) ? basic : index;
            if (request.RequestUri.AbsolutePath.Contains("ksq_", StringComparison.Ordinal) ||
                request.RequestUri.AbsolutePath.Contains("kosdaq_", StringComparison.Ordinal))
                content = content.Replace("\"KOSPI\"", "\"KOSDAQ\"", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content)
            });
        }
    }
}
