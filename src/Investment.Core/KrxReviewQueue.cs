using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record KrxReviewSourceInput(int InputIndex, string Service, string Market, DateOnly Date,
    DateTimeOffset ObservedAt, string RawHash, string? Source, JsonElement Payload);
public sealed record KrxReviewSourceReport(int ReportIndex, string Kind, string ReportDigest,
    string RawJson, string? ReportProperty, KrxReviewSourceInput[] Inputs);
public sealed record KrxReviewDaySummary(int ReportIndex, string ReportDigest, int Index, JsonElement Payload);
public sealed record SourceFlagReference(int ReportIndex, string ReportDigest, string ArrayName, int Index, JsonElement Payload);
public sealed record ReviewInputReference(int ReportIndex, int InputIndex);
public sealed record ReviewFlagLocation(int ReportIndex, string ArrayName, int Index);
public sealed record ReviewBucket(string Key, string Market, string? Ticker, DateOnly? Date,
    DateOnly FirstDate, DateOnly LastDate, string ListingEpisodeStatus,
    SourceFlagReference[] SourceFlagReferences, ReviewFlagLocation[] KnownIdentityChangeReferences,
    ReviewInputReference[] InputReferences)
{
    public string Status => "OPEN";
}
public sealed record KrxReviewQueueReport(string Id, DateTimeOffset CreatedAt, string Version,
    KrxReviewSourceReport[] Sources, KrxReviewDaySummary[] DaySummaries, ReviewBucket[] Buckets,
    string Hash, string Note)
{
    public string Status => "OPEN";
}

/// <summary>
/// Organizes existing audit reports, not underlying market evidence. A bucket is neither a security
/// identity nor a corporate action, and this operation cannot certify or close any review.
/// </summary>
public static class KrxReviewQueue
{
    public const int MaximumInputBytes = 64 * 1024 * 1024;
    public const int MaximumFlagCount = 100_000;
    public const int MaximumMetadataEntries = 100_000;
    public const int MaximumReports = 1_000;
    public const int MaximumEvidenceReferences = 1_000_000;
    public const string Version = "krx-review-queue-v1";
    private const string Note = "Report-only review organization; supplied reports are not signed or independently re-audited. " +
        "All original issue/change payloads and daily aggregates are retained. Buckets do not establish an ISIN, " +
        "listing episode, corporate action, suspension, historical publication timing, executable liquidity or review closure. " +
        "InputReferences are related selected snapshots at the flag boundaries, not proof that another report used them. " +
        "No strategy performance, point-in-time certification or promotion evidence. No underlying snapshot was read.";
    private sealed record Flag(string Market, string? Ticker, DateOnly Date, DateOnly? PreviousDate,
        SourceFlagReference Reference, bool Ambiguous);
    private sealed record Parsed(KrxReviewSourceReport Source, KrxReviewDaySummary[] Days, Flag[] Flags,
        string ReportId, string CanonicalReport);

    /// <summary>
    /// Accepts bare reports or wrappers containing a Report property. Digests bind the complete supplied
    /// UTF-8 JSON text, including wrapper metadata, report IDs and creation times. File paths are never opened.
    /// </summary>
    public static KrxReviewQueueReport Build(string[] priceAuditReportJson, string[] referenceAuditReportJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(priceAuditReportJson);
        ArgumentNullException.ThrowIfNull(referenceAuditReportJson);
        if (now == default || (long)priceAuditReportJson.Length + referenceAuditReportJson.Length is < 1 or > MaximumReports)
            throw new ArgumentException("Supply a valid review time and a bounded, nonempty report selection.");
        var selected = priceAuditReportJson.Select(raw => (Kind: "PRICE", Raw: raw))
            .Concat(referenceAuditReportJson.Select(raw => (Kind: "REFERENCE", Raw: raw))).ToArray();
        long bytes = 0;
        foreach (var item in selected)
        {
            if (string.IsNullOrWhiteSpace(item.Raw)) throw new ArgumentException("Audit report JSON is required.");
            bytes += Encoding.UTF8.GetByteCount(item.Raw);
            if (bytes > MaximumInputBytes) throw new ArgumentException("Review queue input limit exceeded; select a smaller explicit report set.");
        }
        var parsed = new List<Parsed>();
        var ids = new HashSet<(string Kind, string Id)>();
        var contents = new HashSet<string>(StringComparer.Ordinal);
        var totalFlags = 0;
        var totalMetadataEntries = 0;
        for (var index = 0; index < selected.Length; index++)
        {
            var item = Parse(selected[index].Kind, selected[index].Raw, index, now, MaximumFlagCount - totalFlags,
                MaximumMetadataEntries - totalMetadataEntries);
            if (!ids.Add((item.Source.Kind, item.ReportId)) || !contents.Add(item.CanonicalReport))
                throw new ArgumentException("The same audit report cannot be selected more than once, including through another wrapper or formatting.");
            totalFlags += item.Flags.Length;
            totalMetadataEntries += item.Source.Inputs.Length + item.Days.Length;
            if (totalFlags > MaximumFlagCount) throw new ArgumentException("Review queue finding limit exceeded; no partial queue is produced.");
            parsed.Add(item);
        }
        var sources = parsed.Select(p => p.Source).ToArray();
        ValidateSelections(sources);
        var inputLookup = sources.SelectMany(s => s.Inputs.Select(i => (s.ReportIndex, Input: i)))
            .ToLookup(x => (x.Input.Market, x.Input.Date));
        var buckets = new List<ReviewBucket>();
        var totalReferences = 0;
        foreach (var group in parsed.SelectMany(p => p.Flags)
            .GroupBy(f => (f.Market, f.Ticker, Day: f.Ticker == null ? (DateOnly?)f.Date : null)))
        {
            var flags = group.OrderBy(f => f.Reference.ReportDigest, StringComparer.Ordinal)
                .ThenBy(f => f.Reference.ArrayName, StringComparer.Ordinal).ThenBy(f => f.Reference.Index).ToArray();
            var dates = flags.SelectMany(f => f.PreviousDate is { } previous ? new[] { previous, f.Date } : [f.Date])
                .Distinct().Order().ToArray();
            var inputs = dates.SelectMany(date => inputLookup[(group.Key.Market, date)])
                .Select(x => new ReviewInputReference(x.ReportIndex, x.Input.InputIndex))
                .Distinct().OrderBy(x => x.ReportIndex).ThenBy(x => x.InputIndex).ToArray();
            totalReferences += inputs.Length;
            if (totalReferences > MaximumEvidenceReferences) throw new ArgumentException("Review queue evidence-reference limit exceeded; no partial queue is produced.");
            var references = flags.Select(f => f.Reference).ToArray();
            var key = Digest(JsonSerializer.Serialize(new
            {
                Version, group.Key.Market, group.Key.Ticker, Date = group.Key.Day,
                Flags = references.Select(f => new { f.ReportDigest, f.ArrayName, f.Index, f.Payload }).ToArray(),
                Inputs = inputs.Select(r => new
                {
                    sources[r.ReportIndex].ReportDigest,
                    sources[r.ReportIndex].Inputs[r.InputIndex].Payload
                }).OrderBy(i => i.ReportDigest, StringComparer.Ordinal)
                    .ThenBy(i => i.Payload.GetRawText(), StringComparer.Ordinal).ToArray()
            }));
            buckets.Add(new(key, group.Key.Market, group.Key.Ticker, group.Key.Day, dates[0], dates[^1],
                flags.Any(f => f.Ambiguous) ? "AMBIGUOUS" : "UNVERIFIED", references,
                references.Where(f => f.ArrayName == "IdentityChanges")
                    .Select(f => new ReviewFlagLocation(f.ReportIndex, f.ArrayName, f.Index)).ToArray(), inputs));
        }
        var orderedBuckets = buckets.OrderBy(b => b.Key, StringComparer.Ordinal).ToArray();
        var days = parsed.SelectMany(p => p.Days).ToArray();
        var hash = Digest(JsonSerializer.Serialize(new { Version, Sources = sources, DaySummaries = days, Buckets = orderedBuckets, Status = "OPEN", Note }));
        return new(Guid.NewGuid().ToString("N"), now, Version, sources, days, orderedBuckets, hash, Note);
    }

    private static Parsed Parse(string kind, string raw, int reportIndex, DateTimeOffset now, int remainingFlags, int remainingMetadataEntries)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var wrapped = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Report", out _);
            var report = wrapped ? root.GetProperty("Report") : root;
            if (report.ValueKind != JsonValueKind.Object) throw new ArgumentException("Audit report must be a JSON object.");
            // Bound both this report and the remaining whole request before canonicalization,
            // array materialization, typed deserialization or per-finding payload cloning.
            var issueCount = RequiredArrayElement(report, "Issues").GetArrayLength();
            var changeCount = RequiredArrayElement(report, kind == "PRICE" ? "Changes" : "IdentityChanges").GetArrayLength();
            if ((long)issueCount + changeCount > remainingFlags)
                throw new ArgumentException("Review queue finding limit exceeded; no partial queue is produced.");
            var inputCount = RequiredArrayElement(report, "Inputs").GetArrayLength();
            var dayCount = RequiredArrayElement(report, "Days").GetArrayLength();
            if ((long)inputCount + dayCount > remainingMetadataEntries)
                throw new ArgumentException("Review queue metadata-entry limit exceeded; no partial queue is produced.");
            // Duplicate JSON properties would give a pointer more than one possible meaning.
            Canonical(root);
            var digest = Digest(raw);
            string id;
            DateTimeOffset created;
            string inputHash;
            KrxReviewSourceInput[] inputs;
            var flags = new List<Flag>();
            var dayPayloads = RequiredArray(report, "Days");
            var inputPayloads = RequiredArray(report, "Inputs");
            var issuePayloads = RequiredArray(report, "Issues");
            if (kind == "PRICE")
            {
                var value = report.Deserialize<KrxAuditReport>() ?? throw new ArgumentException("Price audit report required.");
                id = value.Id; created = value.CreatedAt; inputHash = value.InputHash;
                var changes = RequiredArray(report, "Changes");
                if (value.Inputs == null || value.Days == null || value.Issues == null || value.Changes == null)
                    throw new ArgumentException("Complete price audit arrays are required.");
                inputs = value.Inputs.Select((i, index) => new KrxReviewSourceInput(index, "DAILY", i.Market, i.Date,
                    i.ObservedAt, i.RawHash, null, inputPayloads[index].Clone())).ToArray();
                for (var index = 0; index < value.Issues.Length; index++)
                {
                    var issue = value.Issues[index];
                    RequireText(issue.Code); RequireText(issue.Detail);
                    flags.Add(MakeFlag(issue.Market, issue.Ticker, issue.Date, null, "Issues", index, issuePayloads[index], false));
                }
                for (var index = 0; index < value.Changes.Length; index++)
                {
                    var change = value.Changes[index]; RequireText(change.Kind); RequireTicker(change.Ticker);
                    flags.Add(MakeFlag(change.Market, change.Ticker, change.Date, change.PreviousDate, "Changes", index, changes[index],
                        change.Kind is "APPEARED_REQUIRES_LISTING_EVIDENCE" or "DISAPPEARED_REQUIRES_EXIT_EVIDENCE"));
                }
                foreach (var day in value.Days)
                    if (day.Rows < 0 || day.NoTradeRows < 0 || day.NoTradeRows > day.Rows) throw new ArgumentException("Invalid price day summary.");
            }
            else
            {
                var value = report.Deserialize<KrxReferenceAuditReport>() ?? throw new ArgumentException("Reference audit report required.");
                id = value.Id; created = value.CreatedAt; inputHash = value.InputHash;
                var changes = RequiredArray(report, "IdentityChanges");
                if (value.Inputs == null || value.Days == null || value.Issues == null || value.IdentityChanges == null)
                    throw new ArgumentException("Complete reference audit arrays are required.");
                inputs = value.Inputs.Select((i, index) => new KrxReviewSourceInput(index, i.Service, i.Market, i.Date,
                    i.ObservedAt, i.RawHash, i.Source, inputPayloads[index].Clone())).ToArray();
                for (var index = 0; index < value.Issues.Length; index++)
                {
                    var issue = value.Issues[index]; RequireText(issue.Code); RequireText(issue.Detail);
                    if (issue.Service != null && issue.Service is not ("DAILY" or "BASIC" or "INDEX"))
                        throw new ArgumentException("Unknown reference service.");
                    flags.Add(MakeFlag(issue.Market, issue.Ticker, issue.Date, null, "Issues", index, issuePayloads[index], issue.Code == "DUPLICATE_STANDARD_CODE"));
                }
                for (var index = 0; index < value.IdentityChanges.Length; index++)
                {
                    var change = value.IdentityChanges[index]; RequireTicker(change.Ticker);
                    RequireText(change.PreviousStandardCode); RequireText(change.StandardCode);
                    flags.Add(MakeFlag(change.Market, change.Ticker, change.Date, change.PreviousDate, "IdentityChanges", index, changes[index], true));
                }
                foreach (var day in value.Days)
                    if (day.PriceRows < 0 || day.BasicRows < 0 || day.IndexRows < 0) throw new ArgumentException("Invalid reference day summary.");
            }
            RequireText(id); RequireHash(inputHash);
            if (created == default || created > now) throw new ArgumentException("Audit report creation time is missing or in the future.");
            if (flags.Count > MaximumFlagCount) throw new ArgumentException("Review queue finding limit exceeded.");
            foreach (var input in inputs)
            {
                RequireMarketDate(input.Market, input.Date); RequireHash(input.RawHash);
                if (input.Service is not ("DAILY" or "BASIC" or "INDEX") || input.ObservedAt < Clock.Close(input.Date) || input.ObservedAt > created)
                    throw new ArgumentException("Invalid declared audit input selection.");
                if (kind == "REFERENCE")
                {
                    var endpoint = (input.Service, input.Market) switch
                    {
                        ("DAILY", "KOSPI") => "sto/stk_bydd_trd",
                        ("DAILY", _) => "sto/ksq_bydd_trd",
                        ("BASIC", "KOSPI") => "sto/stk_isu_base_info",
                        ("BASIC", _) => "sto/ksq_isu_base_info",
                        ("INDEX", "KOSPI") => "idx/kospi_dd_trd",
                        _ => "idx/kosdaq_dd_trd"
                    };
                    if (input.Source != "https://data-dbg.krx.co.kr/svc/apis/" + endpoint)
                        throw new ArgumentException("Reference report declares an unexpected source endpoint.");
                }
            }
            if (inputs.GroupBy(i => (i.Service, i.Market, i.Date)).Any(g => g.Count() != 1))
                throw new ArgumentException("A report selects ambiguous snapshot revisions.");
            var days = dayPayloads.Select((day, index) =>
            {
                RequireMarketDate(day.GetProperty("Market").GetString(), day.GetProperty("Date").Deserialize<DateOnly>());
                return new KrxReviewDaySummary(reportIndex, digest, index, day.Clone());
            }).ToArray();
            if (days.GroupBy(d => (d.Payload.GetProperty("Market").GetString(), d.Payload.GetProperty("Date").GetString())).Any(g => g.Count() != 1))
                throw new ArgumentException("A report repeats a market/date day summary.");
            return new(new(reportIndex, kind, digest, raw, wrapped ? "Report" : null, inputs), days, flags.ToArray(), id, Canonical(report));

            Flag MakeFlag(string market, string? ticker, DateOnly date, DateOnly? previous, string array, int index, JsonElement payload, bool ambiguous)
            {
                RequireMarketDate(market, date);
                if (ticker != null) RequireTicker(ticker);
                if (previous is { } prior && (prior == default || prior >= date)) throw new ArgumentException("Invalid audit comparison boundary.");
                return new(market, ticker, date, previous, new(reportIndex, digest, array, index, payload.Clone()), ambiguous);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        {
            throw new ArgumentException("Malformed audit report; a complete queue was not produced.", error);
        }
    }

    private static void ValidateSelections(KrxReviewSourceReport[] sources)
    {
        foreach (var group in sources.SelectMany(s => s.Inputs).GroupBy(i => (i.Service, i.Market, i.Date)))
        {
            if (group.Select(i => (i.ObservedAt, i.RawHash)).Distinct().Count() != 1 ||
                group.Where(i => i.Source != null).Select(i => i.Source).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new ArgumentException("Selected audit reports disagree on a shared service/market/date snapshot; choose one revision before grouping.");
        }
    }

    private static JsonElement[] RequiredArray(JsonElement report, string name)
        => RequiredArrayElement(report, name).EnumerateArray().ToArray();
    private static JsonElement RequiredArrayElement(JsonElement report, string name)
    {
        if (!report.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Required audit array missing: " + name);
        return value;
    }
    private static void RequireMarketDate(string? market, DateOnly date)
    {
        if (market is not ("KOSPI" or "KOSDAQ") || date == default) throw new ArgumentException("An explicit supported market and date are required.");
    }
    private static void RequireTicker(string? ticker)
    {
        if (ticker == null || ticker.Length != 6 || !ticker.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("A six-character ASCII alphanumeric ticker or an explicit null market-level ticker is required.");
    }
    private static void RequireText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A required audit field is empty.");
    }
    private static void RequireHash(string? value)
    {
        if (value == null || value.Length != 64 || value.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("An uppercase SHA-256 evidence hash is required.");
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Canonical(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new ArgumentException("Duplicate JSON properties make source references ambiguous.");
            return "{" + string.Join(",", properties.Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}";
        }
        if (value.ValueKind == JsonValueKind.Array) return "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]";
        return value.GetRawText();
    }
}
