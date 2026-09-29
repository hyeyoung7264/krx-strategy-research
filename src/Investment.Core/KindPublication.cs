using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Investment.Core;

public sealed record KindPublicationQuery(string Ticker, DateOnly From, DateOnly Through, string AcceptanceNo)
{
    public void Validate(DateTimeOffset? now = null) => KindPublicationClient.ValidateQuery(this, now ?? DateTimeOffset.UtcNow);
}

/// <summary>One bounded HTTP entity, including error bodies. Capture time is not publication time.</summary>
public sealed record KindPublicationCapture(string Stage, string Method, string Source, string? RequestBody,
    DateTimeOffset ObservedAt, int HttpStatusCode, string? ContentType, string[] ContentEncodings,
    string RawHash, string RawBase64)
{
    public void Validate(DateTimeOffset? now = null) => KindPublicationClient.ValidateCapture(this, now ?? DateTimeOffset.UtcNow);
}

/// <summary>
/// A current public-screen observation. The minute text has no certified timezone or first-publication meaning.
/// ViewerTicker is a representative company code, not the full set of securities affected by the notice.
/// </summary>
public sealed record KindPublicationRecord(string AcceptanceNo, string DocumentNo, string IssuerId,
    string CompanyName, string Market, string Title, string Submitter, DateOnly DisplayedDate, string DisplayedMinute,
    string ViewerTicker, string ViewerCompanyName, string ExternalSource, string Precision = "MINUTE", string? TimeZone = null);

public sealed record KindPublicationReceipt(string Id, KindPublicationQuery Query, DateTimeOffset CreatedAt,
    string Status, string? FailedStage, string? ErrorKind, KindPublicationCapture[] Captures, KindPublicationRecord? Publication)
{
    public void Validate(DateTimeOffset? now = null) => KindPublicationClient.Validate(this, now ?? DateTimeOffset.UtcNow);
}

/// <summary>
/// Reads one complete, at most 100-row KIND search page and resolves one acceptance number through its viewer.
/// This public UI is not an Open API. No scripts, linked resources, notice bodies, retries or subsequent pages are fetched.
/// No availability timestamp, historical certification or ledger input is produced.
/// </summary>
public sealed class KindPublicationClient : IDisposable
{
    public const int MaximumResponseBytes = 2_000_000;
    public const int MaximumRequests = 3;
    public const int MinimumIntervalSeconds = 1;
    public const string ListSource = "https://kind.krx.co.kr/disclosure/details.do";
    private const string ViewerSource = "https://kind.krx.co.kr/common/disclsviewer.do";
    private const int MaximumBase64Length = ((MaximumResponseBytes + 2) / 3) * 4;
    private static readonly string[] Stages = ["LIST", "VIEWER", "CONTENTS"];
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim pipeline = new(1, 1);
    private readonly Stopwatch sinceRequest = new();

    public KindPublicationClient(HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        ValidateTransport(handler);
        http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KrxStrategyResearch/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<KindPublicationReceipt> Fetch(KindPublicationQuery query,
        Func<KindPublicationCapture, Task>? preserveCapture = null, CancellationToken ct = default)
    {
        ValidateQuery(query, clock());
        try { await pipeline.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            return new(Guid.NewGuid().ToString("N"), query, clock(), "CANCELLED", "LIST", nameof(OperationCanceledException), [], null);
        }
        try { return await FetchCore(query, preserveCapture, ct); }
        finally { pipeline.Release(); }
    }

    private async Task<KindPublicationReceipt> FetchCore(KindPublicationQuery query,
        Func<KindPublicationCapture, Task>? preserveCapture, CancellationToken ct)
    {
        var captures = new List<KindPublicationCapture>();
        Listing? listing = null; Viewer? viewer = null; string? external = null;
        KindPublicationReceipt Finish(string status, string? stage, string? error) => new(
            Guid.NewGuid().ToString("N"), query, clock(), status, stage, error, captures.ToArray(),
            status == "COLLECTED_UNREVIEWED" ? Record(listing!, viewer!, external!) : null);

        foreach (var stage in Stages)
        {
            KindPublicationCapture capture;
            try
            {
                ct.ThrowIfCancellationRequested();
                var remaining = TimeSpan.FromSeconds(MinimumIntervalSeconds) - sinceRequest.Elapsed;
                if (sinceRequest.IsRunning && remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
                ct.ThrowIfCancellationRequested();
                sinceRequest.Restart();
                capture = await Read(stage, query, viewer?.DocumentNo, ct);
                captures.Add(capture);
            }
            catch (Exception ex) when (Operational(ex))
            {
                return Finish(ct.IsCancellationRequested ? "CANCELLED" : "REQUEST_FAILED", stage, ex.GetType().Name);
            }
            if (preserveCapture is not null)
            {
                try { await preserveCapture(capture with { ContentEncodings = capture.ContentEncodings.ToArray() }); }
                catch (Exception ex)
                {
                    // A callback may have failed after writing some bytes. Never claim durable preservation or continue.
                    return Finish("EVIDENCE_PRESERVATION_FAILED", stage, ex.GetType().Name);
                }
            }
            if (ct.IsCancellationRequested) return Finish("CANCELLED", stage, nameof(OperationCanceledException));
            try
            {
                ValidateCapture(capture, clock());
                ValidateQuery(query, capture.ObservedAt);
                if (captures.Count > 1 && captures[^2].ObservedAt > capture.ObservedAt)
                    throw Invalid("Observation sequence reversed.");
                var html = DecodeSuccess(capture);
                if (stage == "LIST") listing = ParseList(html, query);
                else if (stage == "VIEWER") viewer = ParseViewer(html, query, listing!);
                else external = ParseContents(html, viewer!.DocumentNo);
            }
            catch (Exception ex) when (Operational(ex))
            {
                return Finish("RESPONSE_REJECTED", stage, ex.GetType().Name);
            }
        }
        return Finish("COLLECTED_UNREVIEWED", null, null);
    }

    public static void ValidateQuery(KindPublicationQuery query, DateTimeOffset now)
    {
        ValidateQueryShape(query);
        // This bounds a Korean-market request date. It does not assign a timezone to the returned minute text.
        if (query.Through > DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(9)).DateTime))
            throw Invalid("KIND query cannot end in the future.");
    }

    private static void ValidateQueryShape(KindPublicationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Ticker is not { Length: 6 } || !query.Ticker.All(char.IsAsciiLetterOrDigit) ||
            !IsNumber(query.AcceptanceNo, 14) || query.From.Year < 1900 || query.From > query.Through ||
            query.Through.Year - query.From.Year > 3 || query.From.Year <= 9996 && query.From.AddYears(3) < query.Through)
            throw Invalid("KIND requires an explicit six-character ticker, acceptance number and at most three-year date range.");
    }

    public static string ListRequestBody(KindPublicationQuery query)
    {
        ValidateQueryShape(query);
        return "method=searchDetailsSub&forward=details_sub&currentPageSize=100&pageIndex=1&orderMode=1&orderStat=D" +
            "&searchCodeType=number&repIsuSrtCd=A" + query.Ticker + "&searchCorpName=" + query.Ticker +
            "&fromDate=" + query.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            "&toDate=" + query.Through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Source(string stage, KindPublicationQuery query, string? documentNo) => stage switch
    {
        "LIST" => ListSource,
        "VIEWER" => ViewerSource + "?method=search&acptno=" + query.AcceptanceNo,
        "CONTENTS" when IsNumber(documentNo, 14) => ViewerSource + "?method=searchContents&docNo=" + documentNo,
        _ => throw Invalid("Missing KIND request identity.")
    };

    private async Task<KindPublicationCapture> Read(string stage, KindPublicationQuery query, string? documentNo, CancellationToken ct)
    {
        var source = Source(stage, query, documentNo);
        var body = stage == "LIST" ? ListRequestBody(query) : null;
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, source);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != source)
            throw Invalid("Response source differs from the fixed request; redirects are not followed.");
        var length = response.Content.Headers.ContentLength;
        if (length is > MaximumResponseBytes or < 0) throw Invalid("KIND response exceeds the byte limit.");
        var contentType = response.Content.Headers.TryGetValues("Content-Type", out var values) ? string.Join("\n", values) : null;
        var encodings = response.Content.Headers.ContentEncoding.ToArray();
        if (contentType?.Length > 4096 || encodings.Length > 16 || encodings.Any(e => e.Length > 256))
            throw Invalid("KIND response headers exceed the capture bound.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw Invalid("KIND response exceeds the byte limit.");
            buffer.Write(chunk, 0, count);
        }
        if (buffer.Length == 0 || length is { } expected && expected != buffer.Length) throw Invalid("Empty or incomplete KIND response body.");
        var raw = buffer.ToArray();
        return new(stage, request.Method.Method, source, body, clock(), (int)response.StatusCode, contentType, encodings,
            Convert.ToHexString(SHA256.HashData(raw)), Convert.ToBase64String(raw));
    }

    public static void ValidateCapture(KindPublicationCapture capture, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.ObservedAt == default || capture.ObservedAt > now || capture.HttpStatusCode is < 100 or > 599 ||
            capture.ContentType?.Length > 4096 || capture.ContentEncodings is null || capture.ContentEncodings.Length > 16 ||
            capture.ContentEncodings.Any(e => e is null || e.Length > 256) || capture.RawHash is null ||
            !Match(capture.RawHash, @"\A[0-9A-F]{64}\z").Success || string.IsNullOrEmpty(capture.RawBase64) || capture.RawBase64.Length > MaximumBase64Length)
            throw Invalid("Invalid KIND capture metadata or body bound.");
        if (capture.Stage == "LIST")
        {
            if (capture.Method != "POST" || capture.Source != ListSource || capture.RequestBody is null || capture.RequestBody.Length > 500)
                throw Invalid("Invalid KIND list request.");
            var body = Match(capture.RequestBody,
                @"\Amethod=searchDetailsSub&forward=details_sub&currentPageSize=100&pageIndex=1&orderMode=1&orderStat=D&searchCodeType=number&repIsuSrtCd=A(?<ticker>[A-Za-z0-9]{6})&searchCorpName=\k<ticker>&fromDate=(?<from>[0-9]{4}-[0-9]{2}-[0-9]{2})&toDate=(?<through>[0-9]{4}-[0-9]{2}-[0-9]{2})\z");
            if (!body.Success || !DateOnly.TryParseExact(body.Groups["from"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                !DateOnly.TryParseExact(body.Groups["through"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var through))
                throw Invalid("Invalid canonical KIND list form.");
            ValidateQuery(new(body.Groups["ticker"].Value, from, through, "00000000000000"), capture.ObservedAt);
        }
        else
        {
            var method = capture.Stage switch { "VIEWER" => "search&acptno=", "CONTENTS" => "searchContents&docNo=", _ => throw Invalid("Unknown KIND stage.") };
            var prefix = ViewerSource + "?method=" + method;
            if (capture.Method != "GET" || capture.RequestBody is not null || capture.Source is null ||
                !capture.Source.StartsWith(prefix, StringComparison.Ordinal) || !IsNumber(capture.Source[prefix.Length..], 14))
                throw Invalid("Invalid fixed KIND viewer request.");
        }
        _ = RawBytes(capture);
    }

    public static void Validate(KindPublicationReceipt receipt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateQuery(receipt.Query, now);
        if (!Guid.TryParseExact(receipt.Id, "N", out _) || receipt.CreatedAt == default || receipt.CreatedAt > now ||
            receipt.Captures is null || receipt.Captures.Length > MaximumRequests)
            throw Invalid("Invalid KIND receipt identity, time or capture count.");
        ValidateQuery(receipt.Query, receipt.CreatedAt);
        var complete = receipt.Status == "COLLECTED_UNREVIEWED";
        var failed = complete ? MaximumRequests : Array.IndexOf(Stages, receipt.FailedStage);
        if (complete)
        {
            if (receipt.FailedStage is not null || receipt.ErrorKind is not null || receipt.Captures.Length != MaximumRequests || receipt.Publication is null)
                throw Invalid("Incomplete successful KIND receipt.");
        }
        else
        {
            if (receipt.Status is not ("REQUEST_FAILED" or "RESPONSE_REJECTED" or "CANCELLED" or "EVIDENCE_PRESERVATION_FAILED") ||
                failed < 0 || receipt.Publication is not null || receipt.ErrorKind is null ||
                !Match(receipt.ErrorKind, @"\A[A-Za-z_][A-Za-z0-9_`]{0,127}\z").Success ||
                receipt.Captures.Length < failed || receipt.Captures.Length > failed + 1 ||
                receipt.Status == "REQUEST_FAILED" && receipt.Captures.Length != failed ||
                (receipt.Status is "RESPONSE_REJECTED" or "EVIDENCE_PRESERVATION_FAILED") && receipt.Captures.Length != failed + 1)
                throw Invalid("Invalid failed KIND receipt.");
        }
        Listing? listing = null; Viewer? viewer = null; string? external = null;
        for (var i = 0; i < receipt.Captures.Length; i++)
        {
            var capture = receipt.Captures[i];
            ValidateCapture(capture, now);
            ValidateQuery(receipt.Query, capture.ObservedAt);
            if (capture.Stage != Stages[i] || capture.Source != Source(Stages[i], receipt.Query, viewer?.DocumentNo) ||
                capture.RequestBody != (i == 0 ? ListRequestBody(receipt.Query) : null) || capture.ObservedAt > receipt.CreatedAt ||
                i > 0 && capture.ObservedAt < receipt.Captures[i - 1].ObservedAt)
                throw Invalid("KIND receipt request or observation chain differs.");
            if (i == failed && receipt.Status != "RESPONSE_REJECTED") continue;
            void Parse()
            {
                var html = DecodeSuccess(capture);
                if (i == 0) listing = ParseList(html, receipt.Query);
                else if (i == 1) viewer = ParseViewer(html, receipt.Query, listing!);
                else external = ParseContents(html, viewer!.DocumentNo);
            }
            if (i == failed)
            {
                Exception? rejection = null;
                try { Parse(); } catch (Exception ex) when (Operational(ex)) { rejection = ex; }
                if (rejection is null || rejection.GetType().Name != receipt.ErrorKind)
                    throw Invalid("Claimed KIND response rejection does not match the raw response.");
            }
            else Parse();
        }
        if (complete && receipt.Publication != Record(listing!, viewer!, external!))
            throw Invalid("KIND normalized publication observation differs from raw captures.");
    }

    private static byte[] RawBytes(KindPublicationCapture capture)
    {
        byte[] raw;
        try { raw = Convert.FromBase64String(capture.RawBase64); }
        catch (FormatException) { throw Invalid("Invalid KIND body base64."); }
        if (raw.Length is < 1 or > MaximumResponseBytes || Convert.ToBase64String(raw) != capture.RawBase64 ||
            Convert.ToHexString(SHA256.HashData(raw)) != capture.RawHash)
            throw Invalid("KIND exact body/hash mismatch.");
        return raw;
    }

    private static string DecodeSuccess(KindPublicationCapture capture)
    {
        if (capture.HttpStatusCode != 200) throw Invalid("KIND HTTP response was not successful.");
        if (capture.ContentType is null || !MediaTypeHeaderValue.TryParse(capture.ContentType, out var type) ||
            !string.Equals(type.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
            type.Parameters.Count(p => p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) != 1 ||
            !string.Equals(type.CharSet?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase) ||
            capture.ContentEncodings.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("KIND requires the observed UTF-8 HTML transport without content decompression.");
        var raw = RawBytes(capture);
        if (raw.Length == 0) throw Invalid("Empty KIND response.");
        return Utf8.GetString(raw);
    }

    private sealed record Listing(string AcceptanceNo, string IssuerId, string CompanyName, string Market,
        string Title, string Submitter, DateOnly Date, string Minute);
    private sealed record Viewer(string DocumentNo, string Ticker, string CompanyName);
    private static KindPublicationRecord Record(Listing row, Viewer viewer, string external) => new(
        row.AcceptanceNo, viewer.DocumentNo, row.IssuerId, row.CompanyName, row.Market, row.Title, row.Submitter,
        row.Date, row.Minute, viewer.Ticker, viewer.CompanyName, external);

    private static Listing ParseList(string html, KindPublicationQuery query)
    {
        var root = Html.Parse(html);
        var table = One(root.All("table").Where(n => n.Attr("summary") == "번호, 시간, 회사명, 공시제목, 제출인, 차트/주가"), "list table");
        if (!table.Classes.Contains("list")) throw Invalid("Unexpected KIND table class.");
        var body = One(table.Children.Where(n => n.Name == "tbody"), "list body");
        if (body.Children.Any(n => n.Name != "tr") || body.OwnText.Trim().Length != 0) throw Invalid("Unexpected KIND body structure.");
        var paging = One(root.All("section").Where(n => n.Classes.Contains("paging-group")), "paging section");
        var footer = Matches(paging.Text(), @"전체\s*(?<total>[0-9]+)\s*건\s*:\s*(?<page>[0-9]+)\s*/\s*(?<pages>[0-9]+)");
        if (footer.Length != 1 || Integer(footer[0].Groups["page"].Value) != 1 || Integer(footer[0].Groups["pages"].Value) != 1)
            throw Invalid("KIND requires one complete list page.");
        var total = Integer(footer[0].Groups["total"].Value);
        if (total is < 1 or > 100 || body.Children.Count != total)
            throw Invalid("KIND list is empty, exceeds 100 rows, or does not match its complete count.");
        var size = One(paging.All("select").Where(n => n.Attr("name") == "currentPageSize"), "page size");
        if (Selected(size).Attr("value") != "100") throw Invalid("KIND page size differs from the request.");
        var rows = new List<Listing>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, index) in body.Children.Select((row, index) => (row, index)))
        {
            if (row.Children.Count != 6 || row.Children.Any(n => n.Name != "td") || row.OwnText.Trim().Length != 0)
                throw Invalid("KIND list row requires six cells.");
            var cells = row.Children;
            if (Integer(cells[0].Text()) != total - index) throw Invalid("KIND list row number differs.");
            var time = Match(cells[1].Text(), @"\A(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2}) (?<minute>[0-9]{2}:[0-9]{2})\z");
            if (!time.Success || !DateOnly.TryParseExact(time.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !TimeOnly.TryParseExact(time.Groups["minute"].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || date < query.From || date > query.Through)
                throw Invalid("Invalid KIND displayed minute or date range.");
            var company = One(cells[2].All("a"), "company link");
            var issuer = Match(company.Attr("onclick"), @"\A\s*companysummary_open\('(?<id>[A-Za-z0-9]{5})'\);\s*return\s+false;\s*\z");
            var name = Text(company, 200);
            if (!issuer.Success || company.Attr("title") != name) throw Invalid("Invalid KIND company link.");
            var markets = cells[2].All("img").Select(n => n.Attr("alt")).Where(x => x is "유가증권" or "코스닥" or "코넥스").ToArray();
            if (markets.Length != 1) throw Invalid("KIND market indicator missing or ambiguous.");
            var market = markets[0] switch { "유가증권" => "KOSPI", "코스닥" => "KOSDAQ", _ => "KONEX" };
            var title = One(cells[3].All("a"), "notice link");
            var acceptance = Match(title.Attr("onclick"), @"\A\s*openDisclsViewer\('(?<id>[0-9]{14})',''\)\s*;?\s*\z");
            if (!acceptance.Success || title.Attr("href") != "#viewer" || !ids.Add(acceptance.Groups["id"].Value))
                throw Invalid("Invalid or duplicate KIND acceptance number.");
            var parsed = new Listing(acceptance.Groups["id"].Value, issuer.Groups["id"].Value, name, market,
                Text(title, 2000), Text(cells[4], 500), date, time.Groups["minute"].Value);
            if (rows.Count > 0 && (parsed.CompanyName != rows[0].CompanyName || parsed.IssuerId != rows[0].IssuerId || parsed.Market != rows[0].Market ||
                parsed.Date > rows[^1].Date || parsed.Date == rows[^1].Date && string.CompareOrdinal(parsed.Minute, rows[^1].Minute) > 0))
                throw Invalid("KIND company-filtered list or descending time order differs.");
            rows.Add(parsed);
        }
        return One(rows.Where(r => r.AcceptanceNo == query.AcceptanceNo), "requested acceptance number");
    }

    private static Viewer ParseViewer(string html, KindPublicationQuery query, Listing listing)
    {
        var root = Html.Parse(html);
        var form = One(root.All("form").Where(n => n.Attr("id") == "frm"), "viewer form");
        var download = One(root.All("form").Where(n => n.Attr("id") == "docdownloadform"), "download form");
        var allIds = root.All("input").Where(n => n.Attr("id") == "acptNo" || n.Attr("name") == "acptNo").ToArray();
        if (allIds.Length != 2) throw Invalid("KIND viewer requires its two acceptance identity fields.");
        foreach (var scope in new[] { form, download })
        {
            var identity = One(scope.All("input").Where(n => n.Attr("id") == "acptNo" || n.Attr("name") == "acptNo"), "viewer acceptance identity");
            if (identity.Attr("id") != "acptNo" || identity.Attr("name") != "acptNo" || identity.Attr("type") != "hidden" || identity.Attr("value") != query.AcceptanceNo)
                throw Invalid("KIND viewer acceptance number differs.");
        }
        var heading = One(form.All("h1"), "viewer company heading");
        var company = Match(heading.Text(), @"\A(?<name>.+) \((?<ticker>[A-Za-z0-9]{6})\)\z");
        if (!company.Success || company.Groups["ticker"].Value != query.Ticker || company.Groups["name"].Value != listing.CompanyName)
            throw Invalid("KIND viewer representative ticker or company differs.");
        var selected = One(root.All("select").Where(n => n.Attr("id") == "mainDoc" || n.Attr("name") == "mainDoc"), "main document selector");
        if (!form.All("select").Contains(selected) || selected.Attr("id") != "mainDoc" || selected.Attr("name") != "mainDoc")
            throw Invalid("KIND main document selector differs.");
        var option = Selected(selected);
        var docs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in selected.Children)
        {
            var value = candidate.Attr("value");
            if (value.Length == 0) continue;
            var identity = Match(value, @"\A(?<id>[0-9]{14})\|[YN]\z");
            if (!identity.Success || !docs.Add(identity.Groups["id"].Value)) throw Invalid("Ambiguous KIND document options.");
        }
        var document = Match(option.Attr("value"), @"\A(?<id>[0-9]{14})\|[YN]\z");
        if (!document.Success) throw Invalid("KIND selected document identity missing.");
        return new(document.Groups["id"].Value, company.Groups["ticker"].Value, company.Groups["name"].Value);
    }

    private static string ParseContents(string html, string documentNo)
    {
        var root = Html.Parse(html);
        var scripts = root.All("script").Where(n => n.OwnText.Trim().Length != 0).ToArray();
        if (scripts.Length != 1 || scripts[0].Attr("src").Length != 0) throw Invalid("KIND contents routing script missing or ambiguous.");
        var scriptType = scripts[0].Attr("type");
        if (scriptType.Length != 0 && !scriptType.Equals("text/javascript", StringComparison.OrdinalIgnoreCase) &&
            !scriptType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase))
            throw Invalid("KIND routing must be a script, not an inert data block.");
        var tokens = ScriptTokens(scripts[0].OwnText); var offset = 0;
        void Expect(params string[] expected)
        {
            foreach (var value in expected)
                if (offset >= tokens.Count || tokens[offset].Quoted || tokens[offset++].Value != value)
                    throw Invalid("Unexpected KIND routing expression.");
        }
        string Literal()
        {
            if (offset >= tokens.Count || !tokens[offset].Quoted) throw Invalid("KIND routing arguments must be plain string literals.");
            return tokens[offset++].Value;
        }
        Expect("parent", ".", "setPath", "(");
        var args = new string[5];
        for (var i = 0; i < args.Length; i++) { if (i > 0) Expect(","); args[i] = Literal(); }
        Expect(")", ";");
        if (offset < tokens.Count)
        {
            Expect("parent", ".", "$", "(");
            if (Literal() != "#dialog-loading") throw Invalid("Unexpected KIND dialog target.");
            Expect(")", ".", "dialog", "(");
            if (Literal() != "close") throw Invalid("Unexpected KIND dialog action.");
            Expect(")", ";");
        }
        if (offset != tokens.Count || args[0] != "" || !IsNumber(args[3], 2) || !IsNumber(args[4], 2))
            throw Invalid("Unsupported or ambiguous KIND contents routing.");
        var uri = KindNoticeClient.ValidateSource(args[1]);
        if (uri.AbsolutePath.Split('/')[6] != documentNo || args[2] != uri.AbsolutePath[..^4])
            throw Invalid("KIND routing document identity or server path differs.");
        return args[1];
    }

    private sealed record Token(string Value, bool Quoted = false);
    private static List<Token> ScriptTokens(string script)
    {
        if (script.Length > 20_000) throw Invalid("KIND routing script exceeds its structural limit.");
        var result = new List<Token>(); var i = 0;
        while (i < script.Length)
        {
            if (char.IsWhiteSpace(script[i])) { i++; continue; }
            if (script.AsSpan(i).StartsWith("//", StringComparison.Ordinal) || script.AsSpan(i).StartsWith("/*", StringComparison.Ordinal))
            {
                var line = script[i + 1] == '/';
                var end = line ? script.IndexOf('\n', i + 2) : script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { if (!line) throw Invalid("Unterminated KIND script comment."); end = script.Length; }
                if (script[i..end].Contains("setPath", StringComparison.Ordinal)) throw Invalid("KIND routing call hidden in a comment.");
                i = line ? end : end + 2; continue;
            }
            var c = script[i];
            if (c is '\'' or '"')
            {
                var quote = c; var start = ++i;
                while (i < script.Length && script[i] != quote)
                {
                    if (script[i] == '\\' || char.IsControl(script[i])) throw Invalid("Escaped or multiline KIND routing literal.");
                    i++;
                }
                if (i == script.Length) throw Invalid("Unterminated KIND routing literal.");
                result.Add(new(script[start..i++], true));
            }
            else if (char.IsAsciiLetter(c) || c is '_' or '$')
            {
                var start = i++;
                while (i < script.Length && (char.IsAsciiLetterOrDigit(script[i]) || script[i] is '_' or '$')) i++;
                result.Add(new(script[start..i]));
            }
            else if (c is '.' or '(' or ')' or ',' or ';') { result.Add(new(c.ToString())); i++; }
            else throw Invalid("Unsupported KIND routing script token.");
            if (result.Count > 100) throw Invalid("KIND routing script has too many tokens.");
        }
        return result;
    }

    private static Html Selected(Html select)
    {
        if (select.Children.Count is < 1 or > 100 || select.Children.Any(n => n.Name != "option")) throw Invalid("Invalid KIND option structure.");
        var selected = One(select.Children.Where(n => n.Attributes.ContainsKey("selected")), "selected option");
        if (selected.Attr("selected") is { Length: > 0 } value && !value.Equals("selected", StringComparison.OrdinalIgnoreCase))
            throw Invalid("Invalid KIND selected attribute.");
        return selected;
    }
    private static string Text(Html node, int limit)
    {
        if (node.All().Any(n => n.Name is "table" or "form" or "iframe" or "script" or "style" or "select")) throw Invalid("Unexpected nested KIND field.");
        var text = node.Text();
        if (text.Length is < 1 || text.Length > limit || text.Any(char.IsControl)) throw Invalid("Invalid KIND text field.");
        return text;
    }
    private static T One<T>(IEnumerable<T> values, string field)
    {
        var items = values.Take(2).ToArray();
        return items.Length == 1 ? items[0] : throw Invalid("KIND " + field + " missing or ambiguous.");
    }
    private static bool IsNumber(string? value, int length) => value is not null && value.Length == length && value.All(char.IsAsciiDigit);
    private static int Integer(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw Invalid("Invalid KIND integer.");
    private static Match Match(string value, string pattern) => Regex.Match(value, pattern, RegexOptions.CultureInvariant, RegexTimeout);
    private static Match[] Matches(string value, string pattern) => Regex.Matches(value, pattern, RegexOptions.CultureInvariant, RegexTimeout).Cast<Match>().ToArray();
    private static ArgumentException Invalid(string message) => new(message);
    private static bool Operational(Exception ex) => ex is ArgumentException or InvalidOperationException or HttpRequestException or IOException or
        OperationCanceledException or FormatException or OverflowException or RegexMatchTimeoutException;

    // Small bounded tokenizer for the verified HTML structures, not a browser or script interpreter.
    // Comments and raw script/style/title text cannot manufacture DOM fields. Quoted attributes may
    // touch the following attribute (as observed on mainDoc), but duplicate attributes always fail.
    private sealed class Html(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Html> Children { get; } = [];
        private readonly List<object> parts = [];
        public string OwnText => string.Concat(parts.OfType<string>());
        public string Attr(string name) => Attributes.GetValueOrDefault(name, "");
        public string[] Classes => Attr("class").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        public IEnumerable<Html> All(string? name = null)
        {
            foreach (var child in Children)
            {
                if (name is null || child.Name == name) yield return child;
                foreach (var descendant in child.All(name)) yield return descendant;
            }
        }
        private string CombinedText() => string.Concat(parts.Select(p => p is Html node ? node.CombinedText() : WebUtility.HtmlDecode((string)p)));
        public string Text() => Regex.Replace(CombinedText(), @"\s+", " ", RegexOptions.CultureInvariant, RegexTimeout).Trim();
        public static Html Parse(string html)
        {
            var root = new Html("#document"); var stack = new Stack<Html>(); stack.Push(root);
            var i = 0; var nodes = 0;
            while (i < html.Length)
            {
                if (html[i] != '<')
                {
                    var end = html.IndexOf('<', i); if (end < 0) end = html.Length;
                    stack.Peek().parts.Add(html[i..end]); i = end; continue;
                }
                if (html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
                {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (end < 0) throw Invalid("Unterminated KIND HTML comment.");
                    i = end + 3; continue;
                }
                if (html.AsSpan(i).StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
                {
                    var end = html.IndexOf('>', i + 9); if (end < 0) throw Invalid("Invalid KIND doctype.");
                    i = end + 1; continue;
                }
                i++; var closing = i < html.Length && html[i] == '/'; if (closing) i++;
                var start = i;
                while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] is ':' or '-')) i++;
                if (i == start || !char.IsAsciiLetter(html[start])) throw Invalid("Invalid KIND HTML tag.");
                var tag = html[start..i].ToLowerInvariant();
                if (closing)
                {
                    SkipSpace(html, ref i);
                    if (i >= html.Length || html[i++] != '>' || stack.Count == 1 || stack.Pop().Name != tag)
                        throw Invalid("Mismatched KIND HTML closing tag.");
                    continue;
                }
                var node = new Html(tag); var selfClosing = false;
                while (true)
                {
                    SkipSpace(html, ref i);
                    if (i >= html.Length) throw Invalid("Unterminated KIND HTML tag.");
                    if (html[i] == '>') { i++; break; }
                    if (html[i] == '/' && i + 1 < html.Length && html[i + 1] == '>') { selfClosing = true; i += 2; break; }
                    start = i;
                    while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] is '_' or ':' or '-' or '.')) i++;
                    if (i == start || !(char.IsAsciiLetter(html[start]) || html[start] is '_' or ':')) throw Invalid("Invalid KIND HTML attribute.");
                    var attribute = html[start..i]; SkipSpace(html, ref i); var value = "";
                    if (i < html.Length && html[i] == '=')
                    {
                        i++; SkipSpace(html, ref i);
                        if (i >= html.Length) throw Invalid("Missing KIND attribute value.");
                        if (html[i] is '\'' or '"')
                        {
                            var quote = html[i++]; start = i;
                            while (i < html.Length && html[i] != quote) i++;
                            if (i == html.Length) throw Invalid("Unterminated KIND attribute value.");
                            value = html[start..i++];
                        }
                        else
                        {
                            start = i;
                            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                            {
                                if (html[i] is '<' or '\'' or '"' or '=' or '`') throw Invalid("Invalid unquoted KIND attribute.");
                                i++;
                            }
                            if (i == start) throw Invalid("Empty unquoted KIND attribute.");
                            value = html[start..i];
                        }
                    }
                    if (!node.Attributes.TryAdd(attribute, WebUtility.HtmlDecode(value))) throw Invalid("Duplicate KIND HTML attribute.");
                }
                if (++nodes > 20_000 || stack.Count > 128) throw Invalid("KIND HTML exceeds its structural bound.");
                stack.Peek().Children.Add(node); stack.Peek().parts.Add(node);
                if (tag is "script" or "style" or "title" or "textarea" or "noscript" or "template")
                {
                    if (selfClosing) throw Invalid("Unexpected self-closing KIND raw text tag.");
                    var end = html.IndexOf("</" + tag, i, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) throw Invalid("Unterminated KIND raw text tag.");
                    node.parts.Add(html[i..end]); i = end; stack.Push(node);
                }
                else if (!selfClosing && tag is not ("area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input" or "link" or "meta" or "param" or "source" or "track" or "wbr")) stack.Push(node);
            }
            if (stack.Count != 1) throw Invalid("Incomplete KIND HTML structure.");
            return root;
        }
        private static void SkipSpace(string value, ref int i) { while (i < value.Length && char.IsWhiteSpace(value[i])) i++; }
    }

    private static void ValidateTransport(HttpMessageHandler? handler)
    {
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            if (current is HttpClientHandler client && (client.AllowAutoRedirect || client.UseCookies ||
                client.AutomaticDecompression != DecompressionMethods.None || client.UseDefaultCredentials || client.Credentials is not null) ||
                current is SocketsHttpHandler sockets && (sockets.AllowAutoRedirect || sockets.UseCookies ||
                sockets.AutomaticDecompression != DecompressionMethods.None || sockets.Credentials is not null))
                throw Invalid("KIND transport must disable redirects, cookies, decompression and credentials.");
        }
    }
    public void Dispose() { http.Dispose(); pipeline.Dispose(); }
}
