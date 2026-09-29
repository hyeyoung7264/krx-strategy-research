using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Investment.Core;

// One atomically published state transition is the authoritative commit. Export files are recoverable copies.
public sealed class PaperJournal(string directory)
{
    private readonly EvidenceStore store = new(directory);
    public PaperState Start(PaperState state)
    {
        ValidateGenesis(state);
        store.Save("state", Key(state.SessionId, 0), state); return state;
    }
    public PaperState Commit(PaperState before, Observation observation, DateTimeOffset now, string codeVersion)
    {
        Verify(before);
        if (before.CodeVersion != codeVersion) throw new ArgumentException("Paper code changed; revalidate before a new session, never silently change a running strategy.");
        if (File.Exists(PathFor(before.SessionId, before.Audit.Length + 1))) throw new InvalidOperationException("Paper state already consumed; recover its committed successor.");
        var after = PaperEngine.Step(before, observation, now);
        // Concurrent writers may compute; CreateNew publication permits exactly one successor.
        store.Save("state", Key(before.SessionId, after.Audit.Length), after);
        return after;
    }
    public PaperState Recover(PaperState before)
    {
        Verify(before); return Read(before.SessionId, before.Audit.Length + 1);
    }
    public Evaluation Evaluate(PaperState state, string codeVersion)
    {
        Verify(state);
        if (state.CodeVersion != codeVersion) throw new ArgumentException("Paper evaluation code differs; restore and rebuild the validated source before review.");
        if (File.Exists(PathFor(state.SessionId, state.Audit.Length + 1))) throw new ArgumentException("Paper evaluation requires the latest committed state; an earlier snapshot cannot hide later losses or a halt.");
        var previous = Read(state.SessionId, 0);
        ValidateGenesis(previous);
        for (var sequence = 1; sequence <= state.Audit.Length; sequence++)
        {
            var recorded = Read(state.SessionId, sequence);
            var observation = recorded.Audit[^1].Observation;
            // Replay validates accounting only. It publishes nothing and creates no new forward evidence.
            var recomputed = PaperEngine.Step(previous, observation, observation.ObservedAt);
            if (Digest(recomputed) != Digest(recorded)) throw new ArgumentException("Paper journal replay differs from committed trades, accounting or audit; evaluation refused.");
            previous = recorded;
        }
        var evaluation = PaperEngine.EvaluateCommitted(previous);
        if (File.Exists(PathFor(state.SessionId, state.Audit.Length + 1))) throw new ArgumentException("Paper state advanced during evaluation; evaluate its latest committed successor.");
        return evaluation;
    }
    private static void ValidateGenesis(PaperState state)
    {
        if (state.Audit.Length != 0 || string.IsNullOrWhiteSpace(state.CodeVersion) || state.InitialCapital <= 0 ||
            state.ResearchCandidateCount < Math.Max(2, state.Strategies.Length) || state.Strategies.Length == 0 ||
            state.Positions.Length != 0 || state.Fills.Length != 0 || state.Equity.Length != 0 || state.Turnover != 0 ||
            state.ShareUnitAdjustments is { Length: > 0 } ||
            state.TradingDate != null || state.Cash != state.InitialCapital || state.Peak != state.InitialCapital || state.DayStartEquity != state.InitialCapital)
            throw new ArgumentException("New paper session requires versioned genesis, frozen research candidate count and empty trading ledger.");
        state.Costs.Validate(); state.Risk.Validate();
        ShareUnits.Validate(state.ShareUnitChanges, state.ShareInventoryCredits, state.History, state.LifecycleEvents);
        if ((state.ShareUnitChanges ?? []).Any(c => c.AvailableAt > state.LastObservation) ||
            (state.ShareInventoryCredits ?? []).Any(c => c.AvailableAt > state.LastObservation))
            throw new ArgumentException("Paper genesis cannot contain unobserved share-unit or inventory evidence.");
        foreach (var strategy in state.Strategies) strategy.Validate();
    }
    private void Verify(PaperState state)
    {
        var committed = Read(state.SessionId, state.Audit.Length);
        if (Digest(committed) != Digest(state)) throw new ArgumentException("Paper input snapshot differs from committed state; balance, evidence and rules cannot be edited.");
    }
    private PaperState Read(string session, int sequence)
    {
        var path = PathFor(session, sequence);
        if (!File.Exists(path)) throw new ArgumentException("Paper journal state missing; no committed successor to recover.");
        var state = JsonSerializer.Deserialize<PaperState>(File.ReadAllText(path)) ?? throw new ArgumentException("Invalid paper journal.");
        if (state.SessionId != session || state.Audit.Length != sequence) throw new ArgumentException("Paper journal identity/sequence mismatch.");
        return state;
    }
    private string PathFor(string session, int sequence) => Path.GetFullPath(Path.Combine(directory, "state-" + Key(session, sequence) + ".json"));
    private static string Key(string session, int sequence)
    {
        if (string.IsNullOrWhiteSpace(session) || session.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || sequence < 0) throw new ArgumentException("Invalid paper journal identifier.");
        return session + "-" + sequence.ToString(CultureInfo.InvariantCulture);
    }
    private static string Digest(PaperState state) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)));
}
