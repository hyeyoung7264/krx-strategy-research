namespace Investment.Core;

public sealed record PortfolioFold(int Index, string[] SelectedIds, RunResult Validation, RunResult Test, bool ValidationPassed);
public sealed record FamilyCorrelation(string FirstFamily, string SecondFamily, int Sessions, double? Correlation);
public sealed record CohortResult(string Id, string DataHash, bool Synthetic, bool PointInTimeCertified,
    StrategySpec[] Candidates, ResearchPlan Plan, int DeclaredHypotheses, ResearchResult[] Families,
    PortfolioFold[] Folds, RunResult FinalValidation, RunResult Holdout, FamilyCorrelation[] Correlations,
    Evaluation Evaluation, DateTimeOffset CreatedAt, DateTimeOffset? HypothesesCreatedAt = null);

/// <summary>One preregistered cohort evaluates each family and its selected joint portfolio on the same sealed holdout.</summary>
public sealed class CohortAgent
{
    public CohortResult Run(Dataset data, StrategySpec[] candidates, ResearchPlan plan, Costs costs, Risk risk, string codeVersion,
        DateTimeOffset? hypothesesCreatedAt = null)
    {
        data.Validate(); plan.Validate(); costs.Validate(); risk.Validate();
        costs.RequireCoverage(data.Dates);
        if (candidates.Length > 100 || candidates.Select(c => c.Id).Distinct().Count() != candidates.Length)
            throw new ArgumentException("Register at most 100 unique candidates before cohort evaluation.");
        foreach (var spec in candidates) spec.Validate();
        var groups = candidates.GroupBy(c => c.Family).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        if (groups.Length < 2 || groups.Any(g => g.Count() < 2))
            throw new ArgumentException("Cohort requires at least two families with two registered candidates per family.");
        var combinations = groups.Aggregate(1, (count, group) => checked(count * group.Count()));
        // Cover every registered standalone candidate and every possible one-per-family combination.
        // This count is fixed before any family or combined holdout is read.
        var hypotheses = checked(candidates.Length + combinations);
        var block = Math.Max(5, candidates.Max(c => c.HoldDays));
        var families = groups.Select(g => new ResearchAgent().Run(data, g.ToArray(), plan, costs, risk, codeVersion)).ToArray();
        families = families.Select(f =>
        {
            var bound = Statistics.LowerMeanBound(f.Holdout.Equity.Select(p => p.DailyReturn).ToArray(), hypotheses, block: block);
            var reasons = f.Evaluation.Reasons.Where(r => r != "MULTIPLE_TEST_ADJUSTED_LOWER_BOUND_NOT_POSITIVE").ToList();
            if (bound <= 0) reasons.Add("MULTIPLE_TEST_ADJUSTED_LOWER_BOUND_NOT_POSITIVE");
            return f with { Evaluation = new(reasons.Count == 0 ? "PAPER_ELIGIBLE" : "HOLD_OR_REJECT", reasons.ToArray(), bound) };
        }).ToArray();
        var engine = new BacktestEngine(); var folds = new List<PortfolioFold>();
        for (var index = 0; index < families[0].Folds.Length; index++)
        {
            var components = families.Select(f => f.Folds[index]).ToArray(); var window = components[0];
            var selected = components.Select(f => f.Test.Strategies.Single()).ToArray();
            var validation = engine.Run(data, selected, window.ValidationStart, window.ValidationEnd, costs, risk, codeVersion: codeVersion);
            var test = engine.Run(data, selected, window.TestStart, window.TestEnd, costs, risk, codeVersion: codeVersion);
            folds.Add(new(index, selected.Select(s => s.Id).ToArray(), validation, test,
                components.All(f => f.ValidationPassed) && ResearchAgent.Basic(validation, plan, risk)));
        }
        var finalSpecs = families.Select(f => f.Holdout.Strategies.Single()).ToArray();
        var finalValidation = engine.Run(data, finalSpecs, families[0].FinalValidation.Start, families[0].FinalValidation.End, costs, risk, codeVersion: codeVersion);
        var holdout = engine.Run(data, finalSpecs, families[0].Holdout.Start, families[0].Holdout.End, costs, risk, codeVersion: codeVersion);
        var lower = Statistics.LowerMeanBound(holdout.Equity.Select(p => p.DailyReturn).ToArray(), hypotheses, block: block);
        var failures = new List<string>();
        if (!plan.PolicyReviewed) failures.Add("RESEARCH_POLICY_NOT_REVIEWED");
        if (data.Synthetic) failures.Add("SYNTHETIC_DATA: engine evidence only");
        if (!data.PointInTimeCertified) failures.Add("DATA_NOT_POINT_IN_TIME_CERTIFIED");
        if (hypothesesCreatedAt != null && Clock.Open(families[0].Folds[0].ValidationStart) <= hypothesesCreatedAt)
            failures.Add("AI_HYPOTHESIS_POSTDATES_FORWARD_WINDOWS: historical evaluation is exploratory, not independent prospective validation");
        foreach (var family in families.Where(f => f.Evaluation.Decision != "PAPER_ELIGIBLE"))
            failures.Add("FAMILY_GATE_FAILED:" + family.Holdout.Strategies.Single().Family);
        if (folds.Any(f => !f.ValidationPassed || !ResearchAgent.Basic(f.Test, plan, risk))) failures.Add("PORTFOLIO_WALK_FORWARD_FAILURE");
        if (!ResearchAgent.Basic(finalValidation, plan, risk)) failures.Add("PORTFOLIO_FINAL_VALIDATION_FAILURE");
        if (!ResearchAgent.Basic(holdout, plan, risk)) failures.Add("PORTFOLIO_HOLDOUT_FAILURE_OR_INSUFFICIENT_TRADES");
        if (lower <= 0) failures.Add("MULTIPLE_TEST_ADJUSTED_LOWER_BOUND_NOT_POSITIVE");
        if (holdout.Events.Length > 0) failures.Add("PORTFOLIO_RISK_OR_REGIME_HALT");
        var correlations = new List<FamilyCorrelation>();
        for (var first = 0; first < families.Length; first++)
            for (var second = first + 1; second < families.Length; second++)
            {
                var a = families[first].Holdout.Equity; var b = families[second].Holdout.Equity;
                if (!a.Select(p => p.Date).SequenceEqual(b.Select(p => p.Date))) throw new InvalidOperationException("Cohort family sessions differ.");
                var x = a.Select(p => (double)p.DailyReturn).ToArray(); var y = b.Select(p => (double)p.DailyReturn).ToArray();
                var mx = x.Average(); var my = y.Average();
                var denominator = Math.Sqrt(x.Sum(v => (v - mx) * (v - mx)) * y.Sum(v => (v - my) * (v - my)));
                var correlation = denominator == 0 ? (double?)null : Math.Clamp(x.Zip(y).Sum(pair => (pair.First - mx) * (pair.Second - my)) / denominator, -1, 1);
                correlations.Add(new(finalSpecs[first].Family, finalSpecs[second].Family, x.Length, correlation));
            }
        return new(Guid.NewGuid().ToString("N"), data.Hash, data.Synthetic, data.PointInTimeCertified, candidates, plan,
            hypotheses, families, folds.ToArray(), finalValidation, holdout, correlations.ToArray(),
            new(failures.Count == 0 ? "PAPER_ELIGIBLE" : "HOLD_OR_REJECT", failures.ToArray(), lower), DateTimeOffset.UtcNow, hypothesesCreatedAt);
    }
}
