namespace Investment.Core;

public sealed record ResearchPlan(int TrainSessions = 120, int ValidationSessions = 60, int TestSessions = 60,
    int HoldoutSessions = 60, int MinimumTrades = 30, int MinimumEvaluationSessions = 60, bool PolicyReviewed = false)
{
    public void Validate()
    {
        if (TrainSessions < 30 || ValidationSessions < 30 || TestSessions < 30 || HoldoutSessions < 30 || MinimumTrades < 1 || MinimumEvaluationSessions < 30)
            throw new ArgumentException("Insufficient research windows.");
    }
}
public sealed record Fold(int Index, DateOnly TrainStart, DateOnly TrainEnd, DateOnly ValidationStart,
    DateOnly ValidationEnd, DateOnly TestStart, DateOnly TestEnd, string SelectedId,
    RunResult[] Training, RunResult Validation, RunResult Test, bool ValidationPassed);
public sealed record Evaluation(string Decision, string[] Reasons, decimal LowerDailyMean, decimal TargetDailyMean = .01m);
public sealed record ResearchResult(string Id, string DataHash, bool Synthetic, bool PointInTimeCertified,
    StrategySpec[] Candidates, ResearchPlan Plan, Fold[] Folds, RunResult[] FinalTraining, RunResult FinalValidation,
    RunResult Holdout, Evaluation Evaluation, DateTimeOffset CreatedAt);

public interface IHypothesisGenerator { StrategySpec[] Generate(); }
// A finite, registered search space. No unconstrained generated code execution.
public sealed class BaselineHypotheses : IHypothesisGenerator
{
    public StrategySpec[] Generate() => [new("momentum", 10, .02m, 5), new("momentum", 20, .03m, 10),
        new("reversion", 5, .03m, 3), new("reversion", 10, .05m, 5)];
}
public sealed class ResearchAgent
{
    public ResearchResult Run(Dataset data, StrategySpec[] candidates, ResearchPlan plan, Costs costs, Risk risk, string codeVersion)
    {
        data.Validate(); plan.Validate();
        if (candidates.Length < 2 || candidates.Length > 100 || candidates.Select(c => c.Id).Distinct().Count() != candidates.Length) throw new ArgumentException("Register 2..100 unique candidates before accessing holdout.");
        var dates = data.Dates; var researchEnd = dates.Length - plan.HoldoutSessions;
        var foldLength = plan.TrainSessions + plan.ValidationSessions + plan.TestSessions;
        if (researchEnd < foldLength + plan.TestSessions) throw new ArgumentException("At least two non-overlapping walk-forward test windows plus final holdout required.");
        var engine = new BacktestEngine(); var folds = new List<Fold>();
        RunResult Test(StrategySpec candidate, int first, int count) => engine.Run(data, [candidate], dates[first], dates[first + count - 1], costs, risk, codeVersion: codeVersion);
        for (var offset = 0; offset + foldLength <= researchEnd; offset += plan.TestSessions)
        {
            var training = candidates.Select(c => Test(c, offset, plan.TrainSessions)).ToArray();
            var selected = Select(training, risk);
            var valFirst = offset + plan.TrainSessions; var testFirst = valFirst + plan.ValidationSessions;
            var validation = Test(selected.Strategies[0], valFirst, plan.ValidationSessions);
            var test = Test(selected.Strategies[0], testFirst, plan.TestSessions);
            var passed = Basic(validation, plan, risk);
            folds.Add(new(folds.Count, dates[offset], dates[valFirst - 1], dates[valFirst], dates[testFirst - 1], dates[testFirst],
                dates[testFirst + plan.TestSessions - 1], selected.Strategies[0].Id, training, validation, test, passed));
        }
        // Final candidate chosen by training alone; the final validation can veto, never retune.
        var finalTrainFirst = researchEnd - plan.ValidationSessions - plan.TrainSessions;
        var finalTrain = candidates.Select(c => Test(c, finalTrainFirst, plan.TrainSessions)).ToArray();
        var finalSelected = Select(finalTrain, risk).Strategies[0];
        var finalValidation = Test(finalSelected, researchEnd - plan.ValidationSessions, plan.ValidationSessions);
        var holdout = Test(finalSelected, researchEnd, plan.HoldoutSessions);
        var reasons = new List<string>();
        if (!plan.PolicyReviewed) reasons.Add("RESEARCH_POLICY_NOT_REVIEWED: cost/risk/statistical settings are illustrative defaults");
        if (data.Synthetic) reasons.Add("SYNTHETIC_DATA: engine evidence only");
        if (!data.PointInTimeCertified) reasons.Add("DATA_NOT_POINT_IN_TIME_CERTIFIED");
        if (folds.Any(f => !f.ValidationPassed || !Basic(f.Test, plan, risk))) reasons.Add("WALK_FORWARD_FAILURE");
        if (!Basic(finalValidation, plan, risk)) reasons.Add("FINAL_VALIDATION_FAILURE");
        if (!Basic(holdout, plan, risk)) reasons.Add("HOLDOUT_FAILURE_OR_INSUFFICIENT_TRADES");
        var daily = holdout.Equity.Select(e => e.DailyReturn).ToArray();
        var bound = Statistics.LowerMeanBound(daily, candidates.Length, block: Math.Max(5, candidates.Max(c => c.HoldDays)));
        if (bound <= 0) reasons.Add("MULTIPLE_TEST_ADJUSTED_LOWER_BOUND_NOT_POSITIVE");
        if (holdout.Events.Length > 0) reasons.Add("RISK_OR_REGIME_HALT");
        // PAPER_ELIGIBLE is evidence eligibility, never authorization to place live orders.
        return new(Guid.NewGuid().ToString("N"), data.Hash, data.Synthetic, data.PointInTimeCertified, candidates, plan,
            folds.ToArray(), finalTrain, finalValidation, holdout,
            new(reasons.Count == 0 ? "PAPER_ELIGIBLE" : "HOLD_OR_REJECT", reasons.ToArray(), bound), DateTimeOffset.UtcNow);
    }
    private static RunResult Select(RunResult[] training, Risk risk) => training
        .OrderByDescending(r => r.Metrics.MaximumDrawdown <= risk.Drawdown && r.Metrics.ExpectedValuePerTrade > 0)
        .ThenByDescending(r => r.Metrics.TotalReturn).ThenBy(r => r.Strategies[0].Id, StringComparer.Ordinal).First();
    internal static bool Basic(RunResult r, ResearchPlan p, Risk risk) => r.Equity.Length >= p.MinimumEvaluationSessions &&
        r.Metrics.NumberOfTrades >= p.MinimumTrades && r.Metrics.ExpectedValuePerTrade > 0 && r.Metrics.TotalReturn > 0 &&
        r.Metrics.MaximumDrawdown <= risk.Drawdown && r.Events.Length == 0;
}
