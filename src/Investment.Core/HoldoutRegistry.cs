using System.Text.Json;

namespace Investment.Core;

public sealed record HoldoutReservation(string DataHash, DateOnly[] HoldoutDates, StrategySpec[] Candidates,
    ResearchPlan Plan, Costs Costs, Risk Risk, string CodeVersion, string Mode, DateTimeOffset ReservedAt);

// Workspace-wide research registry. A revision/hash change does not make previously inspected sessions independent.
public sealed class HoldoutRegistry(string directory)
{
    public string Reserve(Dataset data, StrategySpec[] candidates, ResearchPlan plan, Costs costs, Risk risk,
        string codeVersion, string mode, DateTimeOffset now)
    {
        data.Validate(); plan.Validate(); costs.Validate(); risk.Validate();
        if (data.Synthetic || mode is not ("single" or "cohort") || string.IsNullOrWhiteSpace(codeVersion))
            throw new ArgumentException("Reserve only real research data with a version and explicit research mode.");
        if (candidates.Length < 2 || candidates.Length > 100 || candidates.Select(s => s.Id).Distinct().Count() != candidates.Length)
            throw new ArgumentException("Register 2..100 unique candidates before reserving holdout.");
        foreach (var candidate in candidates) candidate.Validate();
        if (mode == "cohort" && (candidates.GroupBy(s => s.Family).Count() < 2 || candidates.GroupBy(s => s.Family).Any(g => g.Count() < 2)))
            throw new ArgumentException("Register at least two candidates per family for cohort research.");
        var dates = data.Dates;
        if ((long)dates.Length < (long)plan.TrainSessions + plan.ValidationSessions + 2L * plan.TestSessions + plan.HoldoutSessions)
            throw new ArgumentException("Insufficient dates for two forward folds and holdout; reservation not created.");
        var holdout = dates.TakeLast(plan.HoldoutSessions).ToArray();
        Directory.CreateDirectory(directory);
        FileStream lease;
        try { lease = new FileStream(Path.Combine(directory, "registry.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Holdout registry locked or unavailable; another research reservation may be running."); }
        using (lease)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "seal-*.json"))
            {
                var existing = JsonSerializer.Deserialize<HoldoutReservation>(File.ReadAllText(path));
                if (existing?.HoldoutDates is not { Length: > 0 })
                    throw new InvalidOperationException("Legacy holdout seal has no session provenance; review original evidence before new research. Never delete a seal to retry.");
                if (!existing.HoldoutDates.SequenceEqual(existing.HoldoutDates.Distinct().Order()))
                    throw new InvalidOperationException("Invalid existing holdout reservation; do not silently replace evidence.");
                if (existing.HoldoutDates.Intersect(holdout).Any())
                    throw new InvalidOperationException("Holdout sessions overlap previously reserved research, including other data revisions, universes or code versions. Use genuinely uninspected future sessions.");
            }
            return new EvidenceStore(directory).Save("seal", data.Hash,
                new HoldoutReservation(data.Hash, holdout, candidates, plan, costs, risk, codeVersion, mode, now));
        }
    }
}
