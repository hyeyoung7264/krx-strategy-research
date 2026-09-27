using System.Globalization;
using System.Text;

namespace Investment.Core;

public static class ReportWriter
{
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
        text.AppendLine(); text.AppendLine("## Limitations"); text.AppendLine();
        text.AppendLine("Daily bars do not prove fillability or enforce an exact intraday daily-loss ceiling. Gaps, price limits, suspensions and quote gaps can exceed risk triggers. Corporate actions are rejected pending an explicit point-in-time adjustment model.");
        text.AppendLine("Bootstrap bounds are conditional on the preregistered candidate set, block-length assumptions and available sample. Repeatedly inspecting/reusing holdout invalidates the inference. Independent forward paper evidence is required.");
        return text.ToString();
    }
}
