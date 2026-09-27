namespace Investment.Core;

public sealed record ExperimentArchive(int SchemaVersion, Dataset Data, SourceSnapshot Source,
    ResearchResult? Research = null, RunResult? Backtest = null, RunResult? GrossBacktest = null)
{
    public ReplayCheck Reproduce(SourceSnapshot current)
    {
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
