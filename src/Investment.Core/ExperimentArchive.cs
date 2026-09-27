namespace Investment.Core;

public sealed record ExperimentArchive(int SchemaVersion, Dataset Data, SourceSnapshot Source,
    ResearchResult? Research = null, RunResult? Backtest = null, RunResult? GrossBacktest = null, CohortResult? Cohort = null,
    AiExploration? Ai = null)
{
    public ReplayCheck Reproduce(SourceSnapshot current)
    {
        if (SchemaVersion == 2 && Cohort != null && Research == null && Backtest == null && GrossBacktest == null)
        {
            if ((Ai == null) != (Cohort.HypothesesCreatedAt == null)) throw new ArgumentException("AI cohort requires complete hypothesis provenance.");
            if (Ai != null)
            {
                if (Ai.CodeVersion != current.Hash || Ai.TrainingHash != AiResearchWorker.TrainingData(Data, Cohort.Plan).Hash ||
                    Ai.Rounds.Length == 0 || Ai.Status != "REQUEST_BUDGET_EXHAUSTED" ||
                    !Ai.Rounds.SelectMany(r => r.Proposal.Output.Candidates).SequenceEqual(Cohort.Candidates) ||
                    Ai.Rounds.Max(r => r.Proposal.ReceivedAt) != Cohort.HypothesesCreatedAt)
                    throw new ArgumentException("AI proposal inputs, candidates or registration time differ.");
                foreach (var round in Ai.Rounds) OpenAiHypotheses.ValidateProposal(round.Proposal, Ai.Settings);
            }
            return EvidenceReplay.Cohort(Data, Cohort, Source, current);
        }
        if (Cohort != null || Ai != null) throw new ArgumentException("Invalid cohort archive kind/version.");
        if (SchemaVersion != 1 || (Research == null) == (Backtest == null)) throw new ArgumentException("Invalid experiment archive kind/version.");
        if (Research != null)
        {
            if (GrossBacktest != null) throw new ArgumentException("Unexpected gross backtest in research archive.");
            return EvidenceReplay.Research(Data, Research, Source, current);
        }
        var net = EvidenceReplay.Backtest(Data, Backtest!, Source, current);
        if (GrossBacktest == null) return net;
        var gross = EvidenceReplay.Backtest(Data, GrossBacktest, Source, current);
        var differences = net.Differences.Concat(gross.Differences.Select(d => "gross-" + d)).ToArray();
        return net with { Matches = net.Matches && gross.Matches, Differences = differences };
    }
}
