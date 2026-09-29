using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class CostSensitivityTests
{
    private static readonly ResearchPlan Plan = new(30, 30, 30, 30, 1, 30);
    private static readonly CostStressPlan Stress = new([new("higher_commission", .0001m, 0), new("higher-slippage", 0, .001m)]);
    private static readonly StrategySpec[] Candidates = new BaselineHypotheses().Generate();
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static Dataset Data() => DataFiles.Demo(150);
    private static SourceSnapshot Source()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        return SourceSnapshot.Capture(root?.FullName ?? throw new InvalidOperationException("Repository root missing."), typeof(ResearchAgent).Assembly);
    }

    [Fact] public void StressPlanIsImmutableAndStructurallyEqualAfterJsonRoundTrip()
    {
        var rows = new[] { new CostStressScenario("example", .0001m, .0002m) };
        var plan = Plan with { CostStress = new(rows) };
        rows[0] = rows[0] with { CommissionAdd = .1m };
        var exposed = plan.CostStress!.Scenarios; exposed[0] = exposed[0] with { SlippageAdd = .1m };
        Assert.Equal(.0001m, plan.CostStress.Scenarios[0].CommissionAdd);
        Assert.Equal(.0002m, plan.CostStress.Scenarios[0].SlippageAdd);
        var restored = JsonSerializer.Deserialize<ResearchPlan>(Json(plan))!;
        Assert.True(plan == restored); Assert.Equal(plan.GetHashCode(), restored.GetHashCode());
        Assert.DoesNotContain("CostStress", Json(Plan));
    }

    [Fact] public void StressPlanRejectsUnboundedDuplicateOrImprovedCostScenarios()
    {
        Assert.Throws<ArgumentException>(() => new CostStressPlan([]));
        Assert.Throws<ArgumentException>(() => new CostStressPlan(Enumerable.Range(0, 5).Select(i => new CostStressScenario("s" + i, .01m, 0)).ToArray()));
        Assert.Throws<ArgumentException>(() => new CostStressPlan([new("same", .01m, 0), new("same", 0, .01m)]));
        foreach (var key in new[] { "", "a|b", "line\nbreak", new string('x', 65), "공백" })
            Assert.Throws<ArgumentException>(() => new CostStressPlan([new(key, .01m, 0)]));
        Assert.Throws<ArgumentException>(() => new CostStressPlan([new("negative", -.001m, .01m)]));
        Assert.Throws<ArgumentException>(() => new CostStressPlan([new("none", 0, 0)]));
        Assert.Throws<ArgumentException>(() => new CostStressPlan([new("invalid", 0, 1)]));
    }

    [Fact] public void StressPreservesTaxScheduleAndRejectsImpossibleCombinedCharges()
    {
        var day = new DateOnly(2025, 1, 2);
        var schedule = new SellTaxSchedule("synthetic", [new(day, day.AddDays(4), .4m, "synthetic")]);
        var baseline = new Costs(.1m, .1m, .01m, schedule);
        var stressed = new CostStressScenario("example", .1m, .02m).Apply(baseline);
        Assert.Equal(.2m, stressed.Commission); Assert.Equal(.03m, stressed.Slippage);
        Assert.Equal(baseline.SellTax, stressed.SellTax); Assert.Same(schedule, stressed.TaxSchedule);
        Assert.Throws<ArgumentException>(() => CostSensitivityRunner.Preflight(new([new("impossible", .5m, 0)]), baseline, [day]));
    }

    [Fact] public void InvalidAggregateCostsDoNotCreateHoldoutReservationOrDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cost-stress-seal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Data() with { Synthetic = false }; // Fabricated registry validation fixture only.
            var plan = Plan with { CostStress = new([new("too-expensive", .4m, 0)]) };
            Assert.Throws<ArgumentException>(() => new HoldoutRegistry(root).Reserve(data, Candidates, plan,
                new Costs(.6m, .1m, 0), new(), "fixture", "cohort", DateTimeOffset.UtcNow));
            Assert.False(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact] public void EntireTaxCoverageIsCheckedBeforeResearchOrDiagnosticSimulation()
    {
        var data = Data(); var dates = data.Dates;
        var costs = new Costs(0, 0, 0, new("synthetic", dates.SkipLast(1)
            .Select(d => new SellTaxSession(d, d.AddDays(3), .001m, "synthetic")).ToArray()));
        var plan = Plan with { CostStress = Stress };
        var single = Assert.Throws<ArgumentException>(() => new ResearchAgent().Run(data, Candidates, plan, costs, new(), "fixture"));
        var cohort = Assert.Throws<ArgumentException>(() => new CohortAgent().Run(data, Candidates, plan, costs, new(), "fixture"));
        Assert.Contains(dates[^1].ToString("yyyy-MM-dd"), single.Message);
        Assert.Equal(single.Message, cohort.Message);
    }

    [Fact] public void DiagnosticsLeaveSingleAndCohortChoicesAndDecisionsUnchanged()
    {
        var data = Data(); var stressPlan = Plan with { CostStress = Stress };
        var baseline = new ResearchAgent().Run(data, Candidates, Plan, new(), new(), "fixture");
        var stressed = new ResearchAgent().Run(data, Candidates, stressPlan, new(), new(), "fixture");
        Assert.DoesNotContain("CostDiagnostics", Json(baseline));
        Assert.Equal(baseline.Folds.Select(f => f.SelectedId), stressed.Folds.Select(f => f.SelectedId));
        Assert.Equal(baseline.Holdout.Strategies, stressed.Holdout.Strategies);
        Assert.Equal(baseline.Holdout.Metrics, stressed.Holdout.Metrics);
        Assert.Equal(Json(baseline.Evaluation), Json(stressed.Evaluation));
        var baseCohort = new CohortAgent().Run(data, Candidates, Plan, new(), new(), "fixture");
        var stressCohort = new CohortAgent().Run(data, Candidates, stressPlan, new(), new(), "fixture");
        Assert.Equal(baseCohort.DeclaredHypotheses, stressCohort.DeclaredHypotheses);
        Assert.Equal(baseCohort.Folds.SelectMany(f => f.SelectedIds), stressCohort.Folds.SelectMany(f => f.SelectedIds));
        Assert.Equal(baseCohort.Holdout.Strategies, stressCohort.Holdout.Strategies);
        Assert.Equal(baseCohort.Holdout.Metrics, stressCohort.Holdout.Metrics);
        Assert.Equal(Json(baseCohort.Evaluation), Json(stressCohort.Evaluation));
        Assert.All(stressCohort.Families, family => Assert.NotNull(family.CostDiagnostics));
        Assert.Equal(stressCohort.Folds.Length + 1, stressCohort.CostDiagnostics!.Windows.Length);
    }

    [Fact] public void HoldoutPricesAndFutureListingCannotChangeAnyDiagnosticsOrTheirHash()
    {
        var data = Data(); var first = data.Dates[^Plan.HoldoutSessions];
        var future = data.Bars.Where(b => b.Date >= first && b.Ticker == "DEMO00").Select(b => b with { Ticker = "FUTURE" });
        var changed = data with { Bars = data.Bars.Select(b => b.Date >= first ? b with
            { Open = b.Open * 2, High = b.High * 2, Low = b.Low * 2, Close = b.Close * 2 } : b).Concat(future).ToArray(),
            LifecycleEvents = [new("FUTURE", first, "LISTED", Clock.Open(first).AddDays(-1), "future listing fixture")] };
        changed.Validate();
        var plan = Plan with { CostStress = Stress }; var agent = new CohortAgent();
        var before = agent.Run(data, Candidates, plan, new(), new(), "fixture");
        var after = agent.Run(changed, Candidates, plan, new(), new(), "fixture");
        Assert.NotEqual(before.DataHash, after.DataHash);
        Assert.Equal(Json(before.CostDiagnostics), Json(after.CostDiagnostics));
        Assert.Equal(before.Families.Select(f => Json(f.CostDiagnostics)), after.Families.Select(f => Json(f.CostDiagnostics)));
        Assert.DoesNotContain(before.DataHash, Json(before.CostDiagnostics));
        Assert.All(before.CostDiagnostics!.Windows, w => Assert.True(w.End < first));
    }

    [Fact] public void ScenarioMetricsUseFixedStrategiesUnchangedRiskAndOnlyPreHoldoutData()
    {
        var data = Data(); var plan = Plan with { CostStress = Stress }; var costs = new Costs(); var risk = new Risk();
        var result = new ResearchAgent().Run(data, Candidates, plan, costs, risk, "fixture");
        var report = result.CostDiagnostics!;
        var truncated = data with { Bars = data.Bars.Where(b => b.Date < result.Holdout.Start).ToArray() };
        Assert.Equal(truncated.Hash, report.InputDataHash);
        foreach (var window in report.Windows)
        {
            var baseline = window.Role == "final-validation" ? result.FinalValidation : result.Folds.Single(f => "fold-" + f.Index == window.WindowKey).Test;
            Assert.Equal(baseline.Strategies, window.Strategies); Assert.Equal(baseline.Metrics, window.BaselineMetrics);
            foreach (var scenario in window.Scenarios)
            {
                var declared = Stress.Scenarios.Single(s => s.ScenarioKey == scenario.ScenarioKey);
                var expected = new BacktestEngine().Run(truncated, baseline.Strategies, baseline.Start, baseline.End,
                    declared.Apply(costs), risk, baseline.InitialCapital, "fixture");
                Assert.Equal(expected.Metrics, scenario.Metrics); Assert.Equal(expected.Events, scenario.Events);
                Assert.Equal(expected.Metrics.TotalReturn - baseline.Metrics.TotalReturn, scenario.Delta.TotalReturn);
                Assert.Equal(expected.Metrics.MaximumDrawdown - baseline.Metrics.MaximumDrawdown, scenario.Delta.MaximumDrawdown);
            }
        }
    }

    [Fact] public void SingleArchiveRoundTripAndReplayDetectDiagnosticOmissionAndTampering()
    {
        var data = Data(); var source = Source(); var plan = Plan with { CostStress = new([new("stress", .001m, 0)]) };
        var result = new ResearchAgent().Run(data, Candidates, plan, new(), new(), source.Hash);
        var archive = new ExperimentArchive(1, data, source, Research: result);
        var restored = JsonSerializer.Deserialize<ExperimentArchive>(Json(archive))!;
        Assert.True(restored.Reproduce(source).Matches);
        var report = result.CostDiagnostics!; var windows = report.Windows.ToArray();
        var scenarios = windows[0].Scenarios.ToArray(); scenarios[0] = scenarios[0] with { ScenarioKey = "tampered" };
        windows[0] = windows[0] with { Scenarios = scenarios };
        var changed = result with { CostDiagnostics = report with { Windows = windows } };
        Assert.Contains("cost-diagnostics", EvidenceReplay.Research(data, changed, source, source).Differences);
        Assert.Contains("cost-diagnostics", EvidenceReplay.Research(data, result with { CostDiagnostics = null }, source, source).Differences);
    }

    [Fact] public void CohortArchiveReplayIncludesFamilyAndPortfolioDiagnostics()
    {
        var data = Data(); var source = Source(); var plan = Plan with { CostStress = new([new("stress", 0, .001m)]) };
        var result = new CohortAgent().Run(data, Candidates, plan, new(), new(), source.Hash);
        var archive = new ExperimentArchive(2, data, source, Cohort: result);
        Assert.True(JsonSerializer.Deserialize<ExperimentArchive>(Json(archive))!.Reproduce(source).Matches);
        var windows = result.CostDiagnostics!.Windows.ToArray(); var scenarios = windows[0].Scenarios.ToArray();
        scenarios[0] = scenarios[0] with { Metrics = scenarios[0].Metrics with { TotalReturn = 99 } };
        windows[0] = windows[0] with { Scenarios = scenarios };
        Assert.False((archive with { Cohort = result with { CostDiagnostics = result.CostDiagnostics with { Windows = windows } } }).Reproduce(source).Matches);
        var families = result.Families.ToArray(); families[0] = families[0] with { CostDiagnostics = null };
        Assert.False((archive with { Cohort = result with { Families = families } }).Reproduce(source).Matches);
    }
}
