using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record KrxReferenceAuditSource(string Service, string Market, DateOnly Date, string SnapshotFile);
public sealed record KrxRequiredIndex(string Market, string IndexName);
public sealed record KrxReferenceAuditPlan(KrxReferenceAuditSource[] Sources, DateOnly[] ExpectedDates,
    string[] Markets, KrxRequiredIndex[] RequiredIndices);
public sealed record KrxReferenceAuditInput(string Service, string Market, DateOnly Date,
    DateTimeOffset ObservedAt, string Source, string RawHash, string SnapshotFile);
public sealed record KrxReferenceAuditDay(string Market, DateOnly Date, int? PriceRows, int? BasicRows, int? IndexRows);
public sealed record KrxReferenceAuditIssue(string Code, string? Service, string Market, DateOnly Date,
    string? Ticker, string Detail);
public sealed record KrxReferenceIdentityChange(string Market, DateOnly PreviousDate, DateOnly Date,
    string Ticker, string PreviousStandardCode, string StandardCode);
public sealed record KrxReferenceAuditReport(string Id, DateTimeOffset CreatedAt, string InputHash, string Status,
    KrxReferenceAuditInput[] Inputs, KrxReferenceAuditDay[] Days, KrxReferenceAuditIssue[] Issues,
    KrxReferenceIdentityChange[] IdentityChanges, string Note);

/// <summary>
/// Read-only comparison of selected local snapshots. Retains at most one market/date's three snapshots
/// and small prior identity maps; never collects, certifies a universe or infers corporate actions.
/// </summary>
public static class KrxReferenceAudit
{
    public const long MaximumSnapshotBytes = 100_000_000;
    public const int MaximumNormalizedRows = 100_000;
    public const int MaximumDetailedFindings = 100_000;
    private const int MaximumRawCharacters = 10_000_000;
    private static readonly string[] Services = ["DAILY", "BASIC", "INDEX"];
    private sealed record PriorIdentity(DateOnly Date, Dictionary<string, string> Codes);

    public static KrxReferenceAuditReport Run(KrxReferenceAuditPlan plan, DateTimeOffset now,
        CancellationToken ct = default)
    {
        plan = ValidatePlan(plan, now);
        var selected = plan.Sources.ToDictionary(s => (s.Service, s.Market, s.Date));
        var inputs = new List<KrxReferenceAuditInput>();
        var days = new List<KrxReferenceAuditDay>();
        var issues = new List<KrxReferenceAuditIssue>();
        var changes = new List<KrxReferenceIdentityChange>();
        var previous = new Dictionary<string, PriorIdentity>(StringComparer.Ordinal);

        void CheckFindingCapacity()
        {
            if (issues.Count + changes.Count >= MaximumDetailedFindings)
                throw new InvalidOperationException("KRX reference audit finding limit reached; audit a smaller explicit range. No completed report was produced.");
        }
        void Issue(string code, string? service, string market, DateOnly date, string? ticker, string detail)
        {
            CheckFindingCapacity();
            issues.Add(new(code, service, market, date, ticker, detail));
        }

        foreach (var date in plan.ExpectedDates)
        {
            foreach (var market in plan.Markets)
            {
                ct.ThrowIfCancellationRequested();
                KrxSnapshot? prices = null;
                KrxBasicSnapshot? basic = null;
                KrxIndexSnapshot? indices = null;
                foreach (var service in Services)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!selected.TryGetValue((service, market, date), out var source))
                    {
                        Issue("MISSING_SNAPSHOT", service, market, date, null,
                            "No selected snapshot for the requested service/date; not an inferred holiday.");
                        continue;
                    }
                    // A named file that cannot be opened is an I/O failure, not an omitted grid cell.
                    // Read and verify each snapshot before retaining its provenance in the report.
                    KrxReferenceAuditInput input;
                    int count;
                    switch (service)
                    {
                        case "DAILY":
                            prices = Read<KrxSnapshot>(source, now, Validate);
                            input = new(service, market, date, prices.ObservedAt, prices.Source, prices.RawHash, Path.GetFullPath(source.SnapshotFile));
                            count = prices.Rows.Length;
                            break;
                        case "BASIC":
                            basic = Read<KrxBasicSnapshot>(source, now, Validate);
                            input = new(service, market, date, basic.ObservedAt, basic.Source, basic.RawHash, Path.GetFullPath(source.SnapshotFile));
                            count = basic.Rows.Length;
                            break;
                        default:
                            indices = Read<KrxIndexSnapshot>(source, now, Validate);
                            input = new(service, market, date, indices.ObservedAt, indices.Source, indices.RawHash, Path.GetFullPath(source.SnapshotFile));
                            count = indices.Rows.Length;
                            break;
                    }
                    inputs.Add(input);
                    if (count == 0)
                        Issue("EMPTY_RESPONSE", service, market, date, null,
                            "Selected response has no rows; requires source review, not an inferred holiday.");
                }

                days.Add(new(market, date, prices?.Rows.Length, basic?.Rows.Length, indices?.Rows.Length));
                if (prices is { Rows.Length: > 0 } && basic is { Rows.Length: > 0 })
                {
                    var priceMap = prices.Rows.ToDictionary(row => row.Ticker, StringComparer.Ordinal);
                    var basicMap = basic.Rows.ToDictionary(row => row.Ticker, StringComparer.Ordinal);
                    foreach (var ticker in priceMap.Keys.Order(StringComparer.Ordinal))
                    {
                        if (!basicMap.TryGetValue(ticker, out var reference))
                        {
                            Issue("PRICE_WITHOUT_BASIC", null, market, date, ticker,
                                "Price issue code is absent from the selected same-market/date basic snapshot.");
                            continue;
                        }
                        var priceShares = priceMap[ticker].SharesOutstanding;
                        var basicShares = reference.ListedShares;
                        if (priceShares is null or <= 0 || basicShares is null or <= 0)
                            Issue("MISSING_SHARE_COUNT", null, market, date, ticker,
                                "At least one reported share count is missing or nonpositive; equality is unverifiable.");
                        else if (priceShares != basicShares)
                            Issue("SHARE_COUNT_DIFFERS", null, market, date, ticker,
                                "Reported price/basic share counts differ; no holder entitlement or conversion ratio is inferred.");
                    }
                    foreach (var ticker in basicMap.Keys.Except(priceMap.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                        Issue("BASIC_WITHOUT_PRICE", null, market, date, ticker,
                            "Basic issue code is absent from the selected same-market/date price snapshot.");
                }

                if (basic is { Rows.Length: > 0 })
                    foreach (var duplicate in basic.Rows.GroupBy(row => row.StandardCode, StringComparer.Ordinal)
                                 .Where(group => group.Count() > 1).OrderBy(group => group.Key, StringComparer.Ordinal))
                        Issue("DUPLICATE_STANDARD_CODE", "BASIC", market, date, null,
                            "Reported standard code " + duplicate.Key + " belongs to multiple issue codes; identity requires review.");

                if (indices is { Rows.Length: > 0 })
                {
                    foreach (var required in plan.RequiredIndices.Where(index => index.Market == market))
                    {
                        var row = indices.Rows.SingleOrDefault(index => index.IndexName == required.IndexName);
                        if (row == null)
                            Issue("REQUIRED_INDEX_MISSING", "INDEX", market, date, null,
                                "Declared named index is absent: " + required.IndexName);
                        else if (row.Close is null or <= 0)
                            Issue("REQUIRED_INDEX_INVALID_CLOSE", "INDEX", market, date, null,
                                "Declared named index has no positive reported close: " + required.IndexName);
                    }
                }

                // Do not compare identities across a missing/empty service/date in the selected grid.
                if (prices is not { Rows.Length: > 0 } || basic is not { Rows.Length: > 0 } || indices is not { Rows.Length: > 0 })
                {
                    previous.Remove(market);
                    continue;
                }
                var currentCodes = basic.Rows.ToDictionary(row => row.Ticker, row => row.StandardCode, StringComparer.Ordinal);
                if (previous.TryGetValue(market, out var prior))
                    foreach (var ticker in currentCodes.Keys.Intersect(prior.Codes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                        if (currentCodes[ticker] != prior.Codes[ticker])
                        {
                            CheckFindingCapacity();
                            changes.Add(new(market, prior.Date, date, ticker, prior.Codes[ticker], currentCodes[ticker]));
                        }
                previous[market] = new(date, currentCodes);
            }
        }
        ct.ThrowIfCancellationRequested();
        var orderedInputs = inputs.OrderBy(input => input.Market, StringComparer.Ordinal).ThenBy(input => input.Date)
            .ThenBy(input => input.Service, StringComparer.Ordinal).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Inputs = orderedInputs.Select(input => new
            {
                input.Service, input.Market, input.Date, input.ObservedAt, input.Source, input.RawHash
            }).ToArray(),
            plan.ExpectedDates, plan.Markets, plan.RequiredIndices
        })));
        return new(Guid.NewGuid().ToString("N"), now, hash,
            issues.Count == 0 && changes.Count == 0 ? "STRUCTURAL_CHECKS_PASSED_UNREVIEWED" : "REVIEW_REQUIRED",
            orderedInputs, days.ToArray(), issues.ToArray(), changes.ToArray(),
            "Selected local snapshot comparison only; no HTTP collection or automatic revision selection. " +
            "BASIC Date records the requested date; its raw body has no BAS_DD and does not attest historical publication or availability. " +
            "Expected dates are explicit past dates from 2010-01-04 onward, not a certified calendar. " +
            "Standard-code changes are observations between selected dates, not exact lifecycle dates or an ISIN certification. " +
            "Named-index presence does not validate benchmark performance, dividends or reinvestment. " +
            "Detailed daily OHLCV screening remains the separate KrxDataAudit path. " +
            "No point-in-time, industry, tradability, complete corporate-action/settlement, investment eligibility or paper-performance certification. " +
            "Missing selections are issues; unreadable selected files or invalid source evidence abort without a completed report. " +
            "Snapshot files are limited to 100000000 bytes, normalized rows per snapshot to 100000 and detailed findings to 100000; " +
            "limits abort instead of truncating.");
    }

    private static KrxReferenceAuditPlan ValidatePlan(KrxReferenceAuditPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (now == default || plan.Sources == null || plan.ExpectedDates == null || plan.Markets == null || plan.RequiredIndices == null)
            throw new ArgumentException("Explicit reference audit inputs and current observation bound are required.");
        plan = plan with
        {
            Sources = plan.Sources.ToArray(), ExpectedDates = plan.ExpectedDates.ToArray(),
            Markets = plan.Markets.ToArray(), RequiredIndices = plan.RequiredIndices.ToArray()
        };
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(9)).DateTime);
        if (plan.ExpectedDates.Length == 0 || !plan.ExpectedDates.SequenceEqual(plan.ExpectedDates.Distinct().Order()) ||
            plan.ExpectedDates.Any(date => date < new DateOnly(2010, 1, 4) || date >= today) ||
            plan.Markets.Length == 0 || plan.Markets.Distinct(StringComparer.Ordinal).Count() != plan.Markets.Length ||
            plan.Markets.Any(market => market is not ("KOSPI" or "KOSDAQ")))
            throw new ArgumentException("Reference audit requires unique supported markets and ordered explicit past dates from 2010-01-04 onward.");
        var dates = plan.ExpectedDates.ToHashSet();
        var markets = plan.Markets.ToHashSet(StringComparer.Ordinal);
        if (plan.Sources.Any(source => source == null || source.Service is not ("DAILY" or "BASIC" or "INDEX") ||
                !markets.Contains(source.Market) || !dates.Contains(source.Date) || string.IsNullOrWhiteSpace(source.SnapshotFile)) ||
            plan.Sources.GroupBy(source => (source.Service, source.Market, source.Date)).Any(group => group.Count() != 1))
            throw new ArgumentException("Reference audit sources must name one selected revision per supported service/market/date; duplicates or unrelated sources are forbidden.");
        if (plan.RequiredIndices.Any(index => index == null || !markets.Contains(index.Market) ||
                string.IsNullOrWhiteSpace(index.IndexName) || index.IndexName.Any(char.IsControl)) ||
            plan.RequiredIndices.GroupBy(index => (index.Market, index.IndexName)).Any(group => group.Count() != 1) ||
            plan.Markets.Any(market => !plan.RequiredIndices.Any(index => index.Market == market)))
            throw new ArgumentException("Declare at least one exact named index for each requested market, without duplicates.");
        return plan with
        {
            Markets = plan.Markets.Order(StringComparer.Ordinal).ToArray(),
            RequiredIndices = plan.RequiredIndices.OrderBy(index => index.Market, StringComparer.Ordinal)
                .ThenBy(index => index.IndexName, StringComparer.Ordinal).ToArray()
        };
    }

    private static T Read<T>(KrxReferenceAuditSource source, DateTimeOffset now,
        Action<T, KrxReferenceAuditSource, DateTimeOffset> validate) where T : class
    {
        using var stream = File.OpenRead(source.SnapshotFile);
        var length = stream.Length;
        if (length > MaximumSnapshotBytes)
            throw new ArgumentException("Selected KRX snapshot exceeds the reference audit file-size limit.");
        // Bound the bytes and normalized object count before typed deserialization. A small
        // JSON array of empty objects could otherwise expand into millions of row records.
        var bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new ArgumentException("Selected KRX snapshot changed size while being read.");
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
        PreflightNormalizedRows(json);
        try
        {
            var snapshot = JsonSerializer.Deserialize<T>(json) ?? throw new ArgumentException("Empty selected KRX snapshot.");
            validate(snapshot, source, now);
            return snapshot;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or
            FormatException or OverflowException or KeyNotFoundException)
        {
            throw new ArgumentException("Selected KRX reference audit snapshot has invalid source, time, raw or normalized evidence.", exception);
        }
    }

    private static void PreflightNormalizedRows(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new ArgumentException("Selected KRX snapshot must be a JSON object.");
            var foundRows = false;
            var endedObject = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    endedObject = true;
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    throw new ArgumentException("Selected KRX snapshot has an invalid top-level property.");
                var isRows = reader.ValueTextEquals("Rows");
                if (!reader.Read()) throw new ArgumentException("Selected KRX snapshot property has no value.");
                if (!isRows)
                {
                    reader.Skip();
                    continue;
                }
                if (foundRows) throw new ArgumentException("Selected KRX snapshot repeats the normalized Rows property.");
                foundRows = true;
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new ArgumentException("Selected KRX snapshot requires a normalized Rows array.");
                var count = 0;
                var endedArray = false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        endedArray = true;
                        break;
                    }
                    if (++count > MaximumNormalizedRows)
                        throw new ArgumentException("Selected KRX snapshot exceeds the normalized row limit.");
                    reader.Skip();
                }
                if (!endedArray) throw new ArgumentException("Selected KRX snapshot has an incomplete normalized Rows array.");
            }
            if (!foundRows || !endedObject || reader.Read())
                throw new ArgumentException("Selected KRX snapshot requires exactly one complete normalized Rows array.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Selected KRX snapshot has invalid JSON before normalized deserialization.", exception);
        }
    }

    private static void Validate(KrxSnapshot snapshot, KrxReferenceAuditSource selected, DateTimeOffset now)
    {
        ValidateHeader(snapshot.Id, snapshot.Market, snapshot.Date, snapshot.ObservedAt, snapshot.Source,
            snapshot.RawHash, snapshot.RawJson, selected, now);
        if (snapshot.Rows == null || !KrxClient.Parse(snapshot.RawJson, selected.Date, selected.Market).SequenceEqual(snapshot.Rows))
            throw new ArgumentException("Price raw/normalized rows differ.");
    }

    private static void Validate(KrxBasicSnapshot snapshot, KrxReferenceAuditSource selected, DateTimeOffset now)
    {
        ValidateHeader(snapshot.Id, snapshot.Market, snapshot.Date, snapshot.ObservedAt, snapshot.Source,
            snapshot.RawHash, snapshot.RawJson, selected, now);
        if (snapshot.Rows == null || !KrxReferenceClient.ParseBasicInfo(snapshot.RawJson).SequenceEqual(snapshot.Rows) ||
            snapshot.Rows.Any(row => row.MarketName != selected.Market ||
                !DateOnly.TryParseExact(row.ListingDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var listed) || listed > selected.Date))
            throw new ArgumentException("Basic raw/normalized rows, market or listing date differ.");
    }

    private static void Validate(KrxIndexSnapshot snapshot, KrxReferenceAuditSource selected, DateTimeOffset now)
    {
        ValidateHeader(snapshot.Id, snapshot.Market, snapshot.Date, snapshot.ObservedAt, snapshot.Source,
            snapshot.RawHash, snapshot.RawJson, selected, now);
        if (snapshot.Rows == null || !KrxReferenceClient.ParseIndex(snapshot.RawJson, selected.Date).SequenceEqual(snapshot.Rows) ||
            snapshot.Rows.Any(row => row.IndexClass != selected.Market))
            throw new ArgumentException("Index raw/normalized rows, market or date differ.");
    }

    private static void ValidateHeader(string id, string market, DateOnly date, DateTimeOffset observedAt,
        string source, string rawHash, string rawJson, KrxReferenceAuditSource selected, DateTimeOffset now)
    {
        var endpoint = (selected.Service, selected.Market) switch
        {
            ("DAILY", "KOSPI") => "sto/stk_bydd_trd",
            ("DAILY", "KOSDAQ") => "sto/ksq_bydd_trd",
            ("BASIC", "KOSPI") => "sto/stk_isu_base_info",
            ("BASIC", "KOSDAQ") => "sto/ksq_isu_base_info",
            ("INDEX", "KOSPI") => "idx/kospi_dd_trd",
            ("INDEX", "KOSDAQ") => "idx/kosdaq_dd_trd",
            _ => throw new ArgumentException("Unsupported selected KRX reference source.")
        };
        if (string.IsNullOrWhiteSpace(id) || market != selected.Market || date != selected.Date ||
            source != "https://data-dbg.krx.co.kr/svc/apis/" + endpoint || observedAt < Clock.Close(date) || observedAt > now ||
            rawJson == null || rawJson.Length > MaximumRawCharacters ||
            rawHash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))))
            throw new ArgumentException("KRX reference audit provenance, date, observation or raw hash differs.");
    }
}
