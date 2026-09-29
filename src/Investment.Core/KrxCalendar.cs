using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Investment.Core;

public sealed record KrxHoliday(DateOnly Date, string DayCode, string DayName, string Reason);
public sealed record KrxCalendarSnapshot(string Id, int Year, DateTimeOffset ObservedAt, string Source,
    string RawHash, string RawJson, KrxHoliday[] Holidays);

/// <summary>
/// Read-only public KRX holiday-screen query, not a supported KRX Open API contract.
/// Candidate weekdays are calendar diagnostics, not point-in-time certification or opening-time evidence.
/// </summary>
public sealed class KrxCalendarClient : IDisposable
{
    public const string Source = "https://global.krx.co.kr/contents/GLB/05/0501/0501110000/GLB0501110000.jsp";
    private const string TokenUrl = "https://global.krx.co.kr/contents/COM/GenerateOTP.jspx?name=form&bld=GLB%2F05%2F0501%2F0501110000%2Fglb0501110000_01";
    private const string QueryUrl = "https://global.krx.co.kr/contents/GLB/99/GLB99000001.jspx";
    private const int MaximumResponseBytes = 2_000_000;
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;

    public KrxCalendarClient(HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        if (handler is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
            throw new ArgumentException("KRX calendar transport must disable automatic redirects.", nameof(handler));
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        // The public KRX screen rejects a missing User-Agent. Identify this client without browser impersonation.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KrxStrategyResearch/1.0");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<KrxCalendarSnapshot> Year(int year, CancellationToken ct = default)
    {
        if (year < 2010 || year > clock().ToOffset(TimeSpan.FromHours(9)).Year)
            throw new ArgumentException("KRX calendar year must be a historical or current year from 2010 onward.", nameof(year));
        var page = await Fetch(HttpMethod.Get, Source, null, false, ct);
        var selector = Regex.Match(page, "<select\\b[^>]*\\bname=[\"']search_bas_yy[\"'][^>]*>(.*?)</select>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!selector.Success || !Regex.IsMatch(selector.Groups[1].Value,
                "<option\\b[^>]*\\bvalue=[\"']" + year.ToString(CultureInfo.InvariantCulture) + "[\"']", RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("Requested year is not exposed by the official KRX holiday screen.");
        var token = (await Fetch(HttpMethod.Get, TokenUrl, null, false, ct)).Trim();
        if (token.Length is < 1 or > 4096 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '+' and not '/' and not '=' and not '-' and not '_'))
            throw new InvalidOperationException("Invalid KRX public calendar form token; no automatic retry.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["search_bas_yy"] = year.ToString(CultureInfo.InvariantCulture), ["code"] = token
        });
        var raw = await Fetch(HttpMethod.Post, QueryUrl, form, true, ct);
        var holidays = Parse(raw, year);
        return new(Guid.NewGuid().ToString("N"), year, clock(), Source, Hash(raw), raw, holidays);
    }

    private async Task<string> Fetch(HttpMethod method, string uri, HttpContent? content, bool json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Referrer = new Uri(Source);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri)
                throw new InvalidOperationException($"KRX calendar HTTP status {(int)response.StatusCode} or redirect; no automatic retry.");
            // The official UI returns JSON with text/html; UTF-8. Parse() still rejects HTML/error bodies.
            if (json && response.Content.Headers.ContentType?.MediaType is not ("application/json" or "text/json" or "text/html"))
                throw new InvalidOperationException("Unexpected KRX calendar response content type.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidOperationException("KRX calendar response too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, ct)) != 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    throw new InvalidOperationException("KRX calendar response too large.");
                buffer.Write(bytes, 0, count);
            }
            return new UTF8Encoding(false, true).GetString(buffer.ToArray());
        }
        catch (HttpRequestException) { throw new InvalidOperationException("KRX calendar network failure; no automatic retry."); }
        catch (TaskCanceledException) { throw new InvalidOperationException("KRX calendar request cancelled or timed out; no automatic retry."); }
    }

    public static KrxHoliday[] Parse(string json, int expectedYear)
    {
        if (expectedYear is < 2010 or > 9999) throw new ArgumentException("Invalid KRX calendar year.");
        using var document = ParseDocument(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Count(p => p.Name == "block1") != 1 ||
            !document.RootElement.TryGetProperty("block1", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            throw new InvalidOperationException("KRX holiday response missing a nonempty block1; not an empty holiday calendar.");
        var result = rows.EnumerateArray().Select(row =>
        {
            string Field(string name)
            {
                if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count(p => p.Name == name) != 1 ||
                    !row.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Invalid KRX holiday field.");
                return field.GetString()!;
            }
            var rawDate = Field("calnd_dd");
            if (!DateOnly.TryParseExact(rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date.Year != expectedYear || Field("calnd_dd_dy") != rawDate)
                throw new ArgumentException("KRX holiday date/year mismatch.");
            var code = Field("dy_tp_cd");
            var name = Field("kr_dy_tp");
            var reason = Field("holdy_eng_nm");
            if (code != date.ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant() ||
                name != date.ToString("dddd", CultureInfo.InvariantCulture) || reason.Any(char.IsControl))
                throw new ArgumentException("KRX holiday weekday/reason mismatch.");
            return new KrxHoliday(date, code, name, reason);
        }).ToArray();
        if (result.Select(row => row.Date).Distinct().Count() != result.Length)
            throw new ArgumentException("Duplicate KRX holiday date.");
        return result.OrderBy(row => row.Date).ToArray();
    }

    private static JsonDocument ParseDocument(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidOperationException("KRX calendar response is not JSON; no automatic retry."); }
    }

    public static DateOnly[] CandidateSessions(KrxCalendarSnapshot snapshot, DateOnly from, DateOnly through)
    {
        if (snapshot.Source != Source || snapshot.RawHash != Hash(snapshot.RawJson) ||
            !Parse(snapshot.RawJson, snapshot.Year).SequenceEqual(snapshot.Holidays))
            throw new ArgumentException("KRX calendar source/raw/normalized snapshot mismatch.");
        if (from > through || from.Year != snapshot.Year || through.Year != snapshot.Year)
            throw new ArgumentException("Candidate session range must be within the snapshot year.");
        var closed = snapshot.Holidays.Select(row => row.Date).ToHashSet();
        return Enumerable.Range(0, through.DayNumber - from.DayNumber + 1).Select(from.AddDays)
            .Where(date => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !closed.Contains(date)).ToArray();
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    public void Dispose() => http.Dispose();
}
