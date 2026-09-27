using System.Net;
using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class DartResearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(9));
    private static readonly DartFactQuery Financial = new("FINANCIAL", "00126380", 2025, "11012", "CFS");
    private const string RawFinancial = """{"status":"000","list":[{"rcept_no":"20250814000123","corp_code":"00126380","bsns_year":"2025","reprt_code":"11012","sj_div":"IS","account_id":"ifrs-full_Revenue","account_nm":"매출액","thstrm_amount":"1,200","thstrm_add_amount":"2,000","frmtrm_q_amount":"1,000","currency":"KRW"}]}""";
    private const string RawDisclosure = """{"status":"000","page_no":1,"total_page":1,"total_count":1,"list":[{"corp_code":"00126380","corp_name":"fixture","stock_code":"005930","report_nm":"반기보고서","rcept_no":"20250814000123","rcept_dt":"20250814","rm":"U"}]}""";
    private static DartFactSnapshot Snapshot(string json = RawFinancial, DateTimeOffset? at = null, DartFactQuery? query = null)
    {
        var q = query ?? Financial; var parsed = OpenDartClient.ParseFacts(q, json);
        return new(Guid.NewGuid().ToString("N"), q, parsed.Status, parsed.Rows, at ?? Now, DartFactSnapshot.Hash(json), json);
    }
    private static DisclosureBatch Disclosures(DateTimeOffset observed)
    {
        var parsed = OpenDartClient.ParsePage(RawDisclosure, observed);
        return new("fixture", new(2025, 8, 14), new(2025, 8, 14), parsed.Disclosures, [new(DartFactSnapshot.Hash(RawDisclosure), RawDisclosure, observed)]);
    }
    [Fact]
    public async Task FinancialRequestPreservesBasisCurrencyAndQuarterVsCumulativeAmounts()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("opendart.fss.or.kr", request.RequestUri!.Host); Assert.Equal("/api/fnlttSinglAcntAll.json", request.RequestUri.AbsolutePath);
            Assert.Contains("bsns_year=2025", request.RequestUri.Query); Assert.Contains("reprt_code=11012", request.RequestUri.Query);
            Assert.Contains("fs_div=CFS", request.RequestUri.Query); Assert.Equal(HttpMethod.Get, request.Method);
            return new(HttpStatusCode.OK) { Content = new StringContent(RawFinancial) };
        });
        using var http = new HttpClient(handler); var result = await new OpenDartClient(http, () => Now).Facts(new string('a', 40), Financial);
        result.Validate(Now); Assert.Equal(Now, result.ObservedAt); Assert.Equal(RawFinancial, result.RawJson);
        var row = Assert.Single(result.Rows); Assert.Equal("1,200", row.Fields["thstrm_amount"]); Assert.Equal("2,000", row.Fields["thstrm_add_amount"]);
        Assert.Equal("KRW", row.Fields["currency"]); Assert.Equal("CFS", result.Query.StatementType);
        Assert.NotEqual(Financial.Identity, (Financial with { StatementType = "OFS" }).Identity);
    }
    [Theory]
    [InlineData("BUYBACK", "/api/tsstkAqDecsn.json")]
    [InlineData("CAPITAL_INCREASE", "/api/piicDecsn.json")]
    public async Task MaterialEventEndpointsPreserveNoDataAsObservedResponse(string kind, string path)
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(path, request.RequestUri!.AbsolutePath); Assert.Contains("bgn_de=20260901", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"013\"}") };
        });
        using var http = new HttpClient(handler);
        var result = await new OpenDartClient(http, () => Now).Facts(new string('a', 40), new(kind, "00126380", Start: new(2026, 9, 1), End: new(2026, 9, 27)));
        result.Validate(Now); Assert.Empty(result.Rows); Assert.Equal("013", result.ApiStatus);
    }
    [Fact]
    public void ParserRejectsWrongCompanyYearReportOrDuplicateRowsAndArchiveTampering()
    {
        foreach (var raw in new[] { RawFinancial.Replace("00126380", "99999999"), RawFinancial.Replace("2025", "2024"),
            RawFinancial.Replace("11012", "11011"), RawFinancial.Replace("\"currency\":\"KRW\"", "\"currency\":100") })
            Assert.Throws<ArgumentException>(() => OpenDartClient.ParseFacts(Financial, raw));
        var snapshot = Snapshot(); snapshot.Rows[0].Fields["thstrm_amount"] = "9000000";
        Assert.Throws<ArgumentException>(() => snapshot.Validate(Now));
        Assert.Throws<ArgumentException>(() => (Snapshot() with { ObservedAt = Now.AddMinutes(1) }).Validate(Now));
        using var duplicate = JsonDocument.Parse(RawFinancial);
        var row = duplicate.RootElement.GetProperty("list")[0].GetRawText();
        Assert.Throws<ArgumentException>(() => OpenDartClient.ParseFacts(Financial, "{\"status\":\"000\",\"list\":[" + row + "," + row + "]}"));
    }
    [Fact]
    public void FactsCannotBeUsedBeforeBothObservationAndLinkedDisclosureAvailability()
    {
        var snapshot = Snapshot(); var batch = Disclosures(Now);
        Assert.Empty(DartResearchIndex.At([snapshot], [batch], Financial.CorpCode, Now.AddSeconds(-1), Now));
        Assert.Empty(DartResearchIndex.At([snapshot], [], Financial.CorpCode, Now, Now));
        var row = Assert.Single(DartResearchIndex.At([snapshot], [batch], Financial.CorpCode, Now, Now));
        Assert.Equal(Now, row.AvailableAt); Assert.Equal("CFS", row.Query.StatementType); Assert.DoesNotContain("rm", row.Fields.Keys);
        var sameDay = new DateTimeOffset(2025, 8, 14, 12, 0, 0, TimeSpan.FromHours(9));
        Assert.Empty(DartResearchIndex.At([Snapshot(at: sameDay)], [Disclosures(sameDay)], Financial.CorpCode, sameDay, Now));
    }
    [Fact]
    public void FutureCorrectionsCannotChangePastResearchInputsAndConflictingSameTimeRequiresReview()
    {
        var first = Snapshot(at: Now.AddHours(-2)); var batch = Disclosures(Now.AddHours(-2));
        var corrected = Snapshot(RawFinancial.Replace("1,200", "9,000"), at: Now);
        var cutoff = Now.AddHours(-1);
        var original = DartResearchIndex.At([first], [batch], Financial.CorpCode, cutoff, Now);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(DartResearchIndex.At([first, corrected], [batch], Financial.CorpCode, cutoff, Now)));
        Assert.Equal("9,000", Assert.Single(DartResearchIndex.At([first, corrected], [batch], Financial.CorpCode, Now, Now)).Fields["thstrm_amount"]);
        Assert.Throws<ArgumentException>(() => DartResearchIndex.At([first, corrected with { ObservedAt = first.ObservedAt }], [batch], Financial.CorpCode, Now, Now));
        var tamperedBatch = batch with { Disclosures = [batch.Disclosures[0] with { UsableAt = cutoff.AddYears(-1) }] };
        Assert.Throws<ArgumentException>(() => DartResearchIndex.At([first], [tamperedBatch], Financial.CorpCode, Now, Now));
    }
    [Fact]
    public async Task ReflectedKeyAndProviderMessagesAreNotExposedOrArchived()
    {
        var key = new string('b', 40);
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"" + key + "\"}") }));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new OpenDartClient(http, () => Now).Facts(key, Financial));
        Assert.DoesNotContain(key, exception.Message);
        var invalid = Assert.Throws<InvalidOperationException>(() => OpenDartClient.ParseFacts(Financial, "{\"status\":\"private provider text\"}"));
        Assert.DoesNotContain("private provider text", invalid.Message);
        var escapedKey = string.Concat(key.Select(c => "\\u" + ((int)c).ToString("x4")));
        using var escaped = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"000\",\"message\":\"" + escapedKey + "\"}") }));
        var reflected = await Assert.ThrowsAsync<InvalidOperationException>(() => new OpenDartClient(escaped, () => Now).Facts(key, Financial));
        Assert.DoesNotContain(key, reflected.Message);
        var diagnostic = await DartConnection.Check(escaped, key, "00126380", "fixture");
        Assert.Equal("RESPONSE_CONTAINS_CREDENTIAL", diagnostic.Status); Assert.Null(diagnostic.Company);
    }
    [Fact]
    public async Task CollectorHonorsBudgetAndResumeDoesNotRefetchOrAcceptModifiedCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "dart-tests-" + Guid.NewGuid().ToString("N"));
        var second = Financial with { StatementType = "OFS" }; var plan = new DartCollectionPlan("fixture", [Financial, second]);
        var calls = 0; Task<DartFactSnapshot> Fetch(DartFactQuery query, CancellationToken ct) { calls++; return Task.FromResult(Snapshot(query: query)); }
        try
        {
            var collector = new DartCollector(root, () => Now);
            var first = await collector.Run(plan, 1, TimeSpan.FromSeconds(1), "fixture-code", Fetch);
            Assert.Equal("REQUEST_BUDGET_REACHED", first.Status); Assert.Equal(1, calls); Assert.Single(first.Pending);
            var secondRun = await collector.Run(plan, 1, TimeSpan.FromSeconds(1), "fixture-code", Fetch);
            Assert.Equal("COLLECTED_UNREVIEWED", secondRun.Status); Assert.Equal(2, calls); Assert.Equal(2, secondRun.SnapshotFiles.Length);
            var cached = JsonSerializer.Deserialize<DartFactSnapshot>(File.ReadAllText(secondRun.SnapshotFiles[0]))!;
            File.WriteAllText(secondRun.SnapshotFiles[0], JsonSerializer.Serialize(cached with { RawHash = "tampered" }));
            await Assert.ThrowsAsync<ArgumentException>(() => collector.Run(plan, 1, TimeSpan.FromSeconds(1), "fixture-code", Fetch)); Assert.Equal(2, calls);
        }
        finally { DeleteFixture(root); }
    }
    [Fact]
    public async Task CollectorStopsOnFirstFailureAndPreservesAttemptWithoutExceptionSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "dart-tests-" + Guid.NewGuid().ToString("N")); var secret = new string('z', 40);
        try
        {
            var calls = 0;
            var result = await new DartCollector(root, () => Now).Run(new("failure", [Financial, Financial with { StatementType = "OFS" }]),
                2, TimeSpan.FromSeconds(1), "fixture-code", (_, _) => { calls++; throw new InvalidOperationException(secret); });
            Assert.Equal(1, calls); Assert.Equal("REQUEST_FAILED", result.Status); Assert.Empty(result.SnapshotFiles); Assert.Equal(2, result.Pending.Length);
            Assert.Single(Directory.GetFiles(root, "dart-attempt-*.json", SearchOption.AllDirectories));
            foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)) Assert.DoesNotContain(secret, File.ReadAllText(file));
        }
        finally { DeleteFixture(root); }
    }
    [Fact]
    public async Task InvalidPlanCannotCreateEvidenceAndNoDataIsNotMistakenForFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "dart-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var calls = 0; var collector = new DartCollector(root, () => Now);
            Task<DartFactSnapshot> Fetch(DartFactQuery query, CancellationToken ct) { calls++; return Task.FromResult(Snapshot("{\"status\":\"013\"}", query: query)); }
            await Assert.ThrowsAsync<ArgumentException>(() => collector.Run(new("invalid", [Financial, Financial]), 1, TimeSpan.FromSeconds(1), "fixture", Fetch));
            Assert.False(Directory.Exists(root)); Assert.Equal(0, calls);
            var result = await collector.Run(new("empty", [Financial]), 1, TimeSpan.FromSeconds(1), "fixture", Fetch);
            Assert.Equal("COLLECTED_UNREVIEWED", result.Status); Assert.Single(result.SnapshotFiles);
            Assert.Empty(JsonSerializer.Deserialize<DartFactSnapshot>(File.ReadAllText(result.SnapshotFiles[0]))!.Rows);
        }
        finally { if (Directory.Exists(root)) DeleteFixture(root); }
    }
    private static void DeleteFixture(string root)
    {
        var absolute = Path.GetFullPath(root);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(absolute), "^dart-tests-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Test cleanup path escaped its temporary fixture.");
        Directory.Delete(absolute, true);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
