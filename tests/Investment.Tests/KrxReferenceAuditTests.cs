using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KrxReferenceAuditTests
{
    private static readonly DateOnly First = new(2026, 9, 21);
    private static readonly DateOnly Second = First.AddDays(1);
    private static readonly DateOnly Third = First.AddDays(2);
    private static readonly DateTimeOffset Observed = Clock.Close(Third).AddHours(1);
    private static readonly DateTimeOffset Now = Observed.AddDays(1);
    private const string OriginalIsin = "KR7005930003";
    private const string ChangedIsin = "KR7000660001";

    public static TheoryData<string, string> InvalidEvidenceCases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var service in new[] { "DAILY", "BASIC", "INDEX" })
            foreach (var mutation in new[] { "raw-hash", "normalized", "source", "date", "future-observation", "early-observation" })
                cases.Add(service, mutation);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidEvidenceCases))]
    public void InvalidSnapshotEvidenceStopsAuditWithoutOverwritingInput(string service, string mutation)
    {
        using var workspace = new Workspace();
        var sources = workspace.Day(First);
        var target = sources.Single(s => s.Service == service);
        var document = JsonNode.Parse(File.ReadAllText(target.SnapshotFile))!;
        switch (mutation)
        {
            case "raw-hash": document["RawHash"] = new string('0', 64); break;
            case "normalized": document["Rows"]![0]![service == "BASIC" ? "ListedShares" : "Close"] = 9999; break;
            case "source": document["Source"] = "https://untrusted.invalid/official-looking-data"; break;
            case "date": document["Date"] = Second.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case "future-observation": document["ObservedAt"] = Now.AddTicks(1); break;
            case "early-observation": document["ObservedAt"] = Clock.Close(First).AddTicks(-1); break;
        }
        File.WriteAllText(target.SnapshotFile, document.ToJsonString());
        var before = File.ReadAllBytes(target.SnapshotFile);
        Assert.ThrowsAny<ArgumentException>(() => KrxReferenceAudit.Run(Plan(sources, [First]), Now));
        Assert.Equal(before, File.ReadAllBytes(target.SnapshotFile));
    }

    [Fact]
    public void AmbiguousLogicalSourceRevisionsAreRejectedBeforeReadingAnyFile()
    {
        using var workspace = new Workspace();
        var source = new KrxReferenceAuditSource("BASIC", "KOSPI", First, Path.Combine(workspace.Root, "missing-a.json"));
        var second = source with { SnapshotFile = Path.Combine(workspace.Root, "missing-b.json") };
        Assert.ThrowsAny<ArgumentException>(() => KrxReferenceAudit.Run(Plan([source, second], [First]), Now));
        Assert.False(Directory.Exists(workspace.Root));
    }

    [Fact]
    public void SuccessfulStructuralAuditStillDoesNotCertifyHistoricalAvailabilityOrResearchPerformance()
    {
        using var workspace = new Workspace();
        var report = KrxReferenceAudit.Run(Plan(workspace.Day(First), [First]), Now);
        Assert.Equal("STRUCTURAL_CHECKS_PASSED_UNREVIEWED", report.Status);
        Assert.Empty(report.Issues); Assert.Empty(report.IdentityChanges);
        Assert.Equal(3, report.Inputs.Length);
        var day = Assert.Single(report.Days);
        Assert.Equal(1, day.PriceRows); Assert.Equal(1, day.BasicRows); Assert.Equal(1, day.IndexRows);
        Assert.All(report.Inputs, input => Assert.Equal(Observed, input.ObservedAt));
        Assert.False(string.IsNullOrWhiteSpace(report.Note));
        Assert.Contains("publication", report.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PointInTimeReviewed", JsonSerializer.Serialize(report));
        Assert.DoesNotContain("PaperApproved", JsonSerializer.Serialize(report));
    }

    [Theory]
    [InlineData("DAILY", false)]
    [InlineData("BASIC", false)]
    [InlineData("INDEX", false)]
    [InlineData("DAILY", true)]
    [InlineData("BASIC", true)]
    [InlineData("INDEX", true)]
    public void MissingOrEmptyServiceKeepsUnknownCountsAndBreaksIdentityComparison(string service, bool empty)
    {
        using var workspace = new Workspace();
        var sources = workspace.Day(First).ToList();
        sources.AddRange(workspace.Day(Second).Where(s => s.Service != service));
        if (empty) sources.Add(workspace.Write(service, Second, empty: true));
        sources.AddRange(workspace.Day(Third, isin: ChangedIsin));
        var report = KrxReferenceAudit.Run(Plan(sources.ToArray(), [First, Second, Third]), Now);
        Assert.Equal("REVIEW_REQUIRED", report.Status); Assert.Empty(report.IdentityChanges);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(empty ? "EMPTY_RESPONSE" : "MISSING_SNAPSHOT", issue.Code);
        Assert.Equal(service, issue.Service); Assert.Equal(Second, issue.Date);
        var gap = Assert.Single(report.Days, d => d.Date == Second);
        int? count = service switch { "DAILY" => gap.PriceRows, "BASIC" => gap.BasicRows, _ => gap.IndexRows };
        Assert.Equal(empty ? 0 : (int?)null, count);
        Assert.Equal(3, report.Days.Length);
    }

    [Fact]
    public void TickerAndShareCountDisagreementsRemainExplicitReviewIssues()
    {
        using var workspace = new Workspace();
        var daily = workspace.Save("DAILY", First, PriceSnapshot(First, [Price(First), Price(First, "000001")]));
        var basic = workspace.Save("BASIC", First, BasicSnapshot(First,
            [Basic(shares: 2000), Basic("000002", ChangedIsin)]));
        var index = workspace.Write("INDEX", First);
        var report = KrxReferenceAudit.Run(Plan([daily, basic, index], [First]), Now);
        Assert.Equal("REVIEW_REQUIRED", report.Status);
        Assert.Contains(report.Issues, i => i.Ticker == "000001" && i.Code == "PRICE_WITHOUT_BASIC");
        Assert.Contains(report.Issues, i => i.Ticker == "000002" && i.Code == "BASIC_WITHOUT_PRICE");
        Assert.Contains(report.Issues, i => i.Ticker == "005930" && i.Code == "SHARE_COUNT_DIFFERS");
        Assert.Equal(3, report.Issues.Length);
        Assert.Empty(report.IdentityChanges);
    }

    [Theory]
    [InlineData("DAILY", false)]
    [InlineData("BASIC", false)]
    [InlineData("DAILY", true)]
    [InlineData("BASIC", true)]
    public void MissingOrZeroShareCountIsNeverTreatedAsMatchingValidShares(string service, bool zero)
    {
        using var workspace = new Workspace();
        var sources = workspace.Day(First).Where(s => s.Service != service).ToList();
        long? invalid = zero ? 0 : null;
        sources.Add(service == "DAILY"
            ? workspace.Save(service, First, PriceSnapshot(First, [Price(First, shares: invalid)]))
            : workspace.Save(service, First, BasicSnapshot(First, [Basic(shares: invalid)])));
        var report = KrxReferenceAudit.Run(Plan(sources.ToArray(), [First]), Now);
        Assert.Equal("REVIEW_REQUIRED", report.Status);
        Assert.Contains(report.Issues, i => i.Code == "MISSING_SHARE_COUNT" && i.Ticker == "005930");
        Assert.DoesNotContain(report.Issues, i => i.Code == "SHARE_COUNT_DIFFERS");
    }

    [Fact]
    public void AnIdentityChangeIsReportedOnceAtItsFirstAdjacentBoundary()
    {
        using var workspace = new Workspace();
        var sources = workspace.Day(First).Concat(workspace.Day(Second, isin: ChangedIsin))
            .Concat(workspace.Day(Third, isin: ChangedIsin)).ToArray();
        var report = KrxReferenceAudit.Run(Plan(sources, [First, Second, Third]), Now);
        var change = Assert.Single(report.IdentityChanges);
        Assert.Equal("KOSPI", change.Market); Assert.Equal("005930", change.Ticker);
        Assert.Equal(First, change.PreviousDate); Assert.Equal(Second, change.Date);
        Assert.Equal(OriginalIsin, change.PreviousStandardCode); Assert.Equal(ChangedIsin, change.StandardCode);
        Assert.Equal("REVIEW_REQUIRED", report.Status);
    }

    [Fact]
    public void ReusedStandardCodeAcrossDifferentTickersIsFlaggedWithoutInventingAnIdentity()
    {
        using var workspace = new Workspace();
        var daily = workspace.Save("DAILY", First, PriceSnapshot(First, [Price(First), Price(First, "000001")]));
        var basic = workspace.Save("BASIC", First, BasicSnapshot(First, [Basic(), Basic("000001")]));
        var report = KrxReferenceAudit.Run(Plan([daily, basic, workspace.Write("INDEX", First)], [First]), Now);
        Assert.Equal("REVIEW_REQUIRED", report.Status);
        Assert.Contains(report.Issues, i => i.Code == "DUPLICATE_STANDARD_CODE" && i.Service == "BASIC");
        Assert.Empty(report.IdentityChanges);
    }

    [Fact]
    public void AnExplicitIndexNameCannotBeReplacedByTheFirstAvailableIndex()
    {
        using var workspace = new Workspace();
        var plan = Plan(workspace.Day(First), [First]) with
        {
            RequiredIndices = [new KrxRequiredIndex("KOSPI", "KOSPI"), new("KOSPI", "KOSPI 200")]
        };
        var report = KrxReferenceAudit.Run(plan, Now);
        Assert.Equal("REVIEW_REQUIRED", report.Status);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("REQUIRED_INDEX_MISSING", issue.Code);
        Assert.Equal("INDEX", issue.Service); Assert.Contains("KOSPI 200", issue.Detail);
        Assert.Equal(1, Assert.Single(report.Days).IndexRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredIndexWithMissingOrZeroCloseIsNotUsableMarketEvidence(bool zero)
    {
        using var workspace = new Workspace();
        var sources = workspace.Day(First).Where(s => s.Service != "INDEX").ToList();
        sources.Add(workspace.Save("INDEX", First, IndexSnapshot(First, [Index(First) with { Close = zero ? 0 : null }])));
        var report = KrxReferenceAudit.Run(Plan(sources.ToArray(), [First]), Now);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("REQUIRED_INDEX_INVALID_CLOSE", issue.Code); Assert.Equal("INDEX", issue.Service);
        Assert.Equal("REVIEW_REQUIRED", report.Status);
    }

    [Theory]
    [InlineData("future-listing")]
    [InlineData("basic-market")]
    [InlineData("index-market")]
    public void RawConsistentReferenceRowsStillMustMatchHistoricalDateAndMarket(string mutation)
    {
        using var workspace = new Workspace();
        var service = mutation == "index-market" ? "INDEX" : "BASIC";
        var sources = workspace.Day(First).Where(s => s.Service != service).ToList();
        sources.Add(mutation switch
        {
            "future-listing" => workspace.Save("BASIC", First, BasicSnapshot(First, [Basic() with { ListingDate = Stamp(Second) }])),
            "basic-market" => workspace.Save("BASIC", First, BasicSnapshot(First, [Basic() with { MarketName = "KOSDAQ" }])),
            _ => workspace.Save("INDEX", First, IndexSnapshot(First, [Index(First) with { IndexClass = "KOSDAQ" }]))
        });
        Assert.ThrowsAny<ArgumentException>(() => KrxReferenceAudit.Run(Plan(sources.ToArray(), [First]), Now));
    }

    [Fact]
    public void InputHashIgnoresSourceOrderAndLocalRelocationButBindsSourceContentAndObservationTime()
    {
        using var workspace = new Workspace(); using var relocated = new Workspace();
        var sources = workspace.Day(First).Concat(workspace.Day(Second)).ToArray();
        var plan = Plan(sources, [First, Second]);
        var original = KrxReferenceAudit.Run(plan, Now);
        Directory.CreateDirectory(relocated.Root);
        var copied = sources.Reverse().Select((source, i) =>
        {
            var path = Path.Combine(relocated.Root, "relocated-" + i.ToString(CultureInfo.InvariantCulture) + ".json");
            File.Copy(source.SnapshotFile, path);
            return source with { SnapshotFile = path };
        }).ToArray();
        var copy = KrxReferenceAudit.Run(plan with { Sources = copied }, Now.AddHours(1));
        Assert.Equal(original.InputHash, copy.InputHash);
        Assert.Equal(JsonSerializer.Serialize(original.Days), JsonSerializer.Serialize(copy.Days));
        Assert.Equal(JsonSerializer.Serialize(original.Issues), JsonSerializer.Serialize(copy.Issues));
        Assert.Equal(JsonSerializer.Serialize(original.IdentityChanges), JsonSerializer.Serialize(copy.IdentityChanges));

        var changed = copied.Single(s => s.Service == "DAILY" && s.Date == Second);
        File.WriteAllText(changed.SnapshotFile, JsonSerializer.Serialize(PriceSnapshot(Second, [Price(Second)]) with { Id = "different-local-capture-label" }));
        Assert.Equal(original.InputHash, KrxReferenceAudit.Run(plan with { Sources = copied }, Now).InputHash);
        File.WriteAllText(changed.SnapshotFile, JsonSerializer.Serialize(PriceSnapshot(Second, [Price(Second, close: 101)])));
        Assert.NotEqual(original.InputHash, KrxReferenceAudit.Run(plan with { Sources = copied }, Now).InputHash);
        File.WriteAllText(changed.SnapshotFile, JsonSerializer.Serialize(PriceSnapshot(Second, [Price(Second)]) with { ObservedAt = Observed.AddTicks(1) }));
        Assert.NotEqual(original.InputHash, KrxReferenceAudit.Run(plan with { Sources = copied }, Now).InputHash);
        Assert.Equal(original.InputHash, KrxReferenceAudit.Run(plan, Now).InputHash);
    }

    [Fact]
    public void InputHashAlsoBindsRequestedCoverageAndExplicitIndexSelection()
    {
        using var workspace = new Workspace();
        var plan = Plan(workspace.Day(First), [First]); var baseline = KrxReferenceAudit.Run(plan, Now);
        var wider = KrxReferenceAudit.Run(plan with { ExpectedDates = [First, Second] }, Now);
        var otherIndex = KrxReferenceAudit.Run(plan with { RequiredIndices = [new("KOSPI", "KOSPI 200")] }, Now);
        Assert.NotEqual(baseline.InputHash, wider.InputHash); Assert.NotEqual(baseline.InputHash, otherIndex.InputHash);
    }

    [Fact]
    public void InputHashCanonicalizesRequiredIndexOrdering()
    {
        using var workspace = new Workspace();
        var sources = workspace.Day(First).Where(s => s.Service != "INDEX").ToList();
        sources.Add(workspace.Save("INDEX", First, IndexSnapshot(First, [Index(First), Index(First) with { IndexName = "KOSPI 200" }])));
        var plan = Plan(sources.ToArray(), [First]) with { RequiredIndices = [new("KOSPI", "KOSPI"), new("KOSPI", "KOSPI 200")] };
        var forward = KrxReferenceAudit.Run(plan, Now);
        var reversed = KrxReferenceAudit.Run(plan with { RequiredIndices = plan.RequiredIndices.Reverse().ToArray() }, Now);
        Assert.Empty(forward.Issues); Assert.Equal(forward.InputHash, reversed.InputHash);
    }

    [Fact]
    public void CancellationIsObservedBeforeOpeningSnapshotFiles()
    {
        using var workspace = new Workspace(); using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new KrxReferenceAuditSource("DAILY", "KOSPI", First, Path.Combine(workspace.Root, "not-created.json"));
        Assert.ThrowsAny<OperationCanceledException>(() => KrxReferenceAudit.Run(Plan([source], [First]), Now, cancellation.Token));
        Assert.False(Directory.Exists(workspace.Root));
    }

    [Fact]
    public void ExcessiveNormalizedRowsAreRejectedByTheResourceLimitBeforeTypedMaterialization()
    {
        using var workspace = new Workspace();
        var sources = workspace.Day(First); var target = sources.Single(source => source.Service == "BASIC");
        var document = JsonNode.Parse(File.ReadAllText(target.SnapshotFile))!.AsObject();
        document.Remove("Rows"); var header = document.ToJsonString();
        // A small encoded file can expand into many larger record objects. Its valid raw body
        // intentionally has one row: the resource gate must run before normalized-data validation.
        var encodedRows = string.Join(",", Enumerable.Repeat("{}", KrxReferenceAudit.MaximumNormalizedRows + 1));
        File.WriteAllText(target.SnapshotFile, header[..^1] + ",\"Rows\":[" + encodedRows + "]}");
        Assert.True(new FileInfo(target.SnapshotFile).Length < KrxReferenceAudit.MaximumSnapshotBytes);
        var exception = Assert.ThrowsAny<ArgumentException>(() => KrxReferenceAudit.Run(Plan(sources, [First]), Now));
        Assert.Contains("normalized row limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Rows")]
    [InlineData("R\\u006fws")]
    public void RepeatedNormalizedRowsPropertiesAreAmbiguousEvenWhenTheirValuesMatch(string secondProperty)
    {
        using var workspace = new Workspace();
        var sources = workspace.Day(First); var target = sources.Single(source => source.Service == "BASIC");
        var original = File.ReadAllText(target.SnapshotFile);
        using var document = JsonDocument.Parse(original);
        var repeated = original[..^1] + ",\"" + secondProperty + "\":" + document.RootElement.GetProperty("Rows").GetRawText() + "}";
        File.WriteAllText(target.SnapshotFile, repeated);
        Assert.ThrowsAny<ArgumentException>(() => KrxReferenceAudit.Run(Plan(sources, [First]), Now));
        Assert.Equal(repeated, File.ReadAllText(target.SnapshotFile));
    }

    private static KrxReferenceAuditPlan Plan(KrxReferenceAuditSource[] sources, DateOnly[] dates) =>
        new(sources, dates, ["KOSPI"], [new("KOSPI", "KOSPI")]);

    private static KrxRow Price(DateOnly date, string ticker = "005930", decimal close = 100, long? shares = 1000) =>
        new(date, ticker, "synthetic company", "KOSPI", "section", close, close + 1, close - 1, close, 100, close * 100, close * shares, shares);
    private static KrxBasicRow Basic(string ticker = "005930", string isin = OriginalIsin, long? shares = 1000) =>
        new(isin, ticker, "synthetic company", "20200102", "KOSPI", "주권", "section", "보통주", shares);
    private static KrxIndexRow Index(DateOnly date) => new(date, "KOSPI", "KOSPI", 100, 101, 99, 100, 1000, 100000);
    private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
    private static string Stamp(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static KrxSnapshot PriceSnapshot(DateOnly date, KrxRow[] rows)
    {
        var raw = JsonSerializer.Serialize(new { OutBlock_1 = rows.Select(r => new
        {
            BAS_DD = Stamp(r.Date), ISU_CD = r.Ticker, ISU_NM = r.Company, MKT_NM = r.Market, SECT_TP_NM = r.ListingSection,
            TDD_OPNPRC = Number(r.Open), TDD_HGPRC = Number(r.High), TDD_LWPRC = Number(r.Low), TDD_CLSPRC = Number(r.Close),
            ACC_TRDVOL = Number(r.Volume), ACC_TRDVAL = Number(r.TradingValue), MKTCAP = Number(r.MarketCap), LIST_SHRS = Number(r.SharesOutstanding)
        }).ToArray() });
        return new("synthetic-price", "KOSPI", date, Observed, "https://data-dbg.krx.co.kr/svc/apis/sto/stk_bydd_trd",
            Hash(raw), raw, KrxClient.Parse(raw, date, "KOSPI"));
    }

    private static KrxBasicSnapshot BasicSnapshot(DateOnly date, KrxBasicRow[] rows)
    {
        var raw = JsonSerializer.Serialize(new { OutBlock_1 = rows.Select(r => new
        {
            ISU_CD = r.StandardCode, ISU_SRT_CD = r.Ticker, ISU_NM = r.Name, LIST_DD = r.ListingDate, MKT_TP_NM = r.MarketName,
            SECUGRP_NM = r.SecurityGroup, SECT_TP_NM = r.ListingSection, KIND_STKCERT_TP_NM = r.StockType, LIST_SHRS = Number(r.ListedShares)
        }).ToArray() });
        return new("synthetic-basic", "KOSPI", date, Observed, "https://data-dbg.krx.co.kr/svc/apis/sto/stk_isu_base_info",
            Hash(raw), raw, KrxReferenceClient.ParseBasicInfo(raw));
    }

    private static KrxIndexSnapshot IndexSnapshot(DateOnly date, KrxIndexRow[] rows)
    {
        var raw = JsonSerializer.Serialize(new { OutBlock_1 = rows.Select(r => new
        {
            BAS_DD = Stamp(r.Date), IDX_CLSS = r.IndexClass, IDX_NM = r.IndexName, OPNPRC_IDX = Number(r.Open),
            HGPRC_IDX = Number(r.High), LWPRC_IDX = Number(r.Low), CLSPRC_IDX = Number(r.Close),
            ACC_TRDVOL = Number(r.Volume), ACC_TRDVAL = Number(r.TradingValue)
        }).ToArray() });
        return new("synthetic-index", "KOSPI", date, Observed, "https://data-dbg.krx.co.kr/svc/apis/idx/kospi_dd_trd",
            Hash(raw), raw, KrxReferenceClient.ParseIndex(raw, date));
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "krx-reference-audit-" + Guid.NewGuid().ToString("N"));
        public KrxReferenceAuditSource[] Day(DateOnly date, string isin = OriginalIsin) =>
            [Write("DAILY", date), Write("BASIC", date, isin: isin), Write("INDEX", date)];
        public KrxReferenceAuditSource Write(string service, DateOnly date, bool empty = false, string isin = OriginalIsin) => service switch
        {
            "DAILY" => Save(service, date, PriceSnapshot(date, empty ? [] : [Price(date)])),
            "BASIC" => Save(service, date, BasicSnapshot(date, empty ? [] : [Basic(isin: isin)])),
            "INDEX" => Save(service, date, IndexSnapshot(date, empty ? [] : [Index(date)])),
            _ => throw new ArgumentException("Unknown fixture service.")
        };
        public KrxReferenceAuditSource Save<T>(string service, DateOnly date, T snapshot)
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, service + "-" + Stamp(date) + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
            return new(service, "KOSPI", date, path);
        }
        public void Dispose()
        {
            var root = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("krx-reference-audit-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected reference audit fixture cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
