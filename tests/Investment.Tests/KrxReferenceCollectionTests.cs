using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KrxReferenceCollectionTests
{
    private static readonly DateOnly First = new(2026, 9, 24);
    private static readonly DateOnly Second = First.AddDays(1);
    private static readonly DateTimeOffset Observed = new(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(9));
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    [Fact] public async Task ExplicitDailyReusesAnExistingLegacyPlanHashAndCacheWithoutRewritingEither()
    {
        using var workspace = new Workspace();
        const string legacyPlan = "{\"Market\":\"KOSPI\",\"Dates\":[\"2026-09-24\"]}";
        var hash = Hash(legacyPlan); var directory = Path.Combine(workspace.Root, hash);
        Directory.CreateDirectory(directory);
        var planPath = Path.Combine(directory, "plan-" + hash + ".json");
        var snapshotPath = Path.Combine(directory, "krx-raw-20260924.json");
        File.WriteAllText(planPath, legacyPlan);
        File.WriteAllText(snapshotPath, JsonSerializer.Serialize(Daily(First)));
        var planBytes = File.ReadAllBytes(planPath); var snapshotBytes = File.ReadAllBytes(snapshotPath);
        var calls = 0; var plan = new KrxCollectionPlan("KOSPI", [First], "DAILY");
        Task<KrxSnapshot> Unexpected(string market, DateOnly date, CancellationToken ct)
        { calls++; throw new InvalidOperationException("Legacy cache must be reused."); }

        var receipt = await new KrxCollector(workspace.Root, () => Observed).Run(plan, 1, Interval, Unexpected);
        Assert.Equal(hash, receipt.PlanHash); Assert.Equal("COLLECTED_UNREVIEWED", receipt.Status);
        Assert.Equal(0, receipt.Attempts); Assert.Equal(0, calls);
        Assert.Equal(snapshotPath, Assert.Single(receipt.SnapshotFiles));
        Assert.Equal(planBytes, File.ReadAllBytes(planPath)); Assert.Equal(snapshotBytes, File.ReadAllBytes(snapshotPath));
        Assert.Equal("DAILY", plan.Service); // Canonicalization must not mutate the caller's plan.
        Assert.DoesNotContain("Service", File.ReadAllText(planPath));
    }

    [Fact] public async Task DailyBasicAndIndexUseSeparatePlanNamespacesAndRawPrefixes()
    {
        using var workspace = new Workspace();
        var collector = new KrxCollector(workspace.Root, () => Observed);
        var daily = await collector.Run(new("KOSPI", [First]), 1, Interval,
            (_, date, _) => Task.FromResult(Daily(date)));
        var basic = await Collect(collector, "BASIC", [First], 1);
        var index = await Collect(collector, "INDEX", [First], 1);
        Assert.Equal(3, new[] { daily.PlanHash, basic.PlanHash, index.PlanHash }.Distinct().Count());
        Assert.Equal("krx-raw-20260924.json", Path.GetFileName(Assert.Single(daily.SnapshotFiles)));
        Assert.Equal("krx-basic-raw-20260924.json", Path.GetFileName(Assert.Single(basic.SnapshotFiles)));
        Assert.Equal("krx-index-raw-20260924.json", Path.GetFileName(Assert.Single(index.SnapshotFiles)));
        foreach (var (receipt, service) in new[] { (basic, "BASIC"), (index, "INDEX") })
        {
            var saved = JsonSerializer.Deserialize<KrxCollectionPlan>(File.ReadAllText(Path.Combine(
                workspace.Root, receipt.PlanHash, "plan-" + receipt.PlanHash + ".json")))!;
            Assert.Equal(service, saved.Service);
            Assert.Equal("COLLECTED_UNREVIEWED", receipt.Status);
        }
    }

    [Theory]
    [InlineData("BASIC")]
    [InlineData("INDEX")]
    public async Task ReferenceBudgetResumePreservesRawPayloadAndNeverFetchesCompletedDatesAgain(string service)
    {
        using var workspace = new Workspace(); var calls = new List<DateOnly>();
        var collector = new KrxCollector(workspace.Root, () => Observed);
        var first = await Collect(collector, service, [First, Second], 1, calls.Add);
        Assert.Equal("REQUEST_BUDGET_REACHED", first.Status); Assert.Equal(new[] { Second }, first.PendingDates);
        var path = Assert.Single(first.SnapshotFiles); var original = File.ReadAllBytes(path);
        using (var snapshot = JsonDocument.Parse(original))
        {
            Assert.Equal(service == "BASIC" ? Basic(First).RawJson : Index(First).RawJson,
                snapshot.RootElement.GetProperty("RawJson").GetString());
            Assert.Equal(Observed, snapshot.RootElement.GetProperty("ObservedAt").GetDateTimeOffset());
        }
        var resumed = await Collect(collector, service, [First, Second], 1, calls.Add);
        Assert.Equal("COLLECTED_UNREVIEWED", resumed.Status); Assert.Empty(resumed.PendingDates);
        Assert.Equal(new[] { First, Second }, calls); Assert.Equal(original, File.ReadAllBytes(path));
        var cached = await Collect(collector, service, [First, Second], 1, calls.Add);
        Assert.Equal(0, cached.Attempts); Assert.Equal(2, calls.Count); Assert.Equal(resumed.PlanHash, cached.PlanHash);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact] public async Task UnknownServicesAndTypedWrapperMismatchesFailBeforeFilesOrRequests()
    {
        using var workspace = new Workspace(); var calls = 0;
        var collector = new KrxCollector(workspace.Root, () => Observed);
        Task<KrxSnapshot> DailyFetch(string market, DateOnly date, CancellationToken ct)
        { calls++; return Task.FromResult(Daily(date)); }
        Task<KrxBasicSnapshot> BasicFetch(string market, DateOnly date, CancellationToken ct)
        { calls++; return Task.FromResult(Basic(date)); }
        Task<KrxIndexSnapshot> IndexFetch(string market, DateOnly date, CancellationToken ct)
        { calls++; return Task.FromResult(Index(date)); }
        foreach (var service in new[] { "", "basic", "INDEX ", "UNKNOWN", "BASIC", "INDEX" })
            await Assert.ThrowsAsync<ArgumentException>(() => collector.Run(new("KOSPI", [First], service), 1, Interval, DailyFetch));
        foreach (var service in new string?[] { null, "DAILY", "INDEX", "unknown" })
            await Assert.ThrowsAsync<ArgumentException>(() => collector.RunBasic(new("KOSPI", [First], service), 1, Interval, BasicFetch));
        foreach (var service in new string?[] { null, "DAILY", "BASIC", "unknown" })
            await Assert.ThrowsAsync<ArgumentException>(() => collector.RunIndex(new("KOSPI", [First], service), 1, Interval, IndexFetch));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(workspace.Root));
    }

    [Theory]
    [InlineData("BASIC", "hash")]
    [InlineData("BASIC", "normalized")]
    [InlineData("BASIC", "source")]
    [InlineData("INDEX", "hash")]
    [InlineData("INDEX", "normalized")]
    [InlineData("INDEX", "source")]
    public async Task TamperedReferenceCacheStopsBeforeAnyRefetchOrOverwrite(string service, string mutation)
    {
        using var workspace = new Workspace(); var calls = 0;
        var collector = new KrxCollector(workspace.Root, () => Observed);
        var first = await Collect(collector, service, [First, Second], 1, _ => calls++);
        var path = Assert.Single(first.SnapshotFiles); var document = JsonNode.Parse(File.ReadAllText(path))!;
        if (mutation == "hash") document["RawHash"] = new string('0', 64);
        else if (mutation == "source") document["Source"] = "https://data-dbg.krx.co.kr/svc/apis/sto/stk_bydd_trd";
        else document["Rows"]![0]![service == "BASIC" ? "ListedShares" : "Close"] = 9999;
        File.WriteAllText(path, document.ToJsonString()); var tampered = File.ReadAllBytes(path);
        await Assert.ThrowsAnyAsync<Exception>(() => Collect(collector, service, [First, Second], 5, _ => calls++));
        Assert.Equal(1, calls); Assert.Equal(tampered, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("market")]
    [InlineData("date")]
    [InlineData("future-observation")]
    [InlineData("early-observation")]
    [InlineData("future-listing")]
    [InlineData("invalid-listing")]
    [InlineData("row-market")]
    public async Task BasicProvenanceAndHistoricalListingErrorsNeverBecomeSuccessfulCache(string mutation)
    {
        using var workspace = new Workspace(); var calls = 0;
        var candidate = mutation switch
        {
            "source" => Basic(First) with { Source = "https://data-dbg.krx.co.kr/svc/apis/sto/ksq_isu_base_info" },
            "market" => Basic(First) with { Market = "KOSDAQ" },
            "date" => Basic(First) with { Date = Second },
            "future-observation" => Basic(First) with { ObservedAt = Observed.AddTicks(1) },
            "early-observation" => Basic(First) with { ObservedAt = Clock.Close(First).AddTicks(-1) },
            "future-listing" => Basic(First, listingDate: Stamp(Second)),
            "invalid-listing" => Basic(First, listingDate: "20261301"),
            _ => Basic(First, rowMarket: "KOSDAQ")
        };
        var receipt = await new KrxCollector(workspace.Root, () => Observed).RunBasic(new("KOSPI", [First], "BASIC"), 5,
            Interval, (_, _, _) => { calls++; return Task.FromResult(candidate); });
        Assert.Equal("REQUEST_FAILED", receipt.Status); Assert.Equal(1, calls);
        Assert.Empty(receipt.SnapshotFiles); Assert.Equal(new[] { First }, receipt.PendingDates);
        Assert.Empty(Directory.GetFiles(workspace.Root, "krx-basic-raw-*.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("market")]
    [InlineData("date")]
    [InlineData("future-observation")]
    [InlineData("early-observation")]
    [InlineData("index-class")]
    public async Task IndexProvenanceAndMarketClassErrorsNeverBecomeSuccessfulCache(string mutation)
    {
        using var workspace = new Workspace(); var calls = 0;
        var candidate = mutation switch
        {
            "source" => Index(First) with { Source = "https://data-dbg.krx.co.kr/svc/apis/idx/kosdaq_dd_trd" },
            "market" => Index(First) with { Market = "KOSDAQ" },
            "date" => Index(First) with { Date = Second },
            "future-observation" => Index(First) with { ObservedAt = Observed.AddTicks(1) },
            "early-observation" => Index(First) with { ObservedAt = Clock.Close(First).AddTicks(-1) },
            _ => Index(First, indexClass: "KOSDAQ")
        };
        var receipt = await new KrxCollector(workspace.Root, () => Observed).RunIndex(new("KOSPI", [First], "INDEX"), 5,
            Interval, (_, _, _) => { calls++; return Task.FromResult(candidate); });
        Assert.Equal("REQUEST_FAILED", receipt.Status); Assert.Equal(1, calls);
        Assert.Empty(receipt.SnapshotFiles); Assert.Equal(new[] { First }, receipt.PendingDates);
        Assert.Empty(Directory.GetFiles(workspace.Root, "krx-index-raw-*.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("BASIC")]
    [InlineData("INDEX")]
    public async Task EmptyReferenceResponseIsPreservedAndBlocksLaterDatesIncludingOnResume(string service)
    {
        using var workspace = new Workspace(); var calls = 0;
        var collector = new KrxCollector(workspace.Root, () => Observed);
        var result = await Collect(collector, service, [First, Second], 5, _ => calls++, empty: true);
        Assert.Equal("EMPTY_RESPONSE_REQUIRES_REVIEW", result.Status);
        Assert.Equal(new[] { First, Second }, result.PendingDates);
        var path = Assert.Single(result.SnapshotFiles); var original = File.ReadAllBytes(path);
        using (var snapshot = JsonDocument.Parse(original)) Assert.Equal(0, snapshot.RootElement.GetProperty("Rows").GetArrayLength());
        var resumed = await Collect(collector, service, [First, Second], 5, _ => calls++);
        Assert.Equal(result.Status, resumed.Status); Assert.Equal(0, resumed.Attempts); Assert.Equal(1, calls);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("BASIC")]
    [InlineData("INDEX")]
    public async Task ReferenceFailurePreservesPriorSuccessBeforeNextCallAndNeverPersistsCredentialText(string service)
    {
        using var workspace = new Workspace(); var calls = new List<DateOnly>();
        var collector = new KrxCollector(workspace.Root, () => Observed);
        const string secret = "AUTH_KEY=synthetic-private-provider-error";
        void Request(DateOnly date)
        {
            calls.Add(date);
            if (date != Second) return;
            Assert.Single(Directory.GetFiles(workspace.Root, Prefix(service) + "-*.json", SearchOption.AllDirectories));
            throw new HttpRequestException(secret);
        }
        var result = await Collect(collector, service, [First, Second], 5, Request);
        Assert.Equal("REQUEST_FAILED", result.Status); Assert.Equal("HttpRequestException", result.ErrorKind);
        Assert.Equal(new[] { First, Second }, calls); Assert.Equal(2, result.Attempts);
        Assert.Equal(Second, result.StoppedDate); Assert.Equal(new[] { Second }, result.PendingDates);
        var path = Assert.Single(result.SnapshotFiles); var original = File.ReadAllBytes(path);
        Assert.All(Directory.GetFiles(workspace.Root, "*.json", SearchOption.AllDirectories),
            file => Assert.DoesNotContain(secret, File.ReadAllText(file)));
        var resumed = await Collect(collector, service, [First, Second], 1, calls.Add);
        Assert.Equal("COLLECTED_UNREVIEWED", resumed.Status); Assert.Equal(new[] { First, Second, Second }, calls);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("BASIC")]
    [InlineData("INDEX")]
    public async Task ReferenceParserOverflowLeavesFailureReceiptWithoutSnapshotOrNextRequest(string service)
    {
        using var workspace = new Workspace(); var calls = 0;
        var collector = new KrxCollector(workspace.Root, () => Observed);
        var plan = new KrxCollectionPlan("KOSPI", [First, Second], service);
        const string overflow = "79228162514264337593543950336";
        Task<KrxBasicSnapshot> FetchBasic(string market, DateOnly date, CancellationToken ct)
        {
            calls++;
            var snapshot = Basic(date);
            var raw = JsonNode.Parse(snapshot.RawJson)!;
            raw["OutBlock_1"]![0]!["LIST_SHRS"] = overflow;
            _ = KrxReferenceClient.ParseBasicInfo(raw.ToJsonString());
            return Task.FromResult(snapshot);
        }
        Task<KrxIndexSnapshot> FetchIndex(string market, DateOnly date, CancellationToken ct)
        {
            calls++;
            var snapshot = Index(date);
            var raw = JsonNode.Parse(snapshot.RawJson)!;
            raw["OutBlock_1"]![0]!["CLSPRC_IDX"] = overflow;
            _ = KrxReferenceClient.ParseIndex(raw.ToJsonString(), date);
            return Task.FromResult(snapshot);
        }
        var result = service == "BASIC"
            ? await collector.RunBasic(plan, 5, Interval, FetchBasic)
            : await collector.RunIndex(plan, 5, Interval, FetchIndex);
        Assert.Equal("REQUEST_FAILED", result.Status); Assert.Equal("OverflowException", result.ErrorKind);
        Assert.Equal(1, calls); Assert.Equal(1, result.Attempts); Assert.Equal(First, result.StoppedDate);
        Assert.Equal(new[] { First, Second }, result.PendingDates); Assert.Empty(result.SnapshotFiles);
        Assert.Empty(Directory.GetFiles(workspace.Root, Prefix(service) + "-*.json", SearchOption.AllDirectories));
        var saved = Assert.Single(Directory.GetFiles(workspace.Root, "collection-*.json", SearchOption.AllDirectories));
        Assert.Equal("OverflowException", JsonSerializer.Deserialize<KrxCollectionReceipt>(File.ReadAllText(saved))!.ErrorKind);
    }

    [Theory]
    [InlineData("BASIC")]
    [InlineData("INDEX")]
    public async Task CancellationAfterSuccessfulReferenceCapturePreservesItAndLeavesRemainingDatesPending(string service)
    {
        using var workspace = new Workspace(); using var cancellation = new CancellationTokenSource();
        var calls = new List<DateOnly>(); var collector = new KrxCollector(workspace.Root, () => Observed);
        var result = await Collect(collector, service, [First, Second], 5,
            date => { calls.Add(date); cancellation.Cancel(); }, ct: cancellation.Token);
        Assert.Equal("CANCELLED", result.Status); Assert.Equal(1, result.Attempts);
        Assert.Equal(new[] { First }, calls); Assert.Equal(new[] { Second }, result.PendingDates);
        var path = Assert.Single(result.SnapshotFiles); var original = File.ReadAllBytes(path);
        var resumed = await Collect(collector, service, [First, Second], 1, calls.Add);
        Assert.Equal("COLLECTED_UNREVIEWED", resumed.Status); Assert.Equal(new[] { First, Second }, calls);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static Task<KrxCollectionReceipt> Collect(KrxCollector collector, string service, DateOnly[] dates, int budget,
        Action<DateOnly>? requested = null, bool empty = false, CancellationToken ct = default)
    {
        var plan = new KrxCollectionPlan("KOSPI", dates, service);
        return service switch
        {
            "BASIC" => collector.RunBasic(plan, budget, Interval,
                (_, date, _) => { requested?.Invoke(date); return Task.FromResult(Basic(date, empty)); }, ct),
            "INDEX" => collector.RunIndex(plan, budget, Interval,
                (_, date, _) => { requested?.Invoke(date); return Task.FromResult(Index(date, empty)); }, ct),
            _ => throw new ArgumentException("Reference fixture requires an explicit service.")
        };
    }

    private static string Stamp(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Prefix(string service) => service == "BASIC" ? "krx-basic-raw" : "krx-index-raw";

    private static KrxBasicSnapshot Basic(DateOnly date, bool empty = false, string? listingDate = null, string rowMarket = "KOSPI")
    {
        var raw = empty ? "{\"OutBlock_1\":[]}" : $$"""
            {"OutBlock_1":[{"ISU_CD":"KR7005930003","ISU_SRT_CD":"005930","ISU_NM":"합성 기업",
            "LIST_DD":"{{listingDate ?? Stamp(date.AddDays(-1))}}","MKT_TP_NM":"{{rowMarket}}","SECUGRP_NM":"주권",
            "SECT_TP_NM":"우량기업부","KIND_STKCERT_TP_NM":"보통주","LIST_SHRS":"1,000"}]}
            """;
        return new("synthetic-basic", "KOSPI", date, Observed,
            "https://data-dbg.krx.co.kr/svc/apis/sto/stk_isu_base_info", Hash(raw), raw, KrxReferenceClient.ParseBasicInfo(raw));
    }
    private static KrxIndexSnapshot Index(DateOnly date, bool empty = false, string indexClass = "KOSPI")
    {
        var raw = empty ? "{\"OutBlock_1\":[]}" : $$"""
            {"OutBlock_1":[{"BAS_DD":"{{Stamp(date)}}","IDX_CLSS":"{{indexClass}}","IDX_NM":"합성 지수",
            "OPNPRC_IDX":"100","HGPRC_IDX":"110","LWPRC_IDX":"90","CLSPRC_IDX":"105","ACC_TRDVOL":"1000","ACC_TRDVAL":"105000"}]}
            """;
        return new("synthetic-index", "KOSPI", date, Observed,
            "https://data-dbg.krx.co.kr/svc/apis/idx/kospi_dd_trd", Hash(raw), raw, KrxReferenceClient.ParseIndex(raw, date));
    }
    private static KrxSnapshot Daily(DateOnly date)
    {
        var raw = $$"""
            {"OutBlock_1":[{"BAS_DD":"{{Stamp(date)}}","ISU_CD":"005930","ISU_NM":"legacy fixture","MKT_NM":"KOSPI","SECT_TP_NM":"",
            "TDD_OPNPRC":"100","TDD_HGPRC":"110","TDD_LWPRC":"90","TDD_CLSPRC":"105","ACC_TRDVOL":"1000","ACC_TRDVAL":"105000","MKTCAP":"1000000","LIST_SHRS":"10000"}]}
            """;
        return new("synthetic-legacy-daily", "KOSPI", date, Observed,
            "https://data-dbg.krx.co.kr/svc/apis/sto/stk_bydd_trd", Hash(raw), raw, KrxClient.Parse(raw, date, "KOSPI"));
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "krx-reference-collection-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            var root = Path.GetFullPath(Root); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("krx-reference-collection-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected reference collection fixture cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
