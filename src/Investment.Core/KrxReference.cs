using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record KrxBasicRow(string StandardCode, string Ticker, string Name, string ListingDate,
    string MarketName, string SecurityGroup, string ListingSection, string StockType, long? ListedShares);
public sealed record KrxBasicSnapshot(string Id, string Market, DateOnly Date, DateTimeOffset ObservedAt,
    string Source, string RawHash, string RawJson, KrxBasicRow[] Rows);
public sealed record KrxIndexRow(DateOnly Date, string IndexClass, string IndexName, decimal? Open,
    decimal? High, decimal? Low, decimal? Close, long? Volume, decimal? TradingValue);
public sealed record KrxIndexSnapshot(string Id, string Market, DateOnly Date, DateTimeOffset ObservedAt,
    string Source, string RawHash, string RawJson, KrxIndexRow[] Rows);

/// <summary>Read-only official reference data. Every service requires its own KRX utilization approval.</summary>
public sealed class KrxReferenceClient(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task<KrxBasicSnapshot> BasicInfo(string key, string market, DateOnly date, CancellationToken ct = default)
    {
        var endpoint = market switch
        {
            "KOSPI" => "sto/stk_isu_base_info",
            "KOSDAQ" => "sto/ksq_isu_base_info",
            _ => throw new ArgumentException("Market must be KOSPI or KOSDAQ.")
        };
        var (source, json, hash, observed) = await Fetch(key, endpoint, date, ct);
        var rows = ParseBasicInfo(json);
        if (rows.Any(row => row.MarketName != market ||
            !DateOnly.TryParseExact(row.ListingDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var listed) || listed > date))
            throw new ArgumentException("KRX basic security market/listing date mismatch.");
        return new(Guid.NewGuid().ToString("N"), market, date, observed, source, hash, json, rows);
    }

    public async Task<KrxIndexSnapshot> IndexDaily(string key, string market, DateOnly date, CancellationToken ct = default)
    {
        var endpoint = market switch
        {
            "KOSPI" => "idx/kospi_dd_trd",
            "KOSDAQ" => "idx/kosdaq_dd_trd",
            _ => throw new ArgumentException("Market must be KOSPI or KOSDAQ.")
        };
        var (source, json, hash, observed) = await Fetch(key, endpoint, date, ct);
        var rows = ParseIndex(json, date);
        if (rows.Any(row => row.IndexClass != market)) throw new ArgumentException("KRX index market mismatch.");
        return new(Guid.NewGuid().ToString("N"), market, date, observed, source, hash, json, rows);
    }

    private async Task<(string Source, string Json, string Hash, DateTimeOffset Observed)> Fetch(
        string key, string endpoint, DateOnly date, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new ArgumentException("Set KRX_API_KEY environment variable.");
        var today = DateOnly.FromDateTime(Now.ToOffset(TimeSpan.FromHours(9)).DateTime);
        if (date < new DateOnly(2010, 1, 4) || date >= today)
            throw new ArgumentException("KRX reference collection requires a past date from 2010-01-04 onward.");
        var source = "https://data-dbg.krx.co.kr/svc/apis/" + endpoint;
        using var request = new HttpRequestMessage(HttpMethod.Get, source + $"?basDd={date:yyyyMMdd}");
        request.Headers.Add("AUTH_KEY", key);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException($"KRX HTTP status {(int)response.StatusCode}; no automatic retry.");
            var json = await response.Content.ReadAsStringAsync(ct);
            if (json.Length > 10_000_000) throw new InvalidOperationException("KRX response too large.");
            return (source, json, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), Now);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("KRX network failure; authentication header redacted."); }
        catch (TaskCanceledException) { throw new InvalidOperationException("KRX request cancelled or timed out; authentication header redacted."); }
    }

    private static JsonElement Rows(string json, out JsonDocument document)
    {
        document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("OutBlock_1", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new InvalidOperationException("KRX response missing OutBlock_1; not a valid empty session.");
        }
        return rows;
    }

    private static string Text(JsonElement row, string field) => row.GetProperty(field).GetString()?.Trim() ?? "";

    private static decimal? Number(JsonElement row, string field)
    {
        var text = Text(row, field);
        if (text is "" or "-") return null;
        var value = decimal.Parse(text, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (value < 0) throw new ArgumentException("Negative KRX reference data.");
        return value;
    }

    private static long? Whole(JsonElement row, string field)
    {
        var value = Number(row, field);
        if (value == null) return null;
        if (value != decimal.Truncate(value.Value) || value > long.MaxValue)
            throw new ArgumentException("Invalid KRX integer quantity.");
        return (long)value.Value;
    }

    public static KrxBasicRow[] ParseBasicInfo(string json)
    {
        var rows = Rows(json, out var document);
        using (document)
        {
            var result = rows.EnumerateArray().Select(row =>
            {
                var ticker = Text(row, "ISU_SRT_CD");
                var standard = Text(row, "ISU_CD");
                if (ticker.Length != 6 || !ticker.All(char.IsAsciiLetterOrDigit) || standard.Length == 0)
                    throw new ArgumentException("Invalid KRX basic security code.");
                return new KrxBasicRow(standard, ticker, Text(row, "ISU_NM"), Text(row, "LIST_DD"),
                    Text(row, "MKT_TP_NM"), Text(row, "SECUGRP_NM"), Text(row, "SECT_TP_NM"),
                    Text(row, "KIND_STKCERT_TP_NM"), Whole(row, "LIST_SHRS"));
            }).ToArray();
            if (result.Select(r => r.Ticker).Distinct(StringComparer.Ordinal).Count() != result.Length)
                throw new ArgumentException("KRX duplicate basic security rows.");
            return result;
        }
    }

    public static KrxIndexRow[] ParseIndex(string json, DateOnly expectedDate)
    {
        var rows = Rows(json, out var document);
        using (document)
        {
            var result = rows.EnumerateArray().Select(row =>
            {
                var date = DateOnly.ParseExact(Text(row, "BAS_DD"), "yyyyMMdd", CultureInfo.InvariantCulture);
                var name = Text(row, "IDX_NM");
                if (date != expectedDate || name.Length == 0) throw new ArgumentException("KRX index date/name mismatch.");
                return new KrxIndexRow(date, Text(row, "IDX_CLSS"), name, Number(row, "OPNPRC_IDX"),
                    Number(row, "HGPRC_IDX"), Number(row, "LWPRC_IDX"), Number(row, "CLSPRC_IDX"),
                    Whole(row, "ACC_TRDVOL"), Number(row, "ACC_TRDVAL"));
            }).ToArray();
            if (result.GroupBy(r => (r.IndexClass, r.IndexName)).Any(g => g.Count() != 1))
                throw new ArgumentException("KRX duplicate index rows.");
            return result;
        }
    }
}
