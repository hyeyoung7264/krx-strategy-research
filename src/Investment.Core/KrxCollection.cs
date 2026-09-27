using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Investment.Core;

public sealed record KrxCollectionPlan(string Market, DateOnly[] Dates);
public sealed record KrxCollectionReceipt(string Id, string PlanHash, DateTimeOffset CreatedAt, string Status,
    string[] SnapshotFiles, DateOnly[] PendingDates, DateOnly? StoppedDate, string? ErrorKind,
    int Attempts, int RequestBudget, double MinimumIntervalSeconds);

/// <summary>Collects explicit requested dates; neither infers sessions nor certifies historical availability.</summary>
public sealed class KrxCollector(string directory, Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public async Task<KrxCollectionReceipt> Run(KrxCollectionPlan plan, int requestBudget, TimeSpan minimumInterval,
        Func<string, DateOnly, CancellationToken, Task<KrxSnapshot>> fetch, CancellationToken ct = default)
    {
        plan = plan with { Dates = plan.Dates?.ToArray()! };
        var today = DateOnly.FromDateTime(Now.ToOffset(TimeSpan.FromHours(9)).DateTime);
        if (plan.Market is not ("KOSPI" or "KOSDAQ") || plan.Dates is null || plan.Dates.Length == 0 ||
            !plan.Dates.SequenceEqual(plan.Dates.Distinct().Order()) ||
            plan.Dates.Any(d => d < new DateOnly(2010, 1, 4) || d >= today))
            throw new ArgumentException("Collection requires KOSPI/KOSDAQ and unique ascending explicit past dates from 2010-01-04 onward.");
        // Conservative local throttle, not a claim about the provider's approved quota.
        if (requestBudget is < 1 or > 1000 || minimumInterval < TimeSpan.FromSeconds(1) || minimumInterval > TimeSpan.FromSeconds(60))
            throw new ArgumentException("Request budget must be 1..1000 and minimum request interval 1..60 seconds; also respect your approved provider quota.");
        var planHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
        var root = Path.GetFullPath(Path.Combine(directory, planHash));
        Directory.CreateDirectory(root);
        FileStream lease;
        try { lease = new FileStream(Path.Combine(root, "collection.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Collection directory is locked or unavailable; do not run concurrent collectors for this plan."); }
        using (lease)
        {
            var store = new EvidenceStore(root);
            var planFile = Path.Combine(root, "plan-" + planHash + ".json");
            if (File.Exists(planFile))
            {
                var saved = JsonSerializer.Deserialize<KrxCollectionPlan>(File.ReadAllText(planFile));
                if (saved == null || !JsonSerializer.SerializeToUtf8Bytes(saved).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan)))
                    throw new InvalidOperationException("Saved collection plan differs; never overwrite collection evidence.");
            }
            else store.Save("plan", planHash, plan);

            string SnapshotPath(DateOnly date) => Path.Combine(root, "krx-raw-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json");
            var snapshots = new Dictionary<DateOnly, KrxSnapshot>();
            foreach (var date in plan.Dates)
            {
                var path = SnapshotPath(date);
                if (!File.Exists(path)) continue;
                var saved = JsonSerializer.Deserialize<KrxSnapshot>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Empty cached KRX evidence.");
                ValidateSnapshot(saved, plan.Market, date, Now);
                snapshots.Add(date, saved);
            }

            var requests = 0; var status = "REQUEST_BUDGET_REACHED"; DateOnly? stopped = null; string? error = null;
            var sinceRequest = new Stopwatch();
            foreach (var date in plan.Dates)
            {
                if (snapshots.TryGetValue(date, out var saved))
                {
                    if (saved.Rows.Length == 0) { status = "EMPTY_RESPONSE_REQUIRES_REVIEW"; stopped = date; break; }
                    continue;
                }
                if (requests >= requestBudget) { stopped = date; break; }
                if (ct.IsCancellationRequested) { status = "CANCELLED"; stopped = date; break; }
                var remaining = minimumInterval - sinceRequest.Elapsed;
                if (sinceRequest.IsRunning && remaining > TimeSpan.Zero)
                {
                    try { await Task.Delay(remaining, ct); }
                    catch (OperationCanceledException) { status = "CANCELLED"; stopped = date; break; }
                }
                KrxSnapshot snapshot;
                requests++; sinceRequest.Restart();
                try
                {
                    snapshot = await fetch(plan.Market, date, ct);
                    ValidateSnapshot(snapshot, plan.Market, date, Now);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or
                    OperationCanceledException or JsonException or FormatException or KeyNotFoundException)
                {
                    status = ct.IsCancellationRequested ? "CANCELLED" : "REQUEST_FAILED";
                    stopped = date; error = ex.GetType().Name; break; // No exception text, key, or automatic retry.
                }
                // Publish each raw snapshot before requesting another date. Filesystem failures propagate.
                store.Save("krx-raw", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), snapshot);
                snapshots.Add(date, snapshot);
                if (snapshot.Rows.Length == 0) { status = "EMPTY_RESPONSE_REQUIRES_REVIEW"; stopped = date; break; }
            }
            var pending = plan.Dates.Where(d => !snapshots.TryGetValue(d, out var snapshot) || snapshot.Rows.Length == 0).ToArray();
            if (pending.Length == 0) status = "COLLECTED_UNREVIEWED";
            var receipt = new KrxCollectionReceipt(Guid.NewGuid().ToString("N"), planHash, Now, status,
                plan.Dates.Where(snapshots.ContainsKey).Select(SnapshotPath).ToArray(), pending, stopped, error,
                requests, requestBudget, minimumInterval.TotalSeconds);
            store.Save("collection", receipt.Id, receipt);
            return receipt;
        }
    }
    private static void ValidateSnapshot(KrxSnapshot snapshot, string market, DateOnly date, DateTimeOffset now)
    {
        var endpoint = market == "KOSPI" ? "stk_bydd_trd" : "ksq_bydd_trd";
        if (string.IsNullOrWhiteSpace(snapshot.Id) || snapshot.Market != market || snapshot.Date != date ||
            snapshot.Source != "https://data-dbg.krx.co.kr/svc/apis/sto/" + endpoint ||
            snapshot.ObservedAt < Clock.Close(date) || snapshot.ObservedAt > now ||
            snapshot.RawHash != Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot.RawJson))) ||
            !KrxClient.Parse(snapshot.RawJson, date, market).SequenceEqual(snapshot.Rows))
            throw new InvalidOperationException("KRX snapshot provenance, date, raw hash or normalized rows differ; do not overwrite or silently refetch.");
    }
}
