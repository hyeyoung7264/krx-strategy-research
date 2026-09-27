using Investment.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Investment.Tests;

public sealed class KrxCollectionTests
{
    private static readonly DateOnly First = new(2026, 9, 24);
    private static readonly DateTimeOffset Observed = new(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(9));
    private static string Root() => Path.Combine(Path.GetTempPath(), "krx-collection-" + Guid.NewGuid().ToString("N"));
    private static KrxSnapshot Snapshot(DateOnly date, bool empty = false)
    {
        var json = empty ? "{\"OutBlock_1\":[]}" : $$"""
            {"OutBlock_1":[{"BAS_DD":"{{date:yyyyMMdd}}","ISU_CD":"005930","ISU_NM":"fixture","MKT_NM":"KOSPI","SECT_TP_NM":"",
            "TDD_OPNPRC":"100","TDD_HGPRC":"110","TDD_LWPRC":"90","TDD_CLSPRC":"105","ACC_TRDVOL":"1000","ACC_TRDVAL":"105000","MKTCAP":"1000000","LIST_SHRS":"10000"}]}
            """;
        return new(Guid.NewGuid().ToString("N"), "KOSPI", date, Observed, "https://data-dbg.krx.co.kr/svc/apis/sto/stk_bydd_trd",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), json, KrxClient.Parse(json, date, "KOSPI"));
    }
    [Fact] public async Task BudgetAndResumePreserveOriginalSnapshotAndSkipCachedDates()
    {
        var root = Root(); var calls = new List<DateOnly>();
        try
        {
            var collector = new KrxCollector(root, () => Observed); var plan = new KrxCollectionPlan("KOSPI", [First, First.AddDays(1)]);
            Task<KrxSnapshot> Fetch(string market, DateOnly date, CancellationToken ct) { calls.Add(date); return Task.FromResult(Snapshot(date)); }
            var first = await collector.Run(plan, 1, TimeSpan.FromSeconds(1), Fetch);
            Assert.Equal("REQUEST_BUDGET_REACHED", first.Status); Assert.Equal([First.AddDays(1)], first.PendingDates);
            var original = File.ReadAllBytes(Assert.Single(first.SnapshotFiles));
            var second = await collector.Run(plan, 1, TimeSpan.FromSeconds(1), Fetch);
            Assert.Equal("COLLECTED_UNREVIEWED", second.Status); Assert.Empty(second.PendingDates);
            Assert.Equal(new[] { First, First.AddDays(1) }, calls); Assert.Equal(original, File.ReadAllBytes(second.SnapshotFiles[0]));
            var third = await collector.Run(plan, 1, TimeSpan.FromSeconds(1), Fetch);
            Assert.Equal(0, third.Attempts); Assert.Equal(2, calls.Count);
            Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(second.SnapshotFiles[0])!, "collection-*.json").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task ProviderFailureStopsWithoutRetryAndPersistsPriorSuccessAndRedactedReceipt()
    {
        var root = Root(); var calls = 0; var timer = System.Diagnostics.Stopwatch.StartNew(); var starts = new List<TimeSpan>();
        try
        {
            var plan = new KrxCollectionPlan("KOSPI", [First, First.AddDays(1)]);
            Task<KrxSnapshot> Fetch(string market, DateOnly date, CancellationToken ct)
            { calls++; starts.Add(timer.Elapsed); return date == First ? Task.FromResult(Snapshot(date)) : throw new InvalidOperationException("secret-provider-key"); }
            var result = await new KrxCollector(root, () => Observed).Run(plan, 5, TimeSpan.FromSeconds(1), Fetch);
            Assert.Equal("REQUEST_FAILED", result.Status); Assert.Equal(2, calls); Assert.Single(result.SnapshotFiles);
            Assert.True(starts[1] - starts[0] >= TimeSpan.FromMilliseconds(950));
            Assert.Equal(First.AddDays(1), result.StoppedDate); Assert.Equal("InvalidOperationException", result.ErrorKind);
            var receipt = Directory.GetFiles(Path.GetDirectoryName(result.SnapshotFiles[0])!, "collection-*.json").Single();
            Assert.DoesNotContain("secret-provider-key", File.ReadAllText(receipt));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task EmptyResponseIsPreservedAndBlocksRemainingDatesOnResume()
    {
        var root = Root(); var calls = 0;
        try
        {
            var collector = new KrxCollector(root, () => Observed); var plan = new KrxCollectionPlan("KOSPI", [First, First.AddDays(1)]);
            Task<KrxSnapshot> Fetch(string market, DateOnly date, CancellationToken ct) { calls++; return Task.FromResult(Snapshot(date, true)); }
            var result = await collector.Run(plan, 5, TimeSpan.FromSeconds(1), Fetch);
            Assert.Equal("EMPTY_RESPONSE_REQUIRES_REVIEW", result.Status); Assert.Equal(plan.Dates, result.PendingDates);
            Assert.Single(result.SnapshotFiles); Assert.Empty(JsonSerializer.Deserialize<KrxSnapshot>(File.ReadAllText(result.SnapshotFiles[0]))!.Rows);
            var resumed = await collector.Run(plan, 5, TimeSpan.FromSeconds(1), Fetch);
            Assert.Equal(1, calls); Assert.Equal(0, resumed.Attempts); Assert.Equal(result.Status, resumed.Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task ModifiedCacheIsRejectedBeforeAnyNewRequest()
    {
        var root = Root(); var calls = 0;
        try
        {
            var collector = new KrxCollector(root, () => Observed); var plan = new KrxCollectionPlan("KOSPI", [First, First.AddDays(1)]);
            Task<KrxSnapshot> Fetch(string market, DateOnly date, CancellationToken ct) { calls++; return Task.FromResult(Snapshot(date)); }
            var result = await collector.Run(plan, 1, TimeSpan.FromSeconds(1), Fetch);
            var path = Assert.Single(result.SnapshotFiles);
            var changed = JsonSerializer.Deserialize<KrxSnapshot>(File.ReadAllText(path))! with { RawHash = "changed" };
            File.WriteAllText(path, JsonSerializer.Serialize(changed));
            await Assert.ThrowsAsync<InvalidOperationException>(() => collector.Run(plan, 5, TimeSpan.FromSeconds(1), Fetch));
            Assert.Equal(1, calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task ConcurrentCollectorCannotIssueDuplicateRequestsAndCancellationIsRecorded()
    {
        var root = Root(); var started = new TaskCompletionSource(); var release = new TaskCompletionSource(); var calls = 0;
        try
        {
            var collector = new KrxCollector(root, () => Observed); var plan = new KrxCollectionPlan("KOSPI", [First]);
            async Task<KrxSnapshot> Fetch(string market, DateOnly date, CancellationToken ct) { calls++; started.SetResult(); await release.Task; return Snapshot(date); }
            var running = collector.Run(plan, 1, TimeSpan.FromSeconds(1), Fetch);
            await started.Task;
            await Assert.ThrowsAsync<InvalidOperationException>(() => collector.Run(plan, 1, TimeSpan.FromSeconds(1), Fetch));
            release.SetResult(); await running; Assert.Equal(1, calls);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelled = await collector.Run(new("KOSPI", [First.AddDays(1)]), 1, TimeSpan.FromSeconds(1), Fetch, cancellation.Token);
            Assert.Equal("CANCELLED", cancelled.Status); Assert.Equal(0, cancelled.Attempts);
        }
        finally { release.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task InvalidDatePlansAndSnapshotIdentityNeverReachOrPopulateCollection()
    {
        var root = Root(); var calls = 0;
        try
        {
            var collector = new KrxCollector(root, () => Observed);
            Task<KrxSnapshot> Fetch(string market, DateOnly date, CancellationToken ct) { calls++; return Task.FromResult(Snapshot(date) with { Market = "KOSDAQ" }); }
            foreach (var dates in new[] { new[] { First, First }, new[] { First.AddDays(1), First }, new[] { new DateOnly(2026, 9, 27) } })
                await Assert.ThrowsAsync<ArgumentException>(() => collector.Run(new("KOSPI", dates), 1, TimeSpan.FromSeconds(1), Fetch));
            Assert.Equal(0, calls); Assert.False(Directory.Exists(root));
            var result = await collector.Run(new("KOSPI", [First]), 1, TimeSpan.FromSeconds(1), Fetch);
            Assert.Equal("REQUEST_FAILED", result.Status); Assert.Empty(result.SnapshotFiles); Assert.Equal([First], result.PendingDates);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
