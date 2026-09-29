using System.Net;
using System.Security.Cryptography;
using System.Text;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KrxCalendarTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 29, 8, 0, 0, TimeSpan.FromHours(9));
    private const string Raw = """
        {"block1":[{"calnd_dd":"2026-09-25","dy_tp_cd":"FRI","calnd_dd_dy":"2026-09-25",
        "kr_dy_tp":"Friday","holdy_eng_nm":"Chuseok (Korean Thanksgiving)"},
        {"calnd_dd":"2026-09-24","dy_tp_cd":"THU","calnd_dd_dy":"2026-09-24",
        "kr_dy_tp":"Thursday","holdy_eng_nm":""}]}
        """;

    [Fact] public async Task CalendarUsesOfficialPublicFormAndPreservesRawProvenance()
    {
        using var handler = new Handler();
        using var client = new KrxCalendarClient(handler, () => Observed);
        var snapshot = await client.Year(2026);
        Assert.Equal(Raw, snapshot.RawJson);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Raw))), snapshot.RawHash);
        Assert.Equal(Observed, snapshot.ObservedAt);
        Assert.Equal(2026, snapshot.Year);
        Assert.Equal(KrxCalendarClient.Source, snapshot.Source);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal(KrxCalendarClient.Source, handler.Requests[0].Url);
        Assert.StartsWith("https://global.krx.co.kr/contents/COM/GenerateOTP.jspx?", handler.Requests[1].Url);
        Assert.Equal("POST", handler.Requests[2].Method);
        Assert.Equal("https://global.krx.co.kr/contents/GLB/99/GLB99000001.jspx", handler.Requests[2].Url);
        Assert.Equal("search_bas_yy=2026&code=public%2Bform%2Ftoken%3D", handler.Requests[2].Body);
        Assert.All(handler.Requests, request => Assert.Equal(KrxCalendarClient.Source, request.Referrer));
        Assert.All(handler.Requests, request => Assert.Equal("KrxStrategyResearch/1.0", request.UserAgent));
        Assert.All(handler.Requests, request => Assert.False(request.Authenticated));
        Assert.Equal("", snapshot.Holidays[0].Reason);
        Assert.Equal(new[] { new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 28) },
            KrxCalendarClient.CandidateSessions(snapshot, new(2026, 9, 23), new(2026, 9, 28)));
        Assert.Throws<ArgumentException>(() => KrxCalendarClient.CandidateSessions(snapshot with { RawHash = "tampered" }, new(2026, 9, 23), new(2026, 9, 28)));
        Assert.Throws<ArgumentException>(() => KrxCalendarClient.CandidateSessions(snapshot, new(2025, 12, 31), new(2026, 1, 1)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"block1\":[]}")]
    [InlineData("{\"error\":\"upstream unavailable\"}")]
    [InlineData("{\"block1\":null}")]
    public void EmptyAndErrorPayloadsCannotBecomeCalendar(string raw)
        => Assert.Throws<InvalidOperationException>(() => KrxCalendarClient.Parse(raw, 2026));

    [Fact] public void CalendarRejectsWrongYearDuplicateDayAndMalformedDay()
    {
        Assert.Throws<ArgumentException>(() => KrxCalendarClient.Parse(Raw, 2025));
        Assert.Throws<ArgumentException>(() => KrxCalendarClient.Parse(Raw.Replace("2026-09-24", "2026-09-25").Replace("THU", "FRI").Replace("Thursday", "Friday"), 2026));
        Assert.Throws<ArgumentException>(() => KrxCalendarClient.Parse(Raw.Replace("2026-09-25", "2026-09-31"), 2026));
        Assert.Throws<ArgumentException>(() => KrxCalendarClient.Parse(Raw.Replace("FRI", "THU"), 2026));
        Assert.Throws<ArgumentException>(() => KrxCalendarClient.Parse(Raw.Replace("Friday", "Thursday"), 2026));
    }

    [Theory]
    [InlineData(0, HttpStatusCode.Found)]
    [InlineData(1, HttpStatusCode.Forbidden)]
    [InlineData(2, HttpStatusCode.TooManyRequests)]
    public async Task FailedStepStopsWithoutRetry(int step, HttpStatusCode status)
    {
        using var handler = new Handler(step, status);
        using var client = new KrxCalendarClient(handler, () => Observed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Year(2026));
        Assert.Equal(step + 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData("<html>login</html>", "text/html")]
    [InlineData(Raw, "text/plain")]
    public async Task NonJsonResponseRejected(string content, string contentType)
    {
        using var handler = new Handler(query: content, queryType: contentType);
        using var client = new KrxCalendarClient(handler, () => Observed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Year(2026));
    }

    [Fact] public async Task OfficialHtmlMimeTypeIsAcceptedOnlyWithValidatedJson()
    {
        using var handler = new Handler(queryType: "text/html");
        using var client = new KrxCalendarClient(handler, () => Observed);
        Assert.Equal(2, (await client.Year(2026)).Holidays.Length);
    }

    [Fact] public async Task UnknownScreenYearAndHtmlTokenRejectedBeforeQuery()
    {
        using var handler = new Handler();
        using var client = new KrxCalendarClient(handler, () => Observed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Year(2025));
        Assert.Single(handler.Requests);
        using var loginHandler = new Handler(token: "<html>login</html>");
        using var loginClient = new KrxCalendarClient(loginHandler, () => Observed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loginClient.Year(2026));
        Assert.Equal(2, loginHandler.Requests.Count);
        using var redirectHandler = new HttpClientHandler();
        Assert.Throws<ArgumentException>(() => new KrxCalendarClient(redirectHandler));
    }

    private sealed record Request(string Method, string Url, string? Body, string? Referrer, bool Authenticated, string UserAgent);
    private sealed class Handler(int failureStep = -1, HttpStatusCode status = HttpStatusCode.OK,
        string query = Raw, string queryType = "application/json", string token = "public+form/token=") : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = Requests.Count;
            Requests.Add(new(request.Method.Method, request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct), request.Headers.Referrer?.ToString(),
                request.Headers.Contains("AUTH_KEY") || request.Headers.Authorization is not null, request.Headers.UserAgent.ToString()));
            var content = step switch
            {
                0 => "<select name=\"search_bas_yy\"><option value=\"2026\">2026</option></select>",
                1 => token,
                _ => query
            };
            return new HttpResponseMessage(step == failureStep ? status : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content, Encoding.UTF8, step == 2 ? queryType : "text/html")
            };
        }
    }
}
