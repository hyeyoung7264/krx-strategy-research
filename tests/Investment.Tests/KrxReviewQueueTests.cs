using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KrxReviewQueueTests
{
    private static readonly DateOnly First = new(2026, 9, 21);
    private static readonly DateOnly Second = First.AddDays(1);
    private static readonly DateOnly Third = First.AddDays(2);
    private static readonly DateTimeOffset Observed = Clock.Close(Second).AddHours(1);
    private static readonly DateTimeOffset Created = Observed.AddHours(1);
    private static readonly DateTimeOffset Now = Created.AddHours(1);
    private static readonly string[] Markets = ["KOSPI", "KOSDAQ"];
    private static readonly DateOnly[] Dates = [First, Second];
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RawHash(string market, DateOnly date, string service) => Digest($"synthetic:{market}:{date:yyyy-MM-dd}:{service}");
    private static string Source(string market, string service) => "https://data-dbg.krx.co.kr/svc/apis/" + ((service, market) switch
    {
        ("DAILY", "KOSPI") => "sto/stk_bydd_trd", ("DAILY", _) => "sto/ksq_bydd_trd",
        ("BASIC", "KOSPI") => "sto/stk_isu_base_info", ("BASIC", _) => "sto/ksq_isu_base_info",
        ("INDEX", "KOSPI") => "idx/kospi_dd_trd", _ => "idx/kosdaq_dd_trd"
    });
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static KrxAuditReport Price(string id = "price-report") => new(id, Created, Digest("price-selection"), "REVIEW_REQUIRED",
        Markets.SelectMany(m => Dates.Select(d => new KrxAuditInput(m, d, Observed, RawHash(m, d, "DAILY")))).ToArray(),
        Markets.SelectMany(m => Dates.Select(d => new KrxAuditDay(m, d, 2, d == First ? 1 : 0))).ToArray(),
        [new("MISSING_SHARE_COUNT", "KOSPI", First, "005930", "synthetic duplicate flag"),
         new("MISSING_SHARE_COUNT", "KOSPI", First, "005930", "synthetic duplicate flag"),
         new("MISSING_SNAPSHOT", "KOSPI", Third, null, "synthetic absent date"),
         new("MISSING_OHLCV", "KOSDAQ", First, "005930", "synthetic same ticker on another market")],
        [new("SHARE_COUNT_CHANGED_REQUIRES_REVIEW", "KOSPI", First, Second, "005930"),
         new("APPEARED_REQUIRES_LISTING_EVIDENCE", "KOSPI", First, Second, "000001")], "Synthetic audit fixture, not raw market evidence.");
    private static KrxReferenceAuditReport Reference(string id = "reference-report") => new(id, Created, Digest("reference-selection"), "REVIEW_REQUIRED",
        Markets.SelectMany(m => Dates.SelectMany(d => new[] { "DAILY", "BASIC", "INDEX" }.Select(service =>
            new KrxReferenceAuditInput(service, m, d, Observed, Source(m, service),
                RawHash(m, d, service), "not-read-" + service + ".json")))).ToArray(),
        Markets.SelectMany(m => Dates.Select(d => new KrxReferenceAuditDay(m, d, 2, 2, 1))).ToArray(),
        [new("SHARE_COUNT_DIFFERS", null, "KOSPI", First, "005930", "synthetic cross-service difference"),
         new("REQUIRED_INDEX_MISSING", "INDEX", "KOSPI", Second, null, "synthetic missing named index")],
        [new("KOSPI", First, Second, "005930", "KR7005930003", "KR7000660001")], "Synthetic audit fixture, not raw market evidence.");

    [Fact]
    public void EveryOriginalFlagIncludingDuplicatesResolvesToItsReportAndArrayPosition()
    {
        var price = Json(Price()); var reference = Json(Reference());
        var queue = KrxReviewQueue.Build([price], [reference], Now);
        var flags = queue.Buckets.SelectMany(b => b.SourceFlagReferences).ToArray();
        Assert.Equal(9, flags.Length);
        Assert.Equal(9, flags.Select(f => (f.ReportIndex, f.ArrayName, f.Index)).Distinct().Count());
        foreach (var source in queue.Sources)
        {
            Assert.Equal(Digest(source.RawJson), source.ReportDigest);
            using var doc = JsonDocument.Parse(source.RawJson);
            var report = source.ReportProperty == null ? doc.RootElement : doc.RootElement.GetProperty(source.ReportProperty);
            foreach (var flag in flags.Where(f => f.ReportIndex == source.ReportIndex))
            {
                Assert.Equal(source.ReportDigest, flag.ReportDigest);
                Assert.Equal(report.GetProperty(flag.ArrayName)[flag.Index].GetRawText(), flag.Payload.GetRawText());
            }
            foreach (var input in source.Inputs)
                Assert.Equal(report.GetProperty("Inputs")[input.InputIndex].GetRawText(), input.Payload.GetRawText());
        }
        var duplicate = queue.Buckets.Single(b => b.Market == "KOSPI" && b.Ticker == "005930")
            .SourceFlagReferences.Where(f => f.ReportIndex == 0 && f.ArrayName == "Issues").ToArray();
        Assert.Equal(2, duplicate.Length);
        Assert.Equal(duplicate[0].Payload.GetRawText(), duplicate[1].Payload.GetRawText());
        Assert.NotEqual(duplicate[0].Index, duplicate[1].Index);
    }

    [Fact]
    public void BucketsNeverResolveEpisodesAndSameTickerAcrossMarketsStaysSeparate()
    {
        var queue = KrxReviewQueue.Build([Json(Price())], [Json(Reference())], Now);
        Assert.Equal(5, queue.Buckets.Length);
        var kospi = queue.Buckets.Single(b => b.Market == "KOSPI" && b.Ticker == "005930");
        var kosdaq = queue.Buckets.Single(b => b.Market == "KOSDAQ" && b.Ticker == "005930");
        Assert.NotEqual(kospi.Key, kosdaq.Key);
        Assert.Equal("AMBIGUOUS", kospi.ListingEpisodeStatus);
        Assert.Equal("UNVERIFIED", kosdaq.ListingEpisodeStatus);
        Assert.Single(kospi.KnownIdentityChangeReferences);
        Assert.Empty(kosdaq.KnownIdentityChangeReferences);
        Assert.Null(kospi.Date);
        Assert.Equal(First, kospi.FirstDate); Assert.Equal(Second, kospi.LastDate);
        Assert.Equal("AMBIGUOUS", queue.Buckets.Single(b => b.Ticker == "000001").ListingEpisodeStatus);
        var marketDates = queue.Buckets.Where(b => b.Ticker == null).ToArray();
        Assert.Equal(new[] { Second, Third }, marketDates.Select(b => b.Date!.Value).Order().ToArray());
        Assert.All(queue.Buckets, b => Assert.Equal("OPEN", b.Status));
        Assert.Equal("OPEN", queue.Status);
        using var document = JsonDocument.Parse(Json(queue));
        foreach (var bucket in document.RootElement.GetProperty("Buckets").EnumerateArray())
        {
            Assert.False(bucket.TryGetProperty("StandardCode", out _));
            Assert.False(bucket.TryGetProperty("ListingEpisodeKey", out _));
            Assert.False(bucket.TryGetProperty("ReviewComplete", out _));
        }
    }

    [Fact]
    public void DailyNoTradeAggregatesAndBothBoundarySnapshotReferencesArePreserved()
    {
        var queue = KrxReviewQueue.Build([Json(Price())], [Json(Reference())], Now);
        Assert.Equal(8, queue.DaySummaries.Length);
        Assert.Equal(2, queue.DaySummaries.Where(d => d.ReportIndex == 0).Sum(d => d.Payload.GetProperty("NoTradeRows").GetInt32()));
        var bucket = queue.Buckets.Single(b => b.Market == "KOSPI" && b.Ticker == "005930");
        var inputs = bucket.InputReferences.Select(r => queue.Sources[r.ReportIndex].Inputs[r.InputIndex]).ToArray();
        Assert.Equal(8, inputs.Length); // Two daily inputs plus both dates' three reference services.
        Assert.Equal(new[] { First, Second }, inputs.Select(i => i.Date).Distinct().Order().ToArray());
        Assert.Contains(inputs, i => i.Service == "BASIC"); Assert.Contains(inputs, i => i.Service == "INDEX");
        Assert.All(inputs, i => Assert.Equal(Observed, i.ObservedAt));
        Assert.Empty(queue.Buckets.Single(b => b.Date == Third).InputReferences);
        Assert.DoesNotContain(queue.Buckets.SelectMany(b => b.SourceFlagReferences), f => f.Payload.TryGetProperty("Code", out var code) && code.GetString() == "NO_TRADE");
    }

    [Fact]
    public void WholeWrapperAndReportMetadataAreBoundWithoutReadingSnapshotFiles()
    {
        var wrapped = Json(new { CodeVersion = "fixture-a", Report = Price() });
        var queue = KrxReviewQueue.Build([wrapped], [Json(Reference())], Now);
        Assert.Equal(wrapped, queue.Sources[0].RawJson);
        Assert.Equal("Report", queue.Sources[0].ReportProperty);
        Assert.Equal(Digest(wrapped), queue.Sources[0].ReportDigest);
        var changed = KrxReviewQueue.Build([Json(new { CodeVersion = "fixture-b", Report = Price() })], [Json(Reference())], Now);
        Assert.NotEqual(queue.Hash, changed.Hash);
        Assert.NotEqual(queue.Buckets.Single(b => b.Ticker == "000001").Key, changed.Buckets.Single(b => b.Ticker == "000001").Key);
        var laterReport = KrxReviewQueue.Build([Json(Price() with { CreatedAt = Created.AddMinutes(1) })], [Json(Reference())], Now);
        var baseline = KrxReviewQueue.Build([Json(Price())], [Json(Reference())], Now);
        Assert.NotEqual(baseline.Hash, laterReport.Hash);
        Assert.All(queue.Sources.SelectMany(s => s.Inputs), i => Assert.Equal(Observed, i.ObservedAt));
        Assert.Contains("not signed", queue.Note, StringComparison.Ordinal);
        Assert.Contains("No underlying snapshot was read", queue.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void SameInputHasDeterministicKeysHashOrderingAndSerializableEvidencePointers()
    {
        var first = KrxReviewQueue.Build([Json(Price())], [Json(Reference())], Now);
        var again = KrxReviewQueue.Build([Json(Price())], [Json(Reference())], Now.AddHours(1));
        Assert.Equal(first.Hash, again.Hash); Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(first.Buckets.Select(b => b.Key).Order(StringComparer.Ordinal), first.Buckets.Select(b => b.Key));
        Assert.Equal(Json(first.Buckets), Json(again.Buckets));
        var restored = JsonSerializer.Deserialize<KrxReviewQueueReport>(Json(first))!;
        Assert.Equal(Json(first), Json(restored));
        Assert.Equal(9, restored.Buckets.Sum(b => b.SourceFlagReferences.Length));
    }

    [Fact]
    public void RepeatedQueueExecutionPublishesDistinctEvidenceFilesWithTheSameContentHash()
    {
        var directory = Path.Combine(Path.GetTempPath(), "krx-review-queue-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EvidenceStore(directory);
            var first = KrxReviewQueue.Build([Json(Price())], [Json(Reference())], Now);
            var firstPath = store.Save("krx-review-queue", first.Id, new { CodeVersion = "fixture", Report = first });
            var firstBytes = File.ReadAllBytes(firstPath);
            var second = KrxReviewQueue.Build([Json(Price())], [Json(Reference())], Now.AddMinutes(1));
            var secondPath = store.Save("krx-review-queue", second.Id, new { CodeVersion = "fixture", Report = second });
            Assert.NotEqual(firstPath, secondPath);
            Assert.Equal(2, Directory.GetFiles(directory).Length);
            Assert.Equal(firstBytes, File.ReadAllBytes(firstPath));
            using var firstFile = JsonDocument.Parse(firstBytes);
            using var secondFile = JsonDocument.Parse(File.ReadAllBytes(secondPath));
            Assert.Equal(first.Hash, firstFile.RootElement.GetProperty("Report").GetProperty("Hash").GetString());
            Assert.Equal(first.Hash, secondFile.RootElement.GetProperty("Report").GetProperty("Hash").GetString());
            Assert.Equal(Json(first.Buckets), Json(second.Buckets));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DuplicateReportIsRejectedAcrossFormattingWrappersOrEditedSameId()
    {
        var price = Price();
        var pretty = JsonSerializer.Serialize(price, new JsonSerializerOptions { WriteIndented = true });
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(price), Json(price)], [], Now));
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(price), pretty], [], Now));
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(price), Json(new { CodeVersion = "fixture", Report = price })], [], Now));
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(price), Json(price with { CreatedAt = Created.AddMinutes(1) })], [], Now));
    }

    [Theory]
    [InlineData("raw-hash")]
    [InlineData("observation")]
    public void ConflictingCommonDailySelectionIsRejected(string mutation)
    {
        var reference = Reference();
        var selected = reference.Inputs[0];
        reference.Inputs[0] = mutation == "raw-hash"
            ? selected with { RawHash = Digest("another revision") }
            : selected with { ObservedAt = selected.ObservedAt.AddTicks(1) };
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(Price())], [Json(reference)], Now));
    }

    [Fact]
    public void FutureReportsMissingArraysAndAmbiguousJsonPointersCannotProduceAQueue()
    {
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([], [], Now));
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(Price() with { CreatedAt = Now.AddTicks(1) })], [], Now));
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(Price() with { Issues = null! })], [], Now));
        var missing = JsonNode.Parse(Json(Price()))!;
        missing.AsObject().Remove("Changes");
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([missing.ToJsonString()], [], Now));
        var repeatedId = Json(Price()).Replace("\"Id\":\"price-report\"", "\"Id\":\"ignored\",\"Id\":\"price-report\"", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([repeatedId], [], Now));
    }

    [Fact]
    public void UnknownFindingCodesSurviveAndCleanReportsDoNotCreateCompletedReviews()
    {
        var report = Price() with { Issues = [new("NEW_UNKNOWN_CODE", "KOSPI", First, "005930", "must remain visible")], Changes = [] };
        var queue = KrxReviewQueue.Build([Json(report)], [], Now);
        Assert.Equal("NEW_UNKNOWN_CODE", Assert.Single(Assert.Single(queue.Buckets).SourceFlagReferences).Payload.GetProperty("Code").GetString());
        var clean = KrxReviewQueue.Build([Json(report with { Id = "clean", Issues = [], Status = "STRUCTURAL_CHECKS_PASSED_UNREVIEWED" })], [], Now);
        Assert.Empty(clean.Buckets); Assert.Equal("OPEN", clean.Status); Assert.Equal(4, clean.DaySummaries.Length);
    }

    [Fact]
    public void AlphanumericTickerKeepsTheSameContractAsKrxParsers()
    {
        var report = Price() with { Issues = [new("SYNTHETIC", "KOSPI", First, "0001A0", "must remain visible")], Changes = [] };
        var queue = KrxReviewQueue.Build([Json(report)], [], Now);
        Assert.Equal("0001A0", Assert.Single(queue.Buckets).Ticker);
    }

    [Fact]
    public void ReferenceEndpointMustMatchItsDeclaredMarketAndService()
    {
        var reference = Reference();
        reference.Inputs[0] = reference.Inputs[0] with { Source = "https://untrusted.invalid/looks-official" };
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(Price())], [Json(reference)], Now));
    }

    [Fact]
    public void FindingLimitRejectsWholeRequestRatherThanSilentlyTruncatingOriginalFlags()
    {
        var issue = new KrxAuditIssue("SYNTHETIC", "KOSPI", First, "005930", "fixture");
        var report = Price() with { Issues = Enumerable.Repeat(issue, KrxReviewQueue.MaximumFlagCount + 1).ToArray(), Changes = [] };
        Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([Json(report)], [], Now));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FindingBudgetIsCheckedBeforePayloadDeserializationIncludingEarlierReports(bool reference, bool earlierReport)
    {
        // Empty objects are intentionally malformed findings. A late check would report missing
        // fields only after allocating all typed findings; the required result is the count limit.
        var prior = Price("prior");
        var remaining = KrxReviewQueue.MaximumFlagCount - (earlierReport ? prior.Issues.Length + prior.Changes.Length : 0);
        var issueCount = remaining / 2;
        var malformedIssues = "[" + string.Join(",", Enumerable.Repeat("{}", issueCount)) + "]";
        var malformedChanges = "[" + string.Join(",", Enumerable.Repeat("{}", remaining - issueCount + 1)) + "]";
        var next = reference
            ? Json(Reference("next") with { Issues = [], IdentityChanges = [] })
            : Json(Price("next") with { Issues = [], Changes = [] });
        next = next.Replace("\"Issues\":[]", "\"Issues\":" + malformedIssues, StringComparison.Ordinal);
        var changeArray = reference ? "IdentityChanges" : "Changes";
        next = next.Replace("\"" + changeArray + "\":[]", "\"" + changeArray + "\":" + malformedChanges, StringComparison.Ordinal);
        string[] prices = earlierReport ? [Json(prior)] : [];
        var error = reference
            ? Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build(prices, [next], Now))
            : Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build([.. prices, next], [], Now));
        Assert.Contains("finding limit exceeded", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataBudgetIsCheckedBeforePayloadDeserializationIncludingEarlierReports(bool earlierReport)
    {
        var prior = Price("prior");
        var remaining = KrxReviewQueue.MaximumMetadataEntries - (earlierReport ? prior.Inputs.Length + prior.Days.Length : 0);
        var inputCount = remaining / 2;
        // Each array is below the limit, but their combined count exhausts the request budget.
        // Empty objects also ensure a late payload-validation error cannot satisfy this regression.
        var malformedInputs = "[" + string.Join(",", Enumerable.Repeat("{}", inputCount)) + "]";
        var malformedDays = "[" + string.Join(",", Enumerable.Repeat("{}", remaining - inputCount + 1)) + "]";
        var next = Json(Reference("next") with { Inputs = [], Days = [], Issues = [], IdentityChanges = [] })
            .Replace("\"Inputs\":[]", "\"Inputs\":" + malformedInputs, StringComparison.Ordinal)
            .Replace("\"Days\":[]", "\"Days\":" + malformedDays, StringComparison.Ordinal);
        string[] prices = earlierReport ? [Json(prior)] : [];
        var error = Assert.Throws<ArgumentException>(() => KrxReviewQueue.Build(prices, [next], Now));
        Assert.Contains("metadata-entry limit exceeded", error.Message, StringComparison.Ordinal);
    }
}
