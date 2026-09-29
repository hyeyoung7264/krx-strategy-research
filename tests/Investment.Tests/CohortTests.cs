using Investment.Core;
using System.Text.Json;
using Xunit;

namespace Investment.Tests;

public sealed class CohortTests
{
    private static readonly ResearchPlan Plan = new(60, 30, 30, 30, 1, 30);
    private static SourceSnapshot Source()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        return SourceSnapshot.Capture(root?.FullName ?? throw new InvalidOperationException("Repository root missing."), typeof(CohortAgent).Assembly);
    }
    [Fact] public void CohortPreservesTwoFamilyChoicesAndDisjointSharedPortfolioWindows()
    {
        var data = DataFiles.Demo(180); var candidates = new BaselineHypotheses().Generate();
        var result = new CohortAgent().Run(data, candidates, Plan, new(), new(), "fixture");
        Assert.Equal(8, result.DeclaredHypotheses); Assert.Equal(2, result.Families.Length); Assert.Equal(2, result.Folds.Length);
        Assert.Equal(2, result.Holdout.Strategies.Select(s => s.Family).Distinct().Count());
        foreach (var fold in result.Folds)
        {
            Assert.True(fold.Validation.End < fold.Test.Start); Assert.True(fold.Test.End < result.Holdout.Start);
            Assert.Equal(2, fold.SelectedIds.Length); Assert.Equal(fold.SelectedIds, fold.Test.Strategies.Select(s => s.Id));
        }
        Assert.True(result.Folds[0].Test.End < result.Folds[1].Test.Start);
        Assert.Equal("HOLD_OR_REJECT", result.Evaluation.Decision);
        Assert.Contains(result.Evaluation.Reasons, r => r.StartsWith("SYNTHETIC_DATA", StringComparison.Ordinal));
        foreach (var family in result.Families)
            Assert.Equal(Statistics.LowerMeanBound(family.Holdout.Equity.Select(p => p.DailyReturn).ToArray(), 8, block: 10), family.Evaluation.LowerDailyMean);
        Assert.Equal(30, Assert.Single(result.Correlations).Sessions);
    }
    [Fact] public void HoldoutMutationCannotRetuneFamilyChoicesOrJointValidation()
    {
        var data = DataFiles.Demo(180); var firstHoldout = data.Dates[^30];
        var changed = data with { Bars = data.Bars.Select(b => b.Date >= firstHoldout ? b with
            { Open = b.Open * 2, High = b.High * 2, Low = b.Low * 2, Close = b.Close * 2 } : b).ToArray() };
        var candidates = new BaselineHypotheses().Generate(); var agent = new CohortAgent();
        var original = agent.Run(data, candidates, Plan, new(), new(), "fixture");
        var modified = agent.Run(changed, candidates, Plan, new(), new(), "fixture");
        Assert.Equal(original.Holdout.Strategies, modified.Holdout.Strategies);
        Assert.Equal(original.FinalValidation.Metrics, modified.FinalValidation.Metrics);
        Assert.Equal(original.Folds.SelectMany(f => f.SelectedIds), modified.Folds.SelectMany(f => f.SelectedIds));
        Assert.Equal(original.Families.Select(f => f.FinalValidation.Metrics), modified.Families.Select(f => f.FinalValidation.Metrics));
    }
    [Fact] public void CohortArchiveReproducesBothFamiliesPortfolioAndCorrectionCount()
    {
        var source = Source(); var data = DataFiles.Demo(180);
        var result = new CohortAgent().Run(data, new BaselineHypotheses().Generate(), Plan, new(), new(), source.Hash);
        var archive = new ExperimentArchive(2, data, source, Cohort: result);
        var restored = JsonSerializer.Deserialize<ExperimentArchive>(JsonSerializer.Serialize(archive))!;
        Assert.True(restored.Reproduce(source).Matches);
        Assert.False((archive with { Cohort = result with { DeclaredHypotheses = 1 } }).Reproduce(source).Matches);
        Assert.False((archive with { Cohort = result with { Evaluation = new("PAPER_ELIGIBLE", [], .01m) } }).Reproduce(source).Matches);
        Assert.Throws<ArgumentException>(() => (archive with { SchemaVersion = 1 }).Reproduce(source));
        Assert.Throws<ArgumentException>(() => (archive with { Research = result.Families[0] }).Reproduce(source));
    }
    [Fact] public void SingleFamilyAndUnderspecifiedFamiliesCannotEnterCohortSearch()
    {
        var data = DataFiles.Demo(180); var agent = new CohortAgent(); var candidates = new BaselineHypotheses().Generate();
        Assert.Throws<ArgumentException>(() => agent.Run(data, candidates.Take(2).ToArray(), Plan, new(), new(), "fixture"));
        Assert.Throws<ArgumentException>(() => agent.Run(data, [candidates[0], candidates[2]], Plan, new(), new(), "fixture"));
        var values = Enumerable.Repeat(.01m, 120).ToArray();
        Assert.Equal(decimal.MinValue, Statistics.LowerMeanBound(values, 1000));
    }
    private static Dataset ProfitableAccountingFixture()
    {
        // Fabricated prices/calendar/certification for engine branch coverage only. Never exported as research evidence.
        var date = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).DateTime).AddDays(-1);
        var dates = new List<DateOnly>();
        while (dates.Count < 180) { if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) dates.Add(date); date = date.AddDays(-1); }
        dates.Reverse(); var bars = new List<Bar>(); decimal trend = 10000;
        for (var index = 0; index < dates.Count; index++)
        {
            for (var ticker = 0; ticker < 10; ticker++)
            {
                var price = ticker == 9 ? (index % 3) switch { 0 => 10000m, 1 => 9000m, _ => 9600m } : trend;
                bars.Add(new("FIXTURE" + ticker, "sector" + ticker % 4, dates[index], Clock.Close(dates[index]), price, price * 1.001m,
                    price * .999m, price, 100_000_000, price * 100_000_000, true, true));
            }
            trend *= 1.002m;
        }
        return new("FABRICATED ACCOUNTING FIXTURE NOT MARKET EVIDENCE", false, true, bars.ToArray(), dates.ToArray(),
            SessionHours: dates.Select(d => new SessionHours(d, Clock.Open(d), Clock.Close(d),
                Clock.Open(d).AddDays(-1), "FABRICATED SESSION FIXTURE NOT MARKET EVIDENCE")).ToArray());
    }
    [Fact] public void PassingJointCohortCanStartPaperWithoutReusingSeparateHoldouts()
    {
        var source = Source(); var data = ProfitableAccountingFixture(); var costs = new Costs(0, 0, 0); var risk = new Risk();
        StrategySpec[] candidates = [new("momentum", 2, 0, 1), new("momentum", 3, 0, 1), new("reversion", 2, .05m, 1), new("reversion", 5, .05m, 1)];
        var result = new CohortAgent().Run(data, candidates, Plan with { PolicyReviewed = true }, costs, risk, source.Hash);
        Assert.True(result.Evaluation.Decision == "PAPER_ELIGIBLE", string.Join("; ", result.Evaluation.Reasons));
        Assert.All(result.Families, f => Assert.Equal("PAPER_ELIGIBLE", f.Evaluation.Decision));
        var archive = new ExperimentArchive(2, data, source, Cohort: result);
        var state = PaperEngine.Start([archive], data, costs, risk, DateTimeOffset.UtcNow, source);
        Assert.Equal(result.Id, state.CohortEvidenceId); Assert.Equal(8, state.ResearchCandidateCount);
        Assert.Equal(2, state.Strategies.Length); Assert.False(state.VerifiedFeed); Assert.Empty(state.Fills);
        Assert.All(result.Holdout.Equity, p => Assert.True(p.Exposure <= risk.ExposureCap + .001m));
        var changed = archive with { Cohort = result with { DeclaredHypotheses = 1 } };
        Assert.Throws<ArgumentException>(() => PaperEngine.Start([changed], data, costs, risk, DateTimeOffset.UtcNow, source));
    }
}
