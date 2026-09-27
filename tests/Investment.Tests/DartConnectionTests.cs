using System.Net;
using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class DartConnectionTests
{
    private static readonly string Key = new('a', 40);
    private const string Company = """{"status":"000","corp_code":"00126380","corp_name":"sample","stock_code":"005930"}""";

    [Theory]
    [InlineData("https://opendart.fss.or.kr/error1.html", "OFFICIAL_ERROR_PAGE")]
    [InlineData("https://other.example/steal", "UNTRUSTED_DESTINATION")]
    [InlineData("http://opendart.fss.or.kr/error1.html", "UNTRUSTED_DESTINATION")]
    [InlineData("https://opendart.fss.or.kr:444/error1.html", "UNTRUSTED_DESTINATION")]
    public async Task RedirectsDoNotAuthenticateAndNeverPublishLocationOrReflectedKey(string location, string expected)
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent(Key) };
            response.Headers.Location = new Uri(location + "?crtfc_key=" + Key); return response;
        });
        using var http = new HttpClient(handler);
        var receipt = await DartConnection.Check(http, Key, "00126380", "probe");
        Assert.False(receipt.Authenticated); Assert.Equal("REDIRECT_REFUSED", receipt.Status);
        Assert.Equal(expected, receipt.RedirectClass); Assert.Equal(302, receipt.HttpStatus);
        Assert.Null(receipt.Company); Assert.Null(receipt.ResponseHash); Assert.Equal(1, handler.Calls);
        var serialized = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain(Key, serialized); Assert.DoesNotContain(location, serialized);
    }

    [Theory]
    [InlineData("{\"status\":\"010\",\"message\":\"KEY\"}", "RESPONSE_CONTAINS_CREDENTIAL")]
    [InlineData("{\"status\":\"012\",\"message\":\"IP denied\"}", "API_REJECTED")]
    [InlineData("<html>not an API</html>", "INVALID_RESPONSE")]
    [InlineData("{\"status\":\"unrecognized private text\"}", "INVALID_RESPONSE")]
    [InlineData("{\"status\":\"000\",\"corp_code\":\"99999999\",\"corp_name\":\"wrong company\",\"stock_code\":\"005930\"}", "INVALID_RESPONSE")]
    public async Task FailureBodyIsNotSavedAndInvalidDataDoesNotEstablishAuthentication(string body, string expected)
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body.Replace("KEY", Key)) }));
        var receipt = await DartConnection.Check(http, Key, "00126380", "probe");
        Assert.False(receipt.Authenticated); Assert.Equal(expected, receipt.Status); Assert.Null(receipt.Company);
        Assert.NotNull(receipt.ResponseHash); Assert.DoesNotContain(Key, JsonSerializer.Serialize(receipt));
        if (expected == "API_REJECTED") Assert.Equal("012", receipt.ApiStatus);
        else Assert.Null(receipt.ApiStatus);
    }

    [Fact]
    public async Task SuccessRetainsCompanySnapshotAndActualObservationTimeOnly()
    {
        var before = DateTimeOffset.UtcNow;
        var handler = new Handler(request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("opendart.fss.or.kr", request.RequestUri.Host);
            Assert.Equal("/api/company.json", request.RequestUri.AbsolutePath); Assert.Equal(HttpMethod.Get, request.Method);
            return new(HttpStatusCode.OK) { Content = new StringContent(Company) };
        });
        using var http = new HttpClient(handler);
        var receipt = await DartConnection.Check(http, Key, "00126380", "probe");
        Assert.True(receipt.Authenticated); Assert.Equal("AUTHENTICATED", receipt.Status); Assert.Equal("000", receipt.ApiStatus);
        Assert.Equal(Company, receipt.Company!.RawJson); Assert.Equal("005930", receipt.Company.StockCode);
        Assert.InRange(receipt.Company.ObservedAt, before, receipt.CompletedAt);
        Assert.Equal(1, handler.Calls); Assert.DoesNotContain(Key, JsonSerializer.Serialize(receipt));
    }

    [Fact]
    public async Task NetworkExceptionsAndOversizedResponsesFailWithoutRetryOrSecretInReceipt()
    {
        var handler = new Handler(_ => throw new HttpRequestException("private URL with " + Key));
        using var http = new HttpClient(handler);
        var receipt = await DartConnection.Check(http, Key, "00126380", "probe");
        Assert.Equal("NETWORK_ERROR", receipt.Status); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(receipt));
        using var large = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1_000_001)) }));
        var tooLarge = await DartConnection.Check(large, Key, "00126380", "probe");
        Assert.Equal("RESPONSE_TOO_LARGE", tooLarge.Status); Assert.Null(tooLarge.Company); Assert.Null(tooLarge.ResponseHash);
    }

    [Fact]
    public async Task InvalidLocalInputsDoNotSendRequests()
    {
        var handler = new Handler(_ => throw new Exception("Must not send")); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => DartConnection.Check(http, "bad", "00126380", "probe"));
        await Assert.ThrowsAsync<ArgumentException>(() => DartConnection.Check(http, Key, "../path", "probe"));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(respond(request)); }
    }
}
