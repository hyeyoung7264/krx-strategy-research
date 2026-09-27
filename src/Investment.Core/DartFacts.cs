using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Investment.Core;

public sealed record DartFactQuery(string Kind, string CorpCode, int? Year = null, string? ReportCode = null,
    string? StatementType = null, DateOnly? Start = null, DateOnly? End = null)
{
    [JsonIgnore] public string Identity => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
    [JsonIgnore] public string Endpoint => Kind switch
    {
        "FINANCIAL" => "fnlttSinglAcntAll.json", "BUYBACK" => "tsstkAqDecsn.json",
        "CAPITAL_INCREASE" => "piicDecsn.json", _ => throw new ArgumentException("Unsupported OpenDART research endpoint.")
    };
    public void Validate(DateTimeOffset now)
    {
        _ = Endpoint;
        if (CorpCode == null || CorpCode.Length != 8 || !CorpCode.All(char.IsAsciiDigit)) throw new ArgumentException("corp_code must be eight digits.");
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(9)).DateTime);
        if (Kind == "FINANCIAL")
        {
            if (Year is null or < 2015 || Year > today.Year || ReportCode is not ("11011" or "11012" or "11013" or "11014") ||
                StatementType is not ("CFS" or "OFS") || Start != null || End != null)
                throw new ArgumentException("Financial requests require a year from 2015, report code and CFS/OFS, without date range.");
        }
        else if (Year != null || ReportCode != null || StatementType != null || Start == null || End == null ||
            Start < new DateOnly(2015, 1, 1) || Start > End || End > today || End.Value.DayNumber - Start.Value.DayNumber > 90)
            throw new ArgumentException("Event requests require a past interval of at most 90 days from 2015, without financial parameters.");
    }
}

public sealed record DartFact(string ReceiptNumber, string CorpCode, Dictionary<string, string?> Fields);
public sealed record DartFactSnapshot(string Id, DartFactQuery Query, string ApiStatus, DartFact[] Rows,
    DateTimeOffset ObservedAt, string RawHash, string RawJson)
{
    public string Source => "https://opendart.fss.or.kr/api/" + Query.Endpoint;
    public void Validate(DateTimeOffset now)
    {
        Query.Validate(ObservedAt);
        if (ObservedAt > now || string.IsNullOrWhiteSpace(Id) || RawHash != Hash(RawJson)) throw new ArgumentException("Invalid OpenDART observation or raw hash.");
        var parsed = OpenDartClient.ParseFacts(Query, RawJson);
        if (ApiStatus != parsed.Status || !JsonSerializer.SerializeToUtf8Bytes(Rows).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(parsed.Rows)))
            throw new ArgumentException("OpenDART normalized facts differ from their raw response.");
    }
    public static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

public sealed partial class OpenDartClient
{
    public async Task<DartFactSnapshot> Facts(string apiKey, DartFactQuery request, CancellationToken ct = default)
    {
        Key(apiKey); request.Validate(Now);
        var query = "crtfc_key=" + Uri.EscapeDataString(apiKey) + "&corp_code=" + request.CorpCode;
        query += request.Kind == "FINANCIAL" ? $"&bsns_year={request.Year}&reprt_code={request.ReportCode}&fs_div={request.StatementType}"
            : $"&bgn_de={request.Start:yyyyMMdd}&end_de={request.End:yyyyMMdd}";
        var json = await Get(request.Endpoint, query, ct); var observed = Now; var parsed = ParseFacts(request, json);
        return new(Guid.NewGuid().ToString("N"), request, parsed.Status, parsed.Rows, observed, DartFactSnapshot.Hash(json), json);
    }
    public sealed record ParsedFacts(string Status, DartFact[] Rows);
    public static ParsedFacts ParseFacts(DartFactQuery query, string json)
    {
        _ = query.Endpoint;
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var status = Status(root, true);
        if (status == "013") return new(status, []);
        var rows = root.GetProperty("list").EnumerateArray().Select(row =>
        {
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in row.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || !fields.TryAdd(property.Name, property.Value.GetString()))
                    throw new ArgumentException("OpenDART fact fields must be unique text or null values.");
            }
            var receipt = fields.GetValueOrDefault("rcept_no") ?? "";
            var company = fields.GetValueOrDefault("corp_code");
            if (receipt.Length != 14 || !receipt.All(char.IsAsciiDigit) || company != query.CorpCode)
                throw new ArgumentException("OpenDART fact identifiers differ from requested company.");
            if (query.Kind == "FINANCIAL" && (fields.GetValueOrDefault("bsns_year") != query.Year!.Value.ToString(CultureInfo.InvariantCulture) ||
                fields.GetValueOrDefault("reprt_code") != query.ReportCode || fields.GetValueOrDefault("sj_div") is not ("BS" or "IS" or "CIS" or "CF" or "SCE") ||
                string.IsNullOrWhiteSpace(fields.GetValueOrDefault("account_id")) || string.IsNullOrWhiteSpace(fields.GetValueOrDefault("account_nm"))))
                throw new ArgumentException("OpenDART financial period/account does not match request.");
            // Keep period, currency, account identity and raw amount strings. Missing is not zero;
            // quarter vs cumulative values and CFS vs OFS are never merged implicitly.
            return new DartFact(receipt, company!, fields);
        }).ToArray();
        if (rows.Length == 0 || rows.Select(row => JsonSerializer.Serialize(row)).Distinct().Count() != rows.Length)
            throw new ArgumentException("OpenDART empty success or duplicate facts require review.");
        return new(status, rows);
    }
}

public sealed record DartVisibleFact(DartFactQuery Query, string ReceiptNumber, string CorpCode, string SnapshotHash,
    DateTimeOffset AvailableAt, Dictionary<string, string?> Fields)
{
    public string Kind => Query.Kind;
}

public static class DartResearchIndex
{
    public static DartVisibleFact[] At(DartFactSnapshot[] snapshots, DisclosureBatch[] batches,
        string company, DateTimeOffset cutoff, DateTimeOffset now)
    {
        if (cutoff > now) throw new ArgumentException("Research cutoff cannot be in the future.");
        foreach (var snapshot in snapshots) snapshot.Validate(now);
        if (snapshots.GroupBy(s => (s.Query.Identity, s.ObservedAt)).Any(group => group.Select(s => s.RawHash).Distinct().Count() > 1))
            throw new ArgumentException("Conflicting OpenDART snapshots share an observation time; review required.");
        var disclosures = new List<Disclosure>();
        foreach (var batch in batches)
        {
            if (batch.Source != "https://opendart.fss.or.kr/api/list.json" || batch.Start > batch.End || batch.Pages.Length == 0)
                throw new ArgumentException("Missing disclosure source/pages.");
            var parsed = batch.Pages.Select(page =>
            {
                if (page.ObservedAt > now || page.Hash != DartFactSnapshot.Hash(page.Json)) throw new ArgumentException("Invalid disclosure page observation/hash.");
                return OpenDartClient.ParsePage(page.Json, page.ObservedAt);
            }).ToArray();
            var rows = parsed.SelectMany(page => page.Disclosures).ToArray();
            if (!JsonSerializer.SerializeToUtf8Bytes(rows).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(batch.Disclosures)) ||
                rows.Any(row => row.ReceiptDate < batch.Start || row.ReceiptDate > batch.End) || rows.Select(row => row.ReceiptNumber).Distinct().Count() != rows.Length ||
                (parsed[0].Status == "013" ? parsed.Length != 1 : parsed.Length != parsed[0].TotalPages || rows.Length != parsed[0].TotalCount ||
                    parsed.Where((page, index) => page.Status != "000" || page.Page != index + 1 || page.TotalPages != parsed.Length || page.TotalCount != rows.Length).Any()))
                throw new ArgumentException("Disclosure raw pages, coverage and normalized records differ.");
            disclosures.AddRange(rows);
        }
        var visible = new List<DartVisibleFact>();
        foreach (var snapshot in snapshots.Where(s => s.Query.CorpCode == company && s.ObservedAt <= cutoff)
            .GroupBy(s => s.Query.Identity).Select(group => group.OrderByDescending(s => s.ObservedAt).ThenBy(s => s.RawHash, StringComparer.Ordinal).First()))
        foreach (var row in snapshot.Rows)
        {
            var disclosure = disclosures.Where(d => d.ReceiptNumber == row.ReceiptNumber && d.CorpCode == company && d.UsableAt <= cutoff)
                .OrderBy(d => d.UsableAt).FirstOrDefault();
            if (disclosure == null) continue; // No inference of historical filing time from identifier/year.
            var available = snapshot.ObservedAt > disclosure.UsableAt ? snapshot.ObservedAt : disclosure.UsableAt;
            if (available <= cutoff) visible.Add(new(snapshot.Query, row.ReceiptNumber, company, snapshot.RawHash, available, new(row.Fields)));
        }
        string Key(DartVisibleFact fact) => JsonSerializer.Serialize(new[] { fact.Kind, fact.ReceiptNumber, fact.Query.StatementType,
            fact.Fields.GetValueOrDefault("sj_div"), fact.Fields.GetValueOrDefault("account_id"), fact.Fields.GetValueOrDefault("account_nm"),
            fact.Fields.GetValueOrDefault("account_detail"), fact.Fields.GetValueOrDefault("ord") });
        return visible.GroupBy(Key).Select(group =>
        {
            var latest = group.Where(f => f.AvailableAt == group.Max(f => f.AvailableAt)).ToArray();
            if (latest.Select(f => JsonSerializer.Serialize(f.Fields)).Distinct().Count() > 1)
                throw new ArgumentException("Conflicting visible versions of an OpenDART fact require review.");
            return latest[0];
        }).OrderBy(f => f.AvailableAt).ThenBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.ReceiptNumber, StringComparer.Ordinal).ToArray();
    }
}
