using System.Diagnostics;
using System.Text.Json;

namespace Investment.Core;

public sealed record DartCollectionPlan(string Label, DartFactQuery[] Queries);
public sealed record DartCollectionReceipt(string Id, string PlanHash, string CodeVersion, DateTimeOffset CreatedAt,
    string Status, string[] SnapshotFiles, DartFactQuery[] Pending, int Attempts, int RequestBudget,
    double MinimumIntervalSeconds, string? ErrorKind, int? HttpStatus = null,
    string Note = "Observed research inputs only; no historical availability certification, price returns or investment eligibility.");

public sealed class DartCollector(string directory, Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public async Task<DartCollectionReceipt> Run(DartCollectionPlan plan, int budget, TimeSpan interval, string codeVersion,
        Func<DartFactQuery, CancellationToken, Task<DartFactSnapshot>> fetch, CancellationToken ct = default)
    {
        if (plan.Label == null || plan.Label.Length is < 1 or > 64 || plan.Label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            plan.Queries == null || plan.Queries.Length is < 1 or > 50 || plan.Queries.Any(q => q == null) ||
            plan.Queries.Select(q => q.Identity).Distinct().Count() != plan.Queries.Length || string.IsNullOrWhiteSpace(codeVersion))
            throw new ArgumentException("Collection requires a label, 1..50 unique queries and code provenance.");
        plan = plan with { Queries = plan.Queries.ToArray() };
        foreach (var query in plan.Queries) query.Validate(Now);
        if (budget is < 1 or > 50 || interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromSeconds(60))
            throw new ArgumentException("Request budget must be 1..50 and interval 1..60 seconds; also respect provider quota.");
        var hash = DartFactSnapshot.Hash(JsonSerializer.Serialize(plan)); var root = Path.GetFullPath(Path.Combine(directory, hash));
        Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, "collection.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new EvidenceStore(root); var planPath = Path.Combine(root, "plan-" + hash + ".json");
        if (!File.Exists(planPath)) store.Save("plan", hash, plan);
        else if (JsonSerializer.Serialize(JsonSerializer.Deserialize<DartCollectionPlan>(File.ReadAllText(planPath))) != JsonSerializer.Serialize(plan))
            throw new InvalidOperationException("Saved OpenDART plan differs; do not overwrite.");
        var files = new List<string>(); var pending = new List<DartFactQuery>(); var requests = 0;
        var status = "REQUEST_BUDGET_REACHED"; string? error = null; int? httpStatus = null; var elapsed = new Stopwatch();
        void Validate(DartFactSnapshot snapshot, DartFactQuery query)
        {
            snapshot.Validate(Now);
            if (snapshot.Query != query) throw new ArgumentException("Collected OpenDART query differs from plan.");
        }
        for (var index = 0; index < plan.Queries.Length; index++)
        {
            var query = plan.Queries[index]; var path = Path.Combine(root, "dart-facts-" + query.Identity + ".json");
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<DartFactSnapshot>(File.ReadAllText(path)) ?? throw new ArgumentException("Empty OpenDART snapshot.");
                Validate(saved, query); files.Add(path); continue;
            }
            if (requests >= budget || ct.IsCancellationRequested)
            { if (ct.IsCancellationRequested) status = "CANCELLED"; pending.AddRange(plan.Queries[index..]); break; }
            if (elapsed.IsRunning && elapsed.Elapsed < interval)
            {
                try { await Task.Delay(interval - elapsed.Elapsed, ct); }
                catch (OperationCanceledException) { status = "CANCELLED"; pending.AddRange(plan.Queries[index..]); break; }
            }
            store.Save("dart-attempt", Guid.NewGuid().ToString("N"), new { Query = query, StartedAt = Now, CodeVersion = codeVersion });
            requests++; elapsed.Restart(); DartFactSnapshot snapshot;
            try { snapshot = await fetch(query, ct); Validate(snapshot, query); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HttpRequestException or
                OperationCanceledException or JsonException or FormatException or KeyNotFoundException or IOException)
            {
                status = ct.IsCancellationRequested ? "CANCELLED" : "REQUEST_FAILED";
                error = exception.GetType().Name; httpStatus = (exception as DartHttpException)?.HttpStatus;
                pending.AddRange(plan.Queries[index..]); break;
            }
            // Immutable per-query snapshots are published before requesting another endpoint.
            files.Add(store.Save("dart-facts", query.Identity, snapshot));
        }
        if (pending.Count == 0) status = "COLLECTED_UNREVIEWED";
        var receipt = new DartCollectionReceipt(Guid.NewGuid().ToString("N"), hash, codeVersion, Now, status,
            files.ToArray(), pending.ToArray(), requests, budget, interval.TotalSeconds, error, httpStatus);
        store.Save("dart-collection", receipt.Id, receipt); return receipt;
    }
}
