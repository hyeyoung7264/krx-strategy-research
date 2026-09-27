namespace Investment.Core;

public enum StrategyStatus { EXPERIMENTAL, BACKTESTED, VALIDATED, PAPER, APPROVED, REJECTED, DISABLED }
public sealed record StrategyVersion(StrategySpec Spec, StrategyStatus Status, string[] EvidenceIds);
public sealed record Promotion(StrategyVersion Before, StrategyVersion After, DateTimeOffset At, string Reason);
public static class PromotionGate
{
    public static Promotion Backtested(StrategyVersion version, RunResult run, DateTimeOffset now)
    {
        if (version.Status != StrategyStatus.EXPERIMENTAL || !run.Strategies.Any(s => s == version.Spec)) throw new ArgumentException("Evidence version mismatch/state.");
        return Move(version, StrategyStatus.BACKTESTED, run.Id, now, "Backtest recorded; no profitability claim.");
    }
    public static Promotion Validated(StrategyVersion version, ResearchResult research, DateTimeOffset now)
    {
        if (version.Status != StrategyStatus.BACKTESTED || research.Holdout.Strategies.Single() != version.Spec || research.Evaluation.Decision != "PAPER_ELIGIBLE" || research.Synthetic || !research.PointInTimeCertified)
            throw new ArgumentException("Research gate failed; hold/reject without promotion.");
        return Move(version, StrategyStatus.VALIDATED, research.Id, now, "Out-of-sample and walk-forward evidence passed.");
    }
    public static Promotion Paper(StrategyVersion version, PaperState paper, DateTimeOffset now)
    {
        if (version.Status != StrategyStatus.VALIDATED || !paper.Strategies.Contains(version.Spec) || !paper.ResearchEvidenceIds.Any(version.EvidenceIds.Contains)) throw new ArgumentException("Paper session evidence mismatch.");
        return Move(version, StrategyStatus.PAPER, paper.SessionId, now, "Forward virtual session started; performance unproven.");
    }
    public static Evaluation Review(ResearchResult research, PaperState? paper, int declaredCandidates,
        PaperJournal? journal = null, string? codeVersion = null)
    {
        if (paper == null) return new("HOLD", ["FORWARD_PAPER_REQUIRED"], decimal.MinValue);
        if (!paper.Strategies.Contains(research.Holdout.Strategies.Single()) || !paper.ResearchEvidenceIds.Contains(research.Id)) throw new ArgumentException("Paper evidence mismatch.");
        // Weak paper performance cannot be overridden by a strong historical backtest.
        var paperResult = journal == null ? PaperEngine.Evaluate(paper, declaredCandidates) :
            journal.Evaluate(paper, codeVersion ?? throw new ArgumentException("Review requires the validated executable/source version."));
        if (paperResult.Decision != "REVIEW_ELIGIBLE_PAPER_ONLY") return paperResult;
        if (research.Evaluation.Decision != "PAPER_ELIGIBLE") return new("HOLD", ["RESEARCH_GATE_FAILED"], paperResult.LowerDailyMean);
        return new("OWNER_REVIEW_REQUIRED_PAPER_ONLY", [], paperResult.LowerDailyMean);
    }
    public static Promotion Disable(StrategyVersion version, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason) || version.Status is StrategyStatus.DISABLED or StrategyStatus.REJECTED) throw new ArgumentException("Invalid disable.");
        return Move(version, StrategyStatus.DISABLED, "", now, reason);
    }
    private static Promotion Move(StrategyVersion v, StrategyStatus next, string evidence, DateTimeOffset now, string reason) =>
        new(v, v with { Status = next, EvidenceIds = string.IsNullOrEmpty(evidence) ? v.EvidenceIds : v.EvidenceIds.Append(evidence).ToArray() }, now, reason);
}
