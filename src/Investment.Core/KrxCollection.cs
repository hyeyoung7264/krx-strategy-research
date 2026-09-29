using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Investment.Core;

public sealed record KrxCollectionPlan(string Market, DateOnly[] Dates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Service = null);
public sealed record KrxCollectionReceipt(string Id, string PlanHash, DateTimeOffset CreatedAt, string Status,
    string[] SnapshotFiles, DateOnly[] PendingDates, DateOnly? StoppedDate, string? ErrorKind,
    int Attempts, int RequestBudget, double MinimumIntervalSeconds);

/// <summary>Collects explicit requested dates; neither infers sessions nor certifies historical availability.</summary>
public sealed class KrxCollector(string directory, Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public Task<KrxCollectionReceipt> Run(KrxCollectionPlan plan, int requestBudget, TimeSpan minimumInterval,
        Func<string, DateOnly, CancellationToken, Task<KrxSnapshot>> fetch, CancellationToken ct = default)
        => RunCore(plan, requestBudget, minimumInterval, fetch, null, "krx-raw", ValidateSnapshot, s => s.Rows.Length, ct);

    public Task<KrxCollectionReceipt> RunBasic(KrxCollectionPlan plan, int requestBudget, TimeSpan minimumInterval,
        Func<string, DateOnly, CancellationToken, Task<KrxBasicSnapshot>> fetch, CancellationToken ct = default)
        => RunCore(plan, requestBudget, minimumInterval, fetch, "BASIC", "krx-basic-raw", ValidateSnapshot, s => s.Rows.Length, ct);

    public Task<KrxCollectionReceipt> RunIndex(KrxCollectionPlan plan, int requestBudget, TimeSpan minimumInterval,
        Func<string, DateOnly, CancellationToken, Task<KrxIndexSnapshot>> fetch, CancellationToken ct = default)
        => RunCore(plan, requestBudget, minimumInterval, fetch, "INDEX", "krx-index-raw", ValidateSnapshot, s => s.Rows.Length, ct);

    private async Task<KrxCollectionReceipt> RunCore<TSnapshot>(KrxCollectionPlan plan, int requestBudget, TimeSpan minimumInterval,
        Func<string, DateOnly, CancellationToken, Task<TSnapshot>> fetch, string? service, string prefix,
        Action<TSnapshot, string, DateOnly, DateTimeOffset> validate, Func<TSnapshot, int> rowCount,
        CancellationToken ct) where TSnapshot : class
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fetch);
        // The default must serialize exactly like legacy { Market, Dates } plans.
        // Normalize only the cloned execution plan, never the caller's mutable date array.
        plan = CanonicalPlan(plan);
        if (plan.Service != service) throw new ArgumentException("KRX plan service differs from the typed collector.");
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
                if (saved == null || !JsonSerializer.SerializeToUtf8Bytes(CanonicalPlan(saved)).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan)))
                    throw new InvalidOperationException("Saved collection plan differs; never overwrite collection evidence.");
            }
            else store.Save("plan", planHash, plan);

            string SnapshotPath(DateOnly date) => Path.Combine(root, prefix + "-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json");
            var snapshots = new Dictionary<DateOnly, TSnapshot>();
            foreach (var date in plan.Dates)
            {
                var path = SnapshotPath(date);
                if (!File.Exists(path)) continue;
                var saved = JsonSerializer.Deserialize<TSnapshot>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Empty cached KRX evidence.");
                validate(saved, plan.Market, date, Now);
                snapshots.Add(date, saved);
            }

            var requests = 0; var status = "REQUEST_BUDGET_REACHED"; DateOnly? stopped = null; string? error = null;
            var sinceRequest = new Stopwatch();
            foreach (var date in plan.Dates)
            {
                if (snapshots.TryGetValue(date, out var saved))
                {
                    if (rowCount(saved) == 0) { status = "EMPTY_RESPONSE_REQUIRES_REVIEW"; stopped = date; break; }
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
                TSnapshot snapshot;
                requests++; sinceRequest.Restart();
                try
                {
                    snapshot = await fetch(plan.Market, date, ct);
                    if (snapshot is null) throw new InvalidOperationException("Missing KRX snapshot.");
                    validate(snapshot, plan.Market, date, Now);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or
                    OperationCanceledException or JsonException or FormatException or OverflowException or KeyNotFoundException)
                {
                    status = ct.IsCancellationRequested ? "CANCELLED" : "REQUEST_FAILED";
                    stopped = date; error = ex.GetType().Name; break; // No exception text, key, or automatic retry.
                }
                // Publish each raw snapshot before requesting another date. Filesystem failures propagate.
                store.Save(prefix, date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), snapshot);
                snapshots.Add(date, snapshot);
                if (rowCount(snapshot) == 0) { status = "EMPTY_RESPONSE_REQUIRES_REVIEW"; stopped = date; break; }
            }
            var pending = plan.Dates.Where(d => !snapshots.TryGetValue(d, out var snapshot) || rowCount(snapshot) == 0).ToArray();
            if (pending.Length == 0) status = "COLLECTED_UNREVIEWED";
            var receipt = new KrxCollectionReceipt(Guid.NewGuid().ToString("N"), planHash, Now, status,
                plan.Dates.Where(snapshots.ContainsKey).Select(SnapshotPath).ToArray(), pending, stopped, error,
                requests, requestBudget, minimumInterval.TotalSeconds);
            store.Save("collection", receipt.Id, receipt);
            return receipt;
        }
    }
    private static KrxCollectionPlan CanonicalPlan(KrxCollectionPlan plan) => plan with
    {
        Dates = plan.Dates?.ToArray()!,
        Service = plan.Service switch
        {
            null or "DAILY" => null,
            "BASIC" => "BASIC",
            "INDEX" => "INDEX",
            _ => throw new ArgumentException("KRX collection service must be DAILY, BASIC or INDEX.")
        }
    };

    private static void ValidateSnapshot(KrxSnapshot snapshot, string market, DateOnly date, DateTimeOffset now)
    {
        var endpoint = market == "KOSPI" ? "sto/stk_bydd_trd" : "sto/ksq_bydd_trd";
        ValidateProvenance(snapshot.Id, snapshot.Market, snapshot.Date, snapshot.ObservedAt, snapshot.Source,
            snapshot.RawHash, snapshot.RawJson, market, date, now, endpoint);
        if (snapshot.Rows is null || !KrxClient.Parse(snapshot.RawJson, date, market).SequenceEqual(snapshot.Rows))
            throw new InvalidOperationException("KRX raw/normalized rows differ; do not overwrite or silently refetch.");
    }

    private static void ValidateSnapshot(KrxBasicSnapshot snapshot, string market, DateOnly date, DateTimeOffset now)
    {
        var endpoint = market == "KOSPI" ? "sto/stk_isu_base_info" : "sto/ksq_isu_base_info";
        ValidateProvenance(snapshot.Id, snapshot.Market, snapshot.Date, snapshot.ObservedAt, snapshot.Source,
            snapshot.RawHash, snapshot.RawJson, market, date, now, endpoint);
        // Basic rows do not echo BAS_DD. Date remains the recorded request date, not proof
        // that this historical version or its listing information was available at that time.
        if (snapshot.Rows is null || !KrxReferenceClient.ParseBasicInfo(snapshot.RawJson).SequenceEqual(snapshot.Rows) ||
            snapshot.Rows.Any(row => row.MarketName != market ||
                !DateOnly.TryParseExact(row.ListingDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var listed) || listed > date))
            throw new InvalidOperationException("KRX basic rows, market or listing date differ; do not overwrite or silently refetch.");
    }

    private static void ValidateSnapshot(KrxIndexSnapshot snapshot, string market, DateOnly date, DateTimeOffset now)
    {
        var endpoint = market == "KOSPI" ? "idx/kospi_dd_trd" : "idx/kosdaq_dd_trd";
        ValidateProvenance(snapshot.Id, snapshot.Market, snapshot.Date, snapshot.ObservedAt, snapshot.Source,
            snapshot.RawHash, snapshot.RawJson, market, date, now, endpoint);
        if (snapshot.Rows is null || !KrxReferenceClient.ParseIndex(snapshot.RawJson, date).SequenceEqual(snapshot.Rows) ||
            snapshot.Rows.Any(row => row.IndexClass != market))
            throw new InvalidOperationException("KRX index rows, market or date differ; do not overwrite or silently refetch.");
    }

    private static void ValidateProvenance(string id, string actualMarket, DateOnly actualDate, DateTimeOffset observedAt,
        string source, string rawHash, string rawJson, string market, DateOnly date, DateTimeOffset now, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(id) || actualMarket != market || actualDate != date ||
            source != "https://data-dbg.krx.co.kr/svc/apis/" + endpoint ||
            observedAt < Clock.Close(date) || observedAt > now || rawJson is null ||
            rawHash != Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawJson))))
            throw new InvalidOperationException("KRX snapshot provenance, date, raw hash or normalized rows differ; do not overwrite or silently refetch.");
    }
}
