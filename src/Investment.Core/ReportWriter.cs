using System.Globalization;
using System.Text;

namespace Investment.Core;

public static class ReportWriter
{
    public static string Markdown(CohortResult result)
    {
        string P(decimal value) => value.ToString("P4", CultureInfo.InvariantCulture);
        string Bound(decimal value) => value == decimal.MinValue ? "insufficient evidence/resolution" : P(value);
        var text = new StringBuilder();
        text.AppendLine("# Family and portfolio research cohort");
        text.AppendLine(); text.AppendLine($"Decision: **{result.Evaluation.Decision}**. Synthetic: **{result.Synthetic}**. Point-in-time certified: **{result.PointInTimeCertified}**.");
        text.AppendLine("Historical simulation only; no forward paper performance or live approval.");
        text.AppendLine($"Cohort: `{result.Id}`. Dataset: `{result.DataHash}`. Source: `{result.Holdout.CodeVersion}`.");
        text.AppendLine($"Preregistered multiple-test count: {result.DeclaredHypotheses} (standalone candidates plus all one-per-family combinations).");
        text.AppendLine(); foreach (var reason in result.Evaluation.Reasons) text.AppendLine("- " + reason);
        text.AppendLine(); text.AppendLine("## Family holdouts"); text.AppendLine();
        text.AppendLine("| Family | Selected from training | Gate | Net return | MDD | Closed trades | Adjusted lower daily mean |");
        text.AppendLine("|---|---|---|---:|---:|---:|---:|");
        foreach (var family in result.Families)
            text.AppendLine($"| {family.Holdout.Strategies.Single().Family} | {family.Holdout.Strategies.Single().Id} | {family.Evaluation.Decision} | {P(family.Holdout.Metrics.TotalReturn)} | {P(family.Holdout.Metrics.MaximumDrawdown)} | {family.Holdout.Metrics.NumberOfTrades} | {Bound(family.Evaluation.LowerDailyMean)} |");
        text.AppendLine(); text.AppendLine("## Shared-capital portfolio walk-forward"); text.AppendLine();
        text.AppendLine("| Fold | Future test | Versions | Validation passed | Test return | MDD | Trades |");
        text.AppendLine("|---|---|---|---|---:|---:|---:|");
        foreach (var fold in result.Folds)
            text.AppendLine($"| {fold.Index} | {fold.Test.Start:yyyy-MM-dd} to {fold.Test.End:yyyy-MM-dd} | {string.Join("; ", fold.SelectedIds)} | {fold.ValidationPassed} | {P(fold.Test.Metrics.TotalReturn)} | {P(fold.Test.Metrics.MaximumDrawdown)} | {fold.Test.Metrics.NumberOfTrades} |");
        text.AppendLine(); text.AppendLine("## Joint holdout"); text.AppendLine();
        text.AppendLine($"{result.Holdout.Start:yyyy-MM-dd} to {result.Holdout.End:yyyy-MM-dd}. All versions were selected without holdout data.");
        text.AppendLine($"Net return: {P(result.Holdout.Metrics.TotalReturn)}. Gross separate simulation: {P(result.Holdout.GrossMetrics?.TotalReturn ?? result.Holdout.Metrics.TotalReturn)}. MDD: {P(result.Holdout.Metrics.MaximumDrawdown)}. Closed trades: {result.Holdout.Metrics.NumberOfTrades}.");
        text.AppendLine($"Adjusted lower daily mean: {Bound(result.Evaluation.LowerDailyMean)}. Daily 1% remains a measurement target, never a gate.");
        text.AppendLine(); text.AppendLine("## Correlation diagnostics"); text.AppendLine();
        foreach (var pair in result.Correlations)
            text.AppendLine($"- {pair.FirstFamily}/{pair.SecondFamily}, {pair.Sessions} aligned daily sessions: {pair.Correlation?.ToString("F4", CultureInfo.InvariantCulture) ?? "undefined (flat returns)"}.");
        text.AppendLine("Correlation is a historical diagnostic, not proof of independence or a new selection rule. The portfolio simulation shares cash, security/sector/strategy caps and liquidity between families.");
        AppendCostDiagnostics(text, result.CostDiagnostics);
        text.AppendLine("Small samples, bootstrap assumptions, gaps, suspensions and daily-bar fill approximations limit inference. Insufficient tail resolution fails the statistical gate. Real forward paper evidence remains required.");
        return text.ToString();
    }
    public static string Markdown(ResearchResult result)
    {
        string P(decimal value) => value.ToString("P4", CultureInfo.InvariantCulture);
        string N(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.AppendLine("# Research evidence report");
        text.AppendLine(); text.AppendLine($"Decision: **{result.Evaluation.Decision}**");
        text.AppendLine($"Synthetic: **{result.Synthetic}**. Point-in-time certified: **{result.PointInTimeCertified}**.");
        text.AppendLine("This report is historical simulation. It contains no forward paper trading performance and no live approval.");
        text.AppendLine(); text.AppendLine($"Experiment: `{result.Id}`. Dataset SHA-256: `{result.DataHash}`.");
        text.AppendLine($"Source SHA-256: `{result.Holdout.CodeVersion}`.");
        text.AppendLine(); text.AppendLine("## Decision reasons"); text.AppendLine();
        foreach (var reason in result.Evaluation.Reasons) text.AppendLine("- " + reason);
        text.AppendLine(); text.AppendLine("## Walk-forward windows"); text.AppendLine();
        text.AppendLine("| Fold | Train | Validation | Future test | Selected | Test net return | MDD | Trades | Validation passed |");
        text.AppendLine("|---|---|---|---|---|---:|---:|---:|---|");
        foreach (var f in result.Folds) text.AppendLine($"| {f.Index} | {f.TrainStart:yyyy-MM-dd} to {f.TrainEnd:yyyy-MM-dd} | {f.ValidationStart:yyyy-MM-dd} to {f.ValidationEnd:yyyy-MM-dd} | {f.TestStart:yyyy-MM-dd} to {f.TestEnd:yyyy-MM-dd} | {f.SelectedId} | {P(f.Test.Metrics.TotalReturn)} | {P(f.Test.Metrics.MaximumDrawdown)} | {f.Test.Metrics.NumberOfTrades} | {f.ValidationPassed} |");
        text.AppendLine(); text.AppendLine("## Final holdout"); text.AppendLine();
        var h = result.Holdout; var m = h.Metrics;
        text.AppendLine($"{h.Start:yyyy-MM-dd} to {h.End:yyyy-MM-dd}. Selected version: `{h.Strategies.Single().Id}`.");
        text.AppendLine(); text.AppendLine("| Metric | Value |"); text.AppendLine("|---|---:|");
        text.AppendLine($"| Net total return | {P(m.TotalReturn)} |");
        text.AppendLine($"| Gross total return (separate simulation) | {P(h.GrossMetrics?.TotalReturn ?? m.TotalReturn)} |");
        text.AppendLine($"| Average daily return, all sessions | {P(m.AverageDailyReturn)} |");
        text.AppendLine($"| Multiple-test adjusted lower daily mean bound | {(result.Evaluation.LowerDailyMean == decimal.MinValue ? "insufficient evidence" : P(result.Evaluation.LowerDailyMean))} |");
        text.AppendLine($"| Research target (does not change gates) | {P(result.Evaluation.TargetDailyMean)} |");
        text.AppendLine($"| Maximum drawdown | {P(m.MaximumDrawdown)} |");
        text.AppendLine($"| Expected value per closed trade, currency units | {N(m.ExpectedValuePerTrade)} |");
        text.AppendLine($"| Closed trades | {m.NumberOfTrades} |"); text.AppendLine($"| Open positions | {m.OpenPositions} |");
        text.AppendLine($"| Sharpe (zero risk-free rate assumption) | {m.Sharpe?.ToString("F4", CultureInfo.InvariantCulture) ?? "undefined"} |");
        AppendCostDiagnostics(text, result.CostDiagnostics);
        text.AppendLine(); text.AppendLine("## Limitations"); text.AppendLine();
        text.AppendLine("Daily bars do not prove fillability or enforce an exact intraday daily-loss ceiling. Gaps, price limits, suspensions and quote gaps can exceed risk triggers. Corporate actions are rejected pending an explicit point-in-time adjustment model.");
        text.AppendLine("Bootstrap bounds are conditional on the preregistered candidate set, block-length assumptions and available sample. Repeatedly inspecting/reusing holdout invalidates the inference. Independent forward paper evidence is required.");
        return text.ToString();
    }

    private static void AppendCostDiagnostics(StringBuilder text, CostSensitivityReport? report)
    {
        if (report == null) return;
        string P(decimal value) => value.ToString("P4", CultureInfo.InvariantCulture);
        text.AppendLine(); text.AppendLine("## Preregistered cost sensitivity before holdout"); text.AppendLine();
        text.AppendLine(report.Note);
        text.AppendLine("Each window keeps its selected strategies. Return changes are percentage-point differences from the original cost assumptions; overlapping windows are not pooled. Holdout is excluded.");
        text.AppendLine();
        text.AppendLine("| Window | Period | Scenario | Net return | Return change | MDD | Closed trades | Events |");
        text.AppendLine("|---|---|---|---:|---:|---:|---:|---:|");
        foreach (var window in report.Windows)
        {
            text.AppendLine($"| {window.WindowKey} | {window.Start:yyyy-MM-dd} to {window.End:yyyy-MM-dd} | baseline | {P(window.BaselineMetrics.TotalReturn)} | {P(0)} | {P(window.BaselineMetrics.MaximumDrawdown)} | {window.BaselineMetrics.NumberOfTrades} | {window.BaselineEvents.Length} |");
            foreach (var scenario in window.Scenarios)
                text.AppendLine($"| {window.WindowKey} | {window.Start:yyyy-MM-dd} to {window.End:yyyy-MM-dd} | {scenario.ScenarioKey} | {P(scenario.Metrics.TotalReturn)} | {P(scenario.Delta.TotalReturn)} | {P(scenario.Metrics.MaximumDrawdown)} | {scenario.Metrics.NumberOfTrades} | {scenario.Events.Length} |");
        }
        text.AppendLine();
    }
}
