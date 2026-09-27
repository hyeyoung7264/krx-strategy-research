using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record Disclosure(string ReceiptNumber, string CorpCode, string Company, string StockCode,
    string ReportName, DateOnly ReceiptDate, DateTimeOffset ObservedAt, DateTimeOffset UsableAt);
public sealed record DartPage(string Hash, string Json, DateTimeOffset ObservedAt);
public sealed record DisclosureBatch(string Id, DateOnly Start, DateOnly End, Disclosure[] Disclosures, DartPage[] Pages,
    string Source = "https://opendart.fss.or.kr/api/list.json");
public sealed record CompanySnapshot(string CorpCode, string Company, string StockCode, DateTimeOffset ObservedAt, string RawJson);

public sealed class OpenDartClient(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public async Task<DisclosureBatch> Search(string apiKey, DateOnly start, DateOnly end, string? corpCode = null, CancellationToken ct = default)
    {
        Key(apiKey);
        if (start > end || end > DateOnly.FromDateTime(Now.ToOffset(TimeSpan.FromHours(9)).DateTime) || end.DayNumber - start.DayNumber > 90)
            throw new ArgumentException("Use a past interval of at most 90 days.");
        if (corpCode != null && (corpCode.Length != 8 || !corpCode.All(char.IsAsciiDigit))) throw new ArgumentException("corp_code must be eight digits.");
        var pages = new List<DartPage>(); var disclosures = new List<Disclosure>(); int totalPages = 1, expectedCount = -1;
        for (var page = 1; page <= totalPages; page++)
        {
            var query = $"crtfc_key={Uri.EscapeDataString(apiKey)}&bgn_de={start:yyyyMMdd}&end_de={end:yyyyMMdd}&last_reprt_at=N&sort=date&sort_mth=asc&page_count=100&page_no={page}";
            if (corpCode != null) query += "&corp_code=" + corpCode;
            var json = await Get("list.json", query, ct);
            var observed = Now; var parsed = ParsePage(json, observed);
            pages.Add(new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), json, observed));
            if (parsed.Status == "013")
            {
                if (page != 1) throw new InvalidOperationException("OpenDART pagination changed mid-collection; preserve and retry as a new snapshot.");
                return new(Guid.NewGuid().ToString("N"), start, end, [], pages.ToArray());
            }
            if (parsed.Page != page || parsed.TotalPages < page || parsed.TotalPages > 100 || (expectedCount != -1 && expectedCount != parsed.TotalCount)) throw new InvalidOperationException("OpenDART incomplete or changing pagination.");
            totalPages = parsed.TotalPages; expectedCount = parsed.TotalCount; disclosures.AddRange(parsed.Disclosures);
        }
        if (disclosures.Count != expectedCount || disclosures.Select(d => d.ReceiptNumber).Distinct().Count() != disclosures.Count || disclosures.Any(d => d.ReceiptDate < start || d.ReceiptDate > end))
            throw new InvalidOperationException("OpenDART incomplete/duplicate/out-of-period disclosures.");
        return new(Guid.NewGuid().ToString("N"), start, end, disclosures.ToArray(), pages.ToArray());
    }
    public async Task<CompanySnapshot> Company(string apiKey, string corpCode, CancellationToken ct = default)
    {
        Key(apiKey); if (corpCode.Length != 8 || !corpCode.All(char.IsAsciiDigit)) throw new ArgumentException("corp_code must be eight digits.");
        var json = await Get("company.json", $"crtfc_key={Uri.EscapeDataString(apiKey)}&corp_code={corpCode}", ct);
        using var doc = JsonDocument.Parse(json); Status(doc.RootElement, false);
        return new(corpCode, doc.RootElement.GetProperty("corp_name").GetString()!, doc.RootElement.GetProperty("stock_code").GetString() ?? "", Now, json);
    }
    private static void Key(string key)
    { if (key.Length != 40 || !key.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Set a 40-character OPENDART_API_KEY environment variable; do not pass it in arguments."); }
    private async Task<string> Get(string path, string query, CancellationToken ct)
    {
        // Fixed official host and read-only endpoints. No logging of URLs containing authentication keys.
        try
        {
            using var response = await http.GetAsync(new Uri("https://opendart.fss.or.kr/api/" + path + "?" + query), ct);
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"OpenDART HTTP status {(int)response.StatusCode}; no automatic retry.");
            var json = await response.Content.ReadAsStringAsync(ct);
            if (json.Length > 5_000_000) throw new InvalidOperationException("OpenDART response too large.");
            return json;
        }
        catch (HttpRequestException) { throw new InvalidOperationException("OpenDART network error; request URL redacted."); }
        catch (TaskCanceledException) { throw new InvalidOperationException("OpenDART request cancelled or timed out; request URL redacted."); }
    }
    private static string Status(JsonElement root, bool allowNoData)
    {
        var status = root.GetProperty("status").GetString() ?? "missing";
        if (status != "000" && !(allowNoData && status == "013")) throw new InvalidOperationException($"OpenDART status {status}; failed collection, no automatic retry.");
        return status;
    }
    public sealed record ParsedPage(string Status, int Page, int TotalPages, int TotalCount, Disclosure[] Disclosures);
    public static ParsedPage ParsePage(string json, DateTimeOffset observed)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement; var status = Status(root, true);
        if (status == "013") return new(status, 1, 0, 0, []);
        string Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        var disclosures = root.GetProperty("list").EnumerateArray().Select(e =>
        {
            var receipt = DateOnly.ParseExact(Text(e, "rcept_dt"), "yyyyMMdd", CultureInfo.InvariantCulture);
            var receiptNumber = Text(e, "rcept_no"); var code = Text(e, "corp_code");
            if (receiptNumber.Length != 14 || !receiptNumber.All(char.IsAsciiDigit) || code.Length != 8 || !code.All(char.IsAsciiDigit)) throw new ArgumentException("Invalid OpenDART receipt/company identifier.");
            var conservative = new DateTimeOffset(receipt.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9));
            // rcept_dt has date precision. Never claim historical availability based on a freshly fetched response.
            // Present-day rm flags are retained in raw pages only; they are not exposed as historical features.
            return new Disclosure(receiptNumber, code, Text(e, "corp_name"), Text(e, "stock_code"), Text(e, "report_nm"), receipt,
                observed, conservative > observed ? conservative : observed);
        }).ToArray();
        return new(status, root.GetProperty("page_no").GetInt32(), root.GetProperty("total_page").GetInt32(), root.GetProperty("total_count").GetInt32(), disclosures);
    }
}
