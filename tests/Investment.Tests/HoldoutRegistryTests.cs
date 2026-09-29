using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class HoldoutRegistryTests
{
    private static readonly ResearchPlan Plan = new(60, 30, 30, 30, 1, 30);
    private static Dataset Data() => DataFiles.Demo(180) with { Synthetic = false, Source = "REGISTRY_FIXTURE_NOT_MARKET_EVIDENCE" };
    [Fact] public void ChangingRevisionUniverseOrCodeCannotUnlockPreviouslyReservedDates()
    {
        var root = Path.Combine(Path.GetTempPath(), "holdout-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new HoldoutRegistry(root); var data = Data(); var candidates = new BaselineHypotheses().Generate();
            var path = registry.Reserve(data, candidates, Plan, new(), new(), "v1", "cohort", DateTimeOffset.UtcNow);
            var original = File.ReadAllBytes(path);
            Assert.Throws<InvalidOperationException>(() => registry.Reserve(data with { Source = "different revision" }, candidates, Plan, new(), new(), "v2", "single", DateTimeOffset.UtcNow));
            Assert.Throws<InvalidOperationException>(() => registry.Reserve(data with { Bars = data.Bars.Where(b => b.Ticker == "DEMO00").ToArray() }, candidates, Plan, new(), new(), "v1", "cohort", DateTimeOffset.UtcNow));
            Assert.Equal(original, File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(root, "seal-*.json"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void DisjointFutureHoldoutCanBeReservedWhileRetainingOldTrainingHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "holdout-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new HoldoutRegistry(root); var candidates = new BaselineHypotheses().Generate();
            registry.Reserve(Data(), candidates, Plan, new(), new(), "v1", "cohort", DateTimeOffset.UtcNow);
            var extended = DataFiles.Demo(210) with { Synthetic = false, Source = "REGISTRY_FIXTURE_NOT_MARKET_EVIDENCE" };
            registry.Reserve(extended, candidates, Plan, new(), new(), "v2", "cohort", DateTimeOffset.UtcNow);
            Assert.Equal(2, Directory.GetFiles(root, "seal-*.json").Length);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void InvalidPlanDoesNotBurnAReservationAndLegacySealsFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "holdout-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new HoldoutRegistry(root); var candidates = new BaselineHypotheses().Generate();
            Assert.Throws<ArgumentException>(() => registry.Reserve(Data(), candidates, new(), new(), new(), "v1", "cohort", DateTimeOffset.UtcNow));
            Assert.False(Directory.Exists(root));
            new EvidenceStore(root).Save("seal", "legacy", new { Hash = "unknown", Time = DateTimeOffset.UtcNow });
            var error = Assert.Throws<InvalidOperationException>(() => registry.Reserve(Data(), candidates, Plan, new(), new(), "v1", "cohort", DateTimeOffset.UtcNow));
            Assert.Contains("Legacy holdout seal", error.Message); Assert.Single(Directory.GetFiles(root, "seal-*.json"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public void IncompleteTaxScheduleDoesNotConsumeUnseenHoldout()
    {
        var root = Path.Combine(Path.GetTempPath(), "holdout-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Data(); var candidates = new BaselineHypotheses().Generate();
            var dates = data.Dates;
            var rows = dates.Select(d => new SellTaxSession(d, d.AddDays(2), .002m, "fixture only")).ToArray();
            var incomplete = new Costs(TaxSchedule: new("fixture only", rows[..^1]));
            var registry = new HoldoutRegistry(root);
            var error = Assert.Throws<ArgumentException>(() => registry.Reserve(data, candidates, Plan, incomplete, new(), "v1", "cohort", DateTimeOffset.UtcNow));
            Assert.Contains("Missing explicit sell-tax", error.Message);
            Assert.False(Directory.Exists(root));
            Assert.Throws<ArgumentException>(() => new ResearchAgent().Run(data, candidates, Plan, incomplete, new(), "v1"));
            Assert.Throws<ArgumentException>(() => new CohortAgent().Run(data, candidates, Plan, incomplete, new(), "v1"));
            var complete = new Costs(TaxSchedule: new("fixture only", rows));
            registry.Reserve(data, candidates, Plan, complete, new(), "v1", "cohort", DateTimeOffset.UtcNow);
            Assert.Single(Directory.GetFiles(root, "seal-*.json"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task ConcurrentReservationsPublishOnlyOneOverlappingStudy()
    {
        var root = Path.Combine(Path.GetTempPath(), "holdout-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Data(); var candidates = new BaselineHypotheses().Generate();
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            {
                try { new HoldoutRegistry(root).Reserve(data with { Source = "fixture revision " + index }, candidates, Plan, new(), new(), "v1", "cohort", DateTimeOffset.UtcNow); return true; }
                catch (InvalidOperationException) { return false; }
            })));
            Assert.Equal(1, results.Count(success => success)); Assert.Single(Directory.GetFiles(root, "seal-*.json"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
