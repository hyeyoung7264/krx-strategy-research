using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record KrxAuditPlan(string[] SnapshotFiles, DateOnly[] ExpectedDates, string[] Markets);
public sealed record KrxAuditInput(string Market, DateOnly Date, DateTimeOffset ObservedAt, string RawHash);
public sealed record KrxAuditDay(string Market, DateOnly Date, int Rows, int NoTradeRows);
public sealed record KrxAuditIssue(string Code, string Market, DateOnly Date, string? Ticker, string Detail);
public sealed record KrxAuditChange(string Kind, string Market, DateOnly PreviousDate, DateOnly Date, string Ticker);
public sealed record KrxAuditReport(string Id, DateTimeOffset CreatedAt, string InputHash, string Status,
    KrxAuditInput[] Inputs, KrxAuditDay[] Days, KrxAuditIssue[] Issues, KrxAuditChange[] Changes, string Note);

/// <summary>Checks immutable snapshots and flags observations for review; never certifies a universe or infers delistings.</summary>
public static class KrxDataAudit
{
    public static KrxAuditReport Run(KrxSnapshot[] snapshots, DateOnly[] expectedDates, string[] markets, DateTimeOffset now)
    {
        if (expectedDates.Length == 0 || !expectedDates.SequenceEqual(expectedDates.Distinct().Order()) ||
            markets.Length == 0 || markets.Distinct().Count() != markets.Length || markets.Any(m => m is not ("KOSPI" or "KOSDAQ")))
            throw new ArgumentException("Audit requires ordered unique requested dates and explicit supported markets.");
        if (snapshots.GroupBy(s => (s.Market, s.Date)).Any(g => g.Count() != 1))
            throw new ArgumentException("Select one immutable snapshot revision per market/date before auditing.");
        foreach (var s in snapshots)
        {
            var endpoint = s.Market == "KOSPI" ? "stk_bydd_trd" : "ksq_bydd_trd";
            if (!markets.Contains(s.Market) || !expectedDates.Contains(s.Date) || string.IsNullOrWhiteSpace(s.Id) ||
                s.Source != "https://data-dbg.krx.co.kr/svc/apis/sto/" + endpoint ||
                s.ObservedAt < Clock.Close(s.Date) || s.ObservedAt > now ||
                s.RawHash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.RawJson))) ||
                !KrxClient.Parse(s.RawJson, s.Date, s.Market).SequenceEqual(s.Rows))
                throw new ArgumentException("Invalid KRX audit provenance or raw/normalized evidence.");
        }
        var byDay = snapshots.ToDictionary(s => (s.Market, s.Date));
        var days = new List<KrxAuditDay>(); var issues = new List<KrxAuditIssue>(); var changes = new List<KrxAuditChange>();
        foreach (var market in markets.Order(StringComparer.Ordinal))
        {
            KrxSnapshot? previous = null;
            foreach (var date in expectedDates)
            {
                if (!byDay.TryGetValue((market, date), out var current))
                {
                    issues.Add(new("MISSING_SNAPSHOT", market, date, null, "Requested date has no snapshot; not an inferred holiday."));
                    previous = null; continue;
                }
                if (current.Rows.Length == 0)
                {
                    days.Add(new(market, date, 0, 0));
                    issues.Add(new("EMPTY_RESPONSE", market, date, null, "Empty response needs a calendar/service check; never skip as a holiday."));
                    previous = null; continue;
                }
                var noTradeCount = 0;
                foreach (var row in current.Rows)
                {
                    var missing = row.Open == null || row.High == null || row.Low == null || row.Close == null || row.Volume == null || row.TradingValue == null;
                    var noTrade = row.Open == 0 && row.High == 0 && row.Low == 0 && row.Volume == 0 && row.TradingValue == 0 && row.Close > 0;
                    // KRX OPEN API FAQ 27/25: OHL covers regular-session price formation, while
                    // turnover also includes other sessions. This shape identifies a review need, not a halt or an executable price.
                    var nonRegularTurnover = row.Open == 0 && row.High == 0 && row.Low == 0 && row.Close > 0 && row.Volume > 0 && row.TradingValue > 0;
                    var priced = row.Open > 0 && row.Low > 0 && row.Close > 0 && row.High >= Math.Max(row.Open.GetValueOrDefault(), row.Close.GetValueOrDefault()) &&
                        row.Low <= Math.Min(row.Open.GetValueOrDefault(), row.Close.GetValueOrDefault()) && row.Low <= row.High;
                    if (missing) issues.Add(new("MISSING_OHLCV", market, date, row.Ticker, "Missing reported values require source review."));
                    else if (nonRegularTurnover) issues.Add(new("NON_REGULAR_TRADING_REQUIRES_REVIEW", market, date, row.Ticker,
                        "Zero regular-session OHL with positive total turnover can reflect non-regular trading. The reported close may be quotation-based or a prior price; session, suspension and execution require separate evidence."));
                    else if (!(noTrade || priced)) issues.Add(new("INVALID_OHLCV", market, date, row.Ticker,
                        "Neither a valid priced bar nor zero OHL with a positive reported close and consistent zero or positive turnover. A reported close may be quotation-based or a prior price."));
                    if (noTrade) noTradeCount++;
                    if (row.SharesOutstanding is null or <= 0)
                        issues.Add(new("MISSING_SHARE_COUNT", market, date, row.Ticker, "Share count cannot support corporate-action screening."));
                }
                days.Add(new(market, date, current.Rows.Length, noTradeCount));
                if (previous != null)
                {
                    var old = previous.Rows.ToDictionary(r => r.Ticker);
                    var next = current.Rows.ToDictionary(r => r.Ticker);
                    foreach (var ticker in next.Keys.Except(old.Keys).Order(StringComparer.Ordinal))
                        changes.Add(new("APPEARED_REQUIRES_LISTING_EVIDENCE", market, previous.Date, date, ticker));
                    foreach (var ticker in old.Keys.Except(next.Keys).Order(StringComparer.Ordinal))
                        changes.Add(new("DISAPPEARED_REQUIRES_EXIT_EVIDENCE", market, previous.Date, date, ticker));
                    foreach (var ticker in old.Keys.Intersect(next.Keys).Order(StringComparer.Ordinal))
                    {
                        var a = old[ticker]; var b = next[ticker];
                        if (a.SharesOutstanding is > 0 && b.SharesOutstanding is > 0 && a.SharesOutstanding != b.SharesOutstanding)
                            changes.Add(new("SHARE_COUNT_CHANGED_REQUIRES_REVIEW", market, previous.Date, date, ticker));
                        // A screening flag, not proof of an action or a legally permitted price-limit threshold.
                        if (a.Close is > 0 && b.Close is > 0 && Math.Abs(b.Close.Value / a.Close.Value - 1) > .35m)
                            changes.Add(new("LARGE_RAW_PRICE_MOVE_REQUIRES_REVIEW", market, previous.Date, date, ticker));
                    }
                }
                previous = current;
            }
        }
        var inputs = snapshots.OrderBy(s => s.Market, StringComparer.Ordinal).ThenBy(s => s.Date)
            .Select(s => new KrxAuditInput(s.Market, s.Date, s.ObservedAt, s.RawHash)).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Inputs = inputs, ExpectedDates = expectedDates, Markets = markets.Order(StringComparer.Ordinal).ToArray() })));
        return new(Guid.NewGuid().ToString("N"), now, hash, issues.Count == 0 && changes.Count == 0 ? "STRUCTURAL_CHECKS_PASSED_UNREVIEWED" : "REVIEW_REQUIRED",
            inputs, days.ToArray(), issues.ToArray(), changes.ToArray(),
            "Quality screening only. Requested dates are not a certified calendar; observations do not prove listing/delisting, corporate actions, industry, publication timing, or executable liquidity. No strategy performance or promotion evidence.");
    }
}
