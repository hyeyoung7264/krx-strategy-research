using System.Text.Json;
using System.Text.Json.Nodes;

namespace Investment.Core;

public sealed record ReplayCheck(bool Matches, string[] Differences, string OriginalEvidenceId, string DataHash,
    string CodeHash, string Runtime, string Note = "Reproduction only; it creates no new independent test or forward paper evidence.");
public static class EvidenceReplay
{
    public static ReplayCheck Cohort(Dataset data, CohortResult expected, SourceSnapshot archived, SourceSnapshot current)
    {
        Provenance(data, expected.DataHash, expected.Holdout.CodeVersion, archived, current);
        var actual = new CohortAgent().Run(data, expected.Candidates, expected.Plan, expected.Holdout.Costs, expected.Holdout.Risk, current.Hash, expected.HypothesesCreatedAt);
        var differences = new List<string>(); Compare("cohort", expected, actual, differences);
        return new(differences.Count == 0, differences.ToArray(), expected.Id, data.Hash, current.Hash, current.Runtime);
    }
    public static ReplayCheck Research(Dataset data, ResearchResult expected, SourceSnapshot archived, SourceSnapshot current)
    {
        Provenance(data, expected.DataHash, expected.Holdout.CodeVersion, archived, current);
        var actual = new ResearchAgent().Run(data, expected.Candidates, expected.Plan, expected.Holdout.Costs, expected.Holdout.Risk, current.Hash);
        var differences = new List<string>();
        Compare("research-metadata", new { expected.DataHash, expected.Synthetic, expected.PointInTimeCertified },
            new { actual.DataHash, actual.Synthetic, actual.PointInTimeCertified }, differences);
        Compare("folds", expected.Folds, actual.Folds, differences);
        Compare("final-training", expected.FinalTraining, actual.FinalTraining, differences);
        Compare("final-validation", expected.FinalValidation, actual.FinalValidation, differences);
        Compare("holdout", expected.Holdout, actual.Holdout, differences);
        Compare("evaluation", expected.Evaluation, actual.Evaluation, differences);
        Compare("cost-diagnostics", expected.CostDiagnostics, actual.CostDiagnostics, differences);
        return new(differences.Count == 0, differences.ToArray(), expected.Id, data.Hash, current.Hash, current.Runtime);
    }
    public static ReplayCheck Backtest(Dataset data, RunResult expected, SourceSnapshot archived, SourceSnapshot current)
    {
        Provenance(data, expected.DataHash, expected.CodeVersion, archived, current);
        var actual = new BacktestEngine().Run(data, expected.Strategies, expected.Start, expected.End, expected.Costs, expected.Risk, expected.InitialCapital, current.Hash);
        var differences = new List<string>(); Compare("backtest", expected, actual, differences);
        return new(differences.Count == 0, differences.ToArray(), expected.Id, data.Hash, current.Hash, current.Runtime);
    }
    private static void Provenance(Dataset data, string dataHash, string codeHash, SourceSnapshot archived, SourceSnapshot current)
    {
        data.Validate(); archived.ValidateArchive(); current.ValidateArchive();
        if (data.Hash != dataHash) throw new ArgumentException("Replay data hash mismatch.");
        if (archived.Hash != codeHash || current.Hash != codeHash) throw new ArgumentException("Replay source mismatch; restore and rebuild the archived source in an isolated workspace.");
        if (archived.Runtime != current.Runtime) throw new ArgumentException("Replay runtime mismatch.");
        if (archived.Binaries is not { Length: > 0 } || current.Binaries is not { Length: > 0 }) throw new ArgumentException("Replay requires source-bound binary evidence; legacy archives are unverified.");
    }
    private static void Compare<T>(string name, T expected, T actual, List<string> differences)
    {
        var before = JsonSerializer.SerializeToNode(expected); var after = JsonSerializer.SerializeToNode(actual);
        StripRunIdentity(before); StripRunIdentity(after);
        if (!JsonNode.DeepEquals(before, after)) differences.Add(name);
    }
    private static void StripRunIdentity(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("Id"); obj.Remove("CreatedAt");
            foreach (var value in obj.ToArray()) StripRunIdentity(value.Value);
        }
        else if (node is JsonArray array) foreach (var value in array) StripRunIdentity(value);
    }
}
