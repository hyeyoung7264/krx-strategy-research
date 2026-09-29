using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record KrxRow(DateOnly Date, string Ticker, string Company, string Market, string ListingSection,
    decimal? Open, decimal? High, decimal? Low, decimal? Close, long? Volume, decimal? TradingValue,
    decimal? MarketCap, long? SharesOutstanding);
public sealed record KrxSnapshot(string Id, string Market, DateOnly Date, DateTimeOffset ObservedAt, string Source,
    string RawHash, string RawJson, KrxRow[] Rows);
public sealed record UniverseRow(string Ticker, DateOnly Date, string Sector, bool Member, bool Tradable,
    DateTimeOffset AvailableAt, bool CorporateAction = false);
public sealed record KrxManifest(string Version, string[] SnapshotFiles, DateOnly[] Sessions, UniverseRow[] Universe,
    bool PointInTimeReviewed = false, string ReviewEvidence = "",
    SecurityLifecycleEvent[]? LifecycleEvents = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ShareUnitChange[]? ShareUnitChanges = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ShareInventoryCredit[]? ShareInventoryCredits = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SessionHours[]? SessionHours = null);

public sealed class KrxClient(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public async Task<KrxSnapshot> Daily(string key, string market, DateOnly date, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new ArgumentException("Set KRX_API_KEY environment variable.");
        var endpoint = market switch { "KOSPI" => "stk_bydd_trd", "KOSDAQ" => "ksq_bydd_trd", _ => throw new ArgumentException("Market must be KOSPI or KOSDAQ.") };
        var today = DateOnly.FromDateTime(Now.ToOffset(TimeSpan.FromHours(9)).DateTime);
        if (date < new DateOnly(2010, 1, 4) || date >= today) throw new ArgumentException("KRX daily collection requires a past date from 2010-01-04 onward.");
        var source = "https://data-dbg.krx.co.kr/svc/apis/sto/" + endpoint;
        using var request = new HttpRequestMessage(HttpMethod.Get, source + $"?basDd={date:yyyyMMdd}");
        request.Headers.Add("AUTH_KEY", key);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"KRX HTTP status {(int)response.StatusCode}; no automatic retry.");
            var json = await response.Content.ReadAsStringAsync(ct);
            if (json.Length > 10_000_000) throw new InvalidOperationException("KRX response too large.");
            var rows = Parse(json, date, market);
            return new(Guid.NewGuid().ToString("N"), market, date, Now, source,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), json, rows);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("KRX network failure; authentication header redacted."); }
        catch (TaskCanceledException) { throw new InvalidOperationException("KRX request cancelled or timed out; authentication header redacted."); }
    }
    public static KrxRow[] Parse(string json, DateOnly expectedDate, string market)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("OutBlock_1", out var block) || block.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("KRX response missing OutBlock_1; not a valid empty session.");
        string Text(JsonElement row, string field) => row.GetProperty(field).GetString() ?? "";
        decimal? Number(JsonElement row, string field)
        {
            var value = Text(row, field).Trim();
            if (value is "" or "-") return null;
            var number = decimal.Parse(value, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            if (number < 0) throw new ArgumentException("Negative KRX market data."); return number;
        }
        long? Count(JsonElement row, string field)
        {
            var number = Number(row, field); if (number == null) return null;
            if (number.Value != decimal.Truncate(number.Value) || number.Value > long.MaxValue) throw new ArgumentException("Invalid KRX integer quantity."); return (long)number.Value;
        }
        var rows = block.EnumerateArray().Select(row =>
        {
            var date = DateOnly.ParseExact(Text(row, "BAS_DD"), "yyyyMMdd", CultureInfo.InvariantCulture);
            var ticker = Text(row, "ISU_CD");
            // KRX short issue codes can contain alphabetic characters; do not coerce them to integers.
            if (date != expectedDate || ticker.Length != 6 || !ticker.All(char.IsAsciiLetterOrDigit) || Text(row, "MKT_NM") != market) throw new ArgumentException("KRX date/ticker/market mismatch.");
            return new KrxRow(date, ticker, Text(row, "ISU_NM"), market, Text(row, "SECT_TP_NM"), Number(row, "TDD_OPNPRC"),
                Number(row, "TDD_HGPRC"), Number(row, "TDD_LWPRC"), Number(row, "TDD_CLSPRC"), Count(row, "ACC_TRDVOL"),
                Number(row, "ACC_TRDVAL"), Number(row, "MKTCAP"), Count(row, "LIST_SHRS"));
        }).ToArray();
        if (rows.Select(r => r.Ticker).Distinct().Count() != rows.Length) throw new ArgumentException("KRX duplicate issue rows.");
        return rows;
    }
}
public static class KrxDatasetBuilder
{
    public static Dataset Build(KrxSnapshot[] snapshots, KrxManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Sessions.Length == 0 || manifest.Universe.Length == 0) throw new ArgumentException("Explicit version, sessions and historical universe required.");
        if (manifest.PointInTimeReviewed && string.IsNullOrWhiteSpace(manifest.ReviewEvidence)) throw new ArgumentException("Point-in-time review evidence reference required.");
        var rows = snapshots.SelectMany(s =>
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.RawJson)));
            if (hash != s.RawHash || !KrxClient.Parse(s.RawJson, s.Date, s.Market).SequenceEqual(s.Rows)) throw new ArgumentException("KRX raw/normalized snapshot mismatch.");
            return s.Rows;
        }).ToArray();
        if (rows.GroupBy(r => (r.Ticker, r.Date)).Any(g => g.Count() != 1)) throw new ArgumentException("Overlapping KRX snapshots; select one immutable revision per session.");
        if (manifest.Universe.GroupBy(r => (r.Ticker, r.Date)).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate historical universe entry.");
        var byKey = rows.ToDictionary(r => (r.Ticker, r.Date));
        var bars = manifest.Universe.Select(u =>
        {
            if (!byKey.TryGetValue((u.Ticker, u.Date), out var row)) throw new ArgumentException("Universe security missing in KRX; do not drop suspended/delisted securities.");
            var noTrade = row.Open == 0 && row.High == 0 && row.Low == 0 && row.Volume == 0 && row.TradingValue == 0;
            var priced = row.Open is > 0 && row.High is > 0 && row.Low is > 0;
            if (row.Close is null or <= 0 || row.Volume == null || row.TradingValue == null || !(priced || noTrade))
                throw new ArgumentException("KRX missing prices need separately reviewed suspension/corporate-action handling; never invent OHLC.");
            var observed = snapshots.Single(s => s.Date == u.Date && s.Market == row.Market).ObservedAt;
            var usable = manifest.PointInTimeReviewed ? u.AvailableAt : observed > u.AvailableAt ? observed : u.AvailableAt;
            // SECT_TP_NM is a listing section, not an industry. Sector comes from historical universe evidence.
            return new Bar(u.Ticker, u.Sector, u.Date, usable, row.Open.GetValueOrDefault(), row.High.GetValueOrDefault(), row.Low.GetValueOrDefault(), row.Close.Value,
                row.Volume.Value, row.TradingValue.Value, u.Tradable, u.Member, u.CorporateAction);
        }).OrderBy(b => b.Date).ThenBy(b => b.Ticker, StringComparer.Ordinal).ToArray();
        var data = new Dataset("KRX approved API; manifest=" + manifest.Version + "; review=" + manifest.ReviewEvidence, false,
            manifest.PointInTimeReviewed, bars, manifest.Sessions, manifest.LifecycleEvents,
            manifest.ShareUnitChanges, manifest.ShareInventoryCredits, manifest.SessionHours);
        data.Validate(); return data;
    }
}
