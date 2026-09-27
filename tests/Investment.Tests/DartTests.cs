using Investment.Core;
using System.Net;
using Xunit;

namespace Investment.Tests;

public sealed class DartTests
{
    private const string Response = """
        {"status":"000","message":"정상","page_no":1,"total_page":1,"total_count":1,"list":[
        {"corp_code":"00126380","corp_name":"sample","stock_code":"005930","report_nm":"사업보고서","rcept_no":"20240315000123","rcept_dt":"20240315","rm":"U"}]}
        """;
    [Fact] public void NewlyFetchedHistoricalFilingIsNotBackdated()
    {
        var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(9));
        var d = Assert.Single(OpenDartClient.ParsePage(Response, now).Disclosures);
        Assert.Equal(now, d.UsableAt); Assert.Equal(new DateOnly(2024, 3, 15), d.ReceiptDate);
    }
    [Fact] public void DatePrecisionForcesNextDayAvailability()
    {
        var now = new DateTimeOffset(2024, 3, 15, 10, 0, 0, TimeSpan.FromHours(9));
        var d = Assert.Single(OpenDartClient.ParsePage(Response, now).Disclosures);
        Assert.Equal(new DateTimeOffset(2024, 3, 16, 0, 0, 0, TimeSpan.FromHours(9)), d.UsableAt);
    }
    [Fact] public void NoDataIsDifferentFromApiFailure()
    {
        Assert.Empty(OpenDartClient.ParsePage("{\"status\":\"013\"}", DateTimeOffset.UtcNow).Disclosures);
        Assert.Throws<InvalidOperationException>(() => OpenDartClient.ParsePage("{\"status\":\"020\"}", DateTimeOffset.UtcNow));
    }
    [Fact] public async Task ClientUsesOfficialReadOnlyEndpointAndPreservesRawResponse()
    {
        var handler = new FakeHandler(Response); using var http = new HttpClient(handler);
        var now = new DateTimeOffset(2024, 3, 16, 10, 0, 0, TimeSpan.FromHours(9));
        var result = await new OpenDartClient(http, () => now).Search(new string('a', 40), new(2024, 3, 15), new(2024, 3, 15));
        Assert.Equal("opendart.fss.or.kr", handler.Uri!.Host); Assert.Equal("/api/list.json", handler.Uri.AbsolutePath);
        Assert.Contains("last_reprt_at=N", handler.Uri.Query); Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Single(result.Disclosures); Assert.Equal(Response, Assert.Single(result.Pages).Json);
    }
    [Fact] public async Task InvalidAuthKeyDoesNotSendNetworkRequest()
    {
        var handler = new FakeHandler(Response); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new OpenDartClient(http).Search("bad", new(2024, 3, 15), new(2024, 3, 15)));
        Assert.Null(handler.Uri);
    }
    private sealed class FakeHandler(string response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; } public HttpMethod? Method { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Uri = request.RequestUri; Method = request.Method; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) }); }
    }
}
