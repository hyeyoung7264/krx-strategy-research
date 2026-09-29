using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Investment.Core;

public sealed record KindDelistingQuery(DateOnly From, DateOnly Through, string Market = "ALL", int PageSize = 100);
// IssuerId is an opaque KIND company identifier, not a ticker or ISIN.
public sealed record KindDelistingRow(string IssuerId, string CompanyName, string Market,
    DateOnly DelistingDate, string Reason, string Remarks);
public sealed record KindDelistingPage(KindDelistingQuery Query, int PageIndex, int PageCount, int TotalCount,
    DateTimeOffset ObservedAt, string Source, string RawHash, string RawHtml, KindDelistingRow[] Rows);
public sealed record KindDelistingSnapshot(string Id, KindDelistingQuery Query, DateTimeOffset ObservedAt,
    KindDelistingPage[] Pages)
{
    public void Validate(DateTimeOffset? now = null) => KindDelistingClient.Validate(this, now ?? DateTimeOffset.UtcNow);
}

/// <summary>
/// Reads the public KIND delisting-status HTML screen. This is not a supported Open API contract.
/// A historical event observed now does not establish original announcement time, exit liquidity or holder recovery.
/// </summary>
public sealed class KindDelistingClient : IDisposable
{
    public const string SourcePage = "https://kind.krx.co.kr/investwarn/delcompany.do?method=searchDelCompanyMain";
    public const int MaximumResponseBytes = 2_000_000;
    public const int MaximumPages = 100;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;

    public KindDelistingClient(HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        if (handler is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
            throw new ArgumentException("KIND transport must disable automatic redirects.", nameof(handler));
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KrxStrategyResearch/1.0");
        clock ??= () => DateTimeOffset.UtcNow;
        this.clock = clock;
    }

    public async Task<KindDelistingSnapshot> Fetch(KindDelistingQuery query, int maxPages = 10,
        TimeSpan? interval = null, Func<KindDelistingPage, Task>? preservePage = null, CancellationToken ct = default)
    {
        ValidateQuery(query, clock());
        if (maxPages is < 1 or > MaximumPages) throw new ArgumentException("KIND page budget must be between 1 and 100.");
        var pause = interval ?? TimeSpan.FromSeconds(1);
        if (pause < TimeSpan.FromMilliseconds(250) || pause > TimeSpan.FromSeconds(60))
            throw new ArgumentException("KIND request interval must be between 250 ms and 60 seconds.");
        var pages = new List<KindDelistingPage>();
        for (var index = 1; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (index > 1) await Task.Delay(pause, ct);
            var source = RequestSource(query, index);
            var raw = await Read(source, ct);
            var parsed = ParsePage(raw, query, index);
            var observed = clock();
            var page = new KindDelistingPage(query, index, parsed.PageCount, parsed.TotalCount,
                observed, source, Hash(raw), raw, parsed.Rows);
            ValidatePage(page, observed);
            if (pages.Count != 0 && (page.TotalCount != pages[0].TotalCount || page.PageCount != pages[0].PageCount ||
                page.ObservedAt < pages[^1].ObservedAt))
                throw new InvalidOperationException("KIND collection changed pagination or observation order; retain prior pages for review.");
            if (pages.SelectMany(p => p.Rows).Select(Key).Intersect(page.Rows.Select(Key)).Any())
                throw new InvalidOperationException("KIND duplicate event across pages; retain prior pages for review.");
            pages.Add(page);
            // Persist each validated page before checking whether the complete result exceeds this run's budget.
            if (preservePage is not null) await preservePage(page);
            ct.ThrowIfCancellationRequested();
            if (page.PageCount > maxPages)
                throw new InvalidOperationException("KIND page budget exceeded; validated pages were preserved if a callback was supplied.");
            if (index == page.PageCount) break;
        }
        var snapshot = new KindDelistingSnapshot(Guid.NewGuid().ToString("N"), query, clock(), pages.ToArray());
        snapshot.Validate(clock());
        return snapshot;
    }

    private async Task<string> Read(string source, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Referrer = new Uri(SourcePage);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri)
                throw new InvalidOperationException($"KIND HTTP status {(int)response.StatusCode} or redirect; no automatic retry.");
            if (response.Content.Headers.ContentType?.MediaType != "text/html")
                throw new InvalidOperationException("KIND status screen must return HTML.");
            var charset = response.Content.Headers.ContentType.CharSet?.Trim('"');
            if (!string.IsNullOrEmpty(charset) && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("KIND status screen returned an unexpected character encoding.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidOperationException("KIND response too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, timeout.Token)) != 0)
            {
                if (buffer.Length + count > MaximumResponseBytes) throw new InvalidOperationException("KIND response too large.");
                buffer.Write(bytes, 0, count);
            }
            return new UTF8Encoding(false, true).GetString(buffer.ToArray());
        }
        catch (HttpRequestException) { throw new InvalidOperationException("KIND network failure; no automatic retry."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("KIND request timed out; no automatic retry."); }
    }

    public static string RequestSource(KindDelistingQuery query, int pageIndex)
    {
        ValidateQueryShape(query);
        if (pageIndex is < 1 or > MaximumPages) throw new ArgumentException("Invalid KIND page index.");
        return "https://kind.krx.co.kr/investwarn/delcompany.do?method=searchDelCompanySub" +
            "&currentPageSize=" + query.PageSize.ToString(CultureInfo.InvariantCulture) +
            "&pageIndex=" + pageIndex.ToString(CultureInfo.InvariantCulture) +
            "&fromDate=" + query.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            "&toDate=" + query.Through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            "&marketType=" + MarketCode(query.Market);
    }

    public static (int PageCount, int TotalCount, KindDelistingRow[] Rows) ParsePage(
        string html, KindDelistingQuery query, int requestedPage)
    {
        ValidateQueryShape(query);
        if (requestedPage is < 1 or > MaximumPages || Encoding.UTF8.GetByteCount(html) > MaximumResponseBytes)
            throw new ArgumentException("Invalid KIND page or response size.");
        // Match only the observed status table and paging sections; never evaluate embedded script.
        var tables = Matches(html, @"<table\b(?<attrs>[^>]*)>(?<body>.*?)</table\s*>")
            .Where(m => Attribute(m.Groups["attrs"].Value, "summary") == "번호, 회사명, 폐지일자, 폐지사유, 비고").ToArray();
        if (tables.Length != 1) throw new InvalidOperationException("KIND delisting table missing or ambiguous; not an empty result.");
        var table = tables[0];
        if (!Classes(table.Groups["attrs"].Value).Contains("list")) throw new InvalidOperationException("Unexpected KIND table class.");
        var bodies = Matches(table.Groups["body"].Value, @"<tbody\b[^>]*>(?<body>.*?)</tbody\s*>");
        if (bodies.Length != 1) throw new InvalidOperationException("KIND status table requires one body.");
        var paging = Matches(html, @"<section\b(?<attrs>[^>]*)>(?<body>.*?)</section\s*>")
            .Where(m => Classes(m.Groups["attrs"].Value).Contains("paging-group")).ToArray();
        if (paging.Length != 1) throw new InvalidOperationException("KIND paging section missing or ambiguous.");
        var footer = Matches(paging[0].Groups["body"].Value,
            @"전체\s*<em\b[^>]*>\s*(?<total>[0-9]+)\s*</em>\s*건\s*:\s*<strong\b[^>]*>\s*(?<page>[0-9]+)\s*</strong>\s*/\s*(?<pages>[0-9]+)(?![0-9])");
        if (footer.Length != 1) throw new InvalidOperationException("KIND total/page metadata missing or ambiguous.");
        var total = Integer(footer[0].Groups["total"].Value);
        var page = Integer(footer[0].Groups["page"].Value);
        var pageCount = Integer(footer[0].Groups["pages"].Value);
        if (total == 0) throw new InvalidOperationException("KIND zero-row structure is not independently verified; review required.");
        if (total > MaximumPages * query.PageSize || page != requestedPage || pageCount is < 1 or > MaximumPages ||
            page > pageCount || pageCount != (total + query.PageSize - 1) / query.PageSize)
            throw new InvalidOperationException("KIND page/total count mismatch or exceeds collection bound.");
        var selectors = Matches(paging[0].Groups["body"].Value, @"<select\b(?<attrs>[^>]*)>(?<body>.*?)</select\s*>")
            .Where(m => Attribute(m.Groups["attrs"].Value, "name") == "currentPageSize").ToArray();
        if (selectors.Length != 1) throw new InvalidOperationException("KIND page size selector missing or ambiguous.");
        var selected = Matches(selectors[0].Groups["body"].Value, @"<option\b(?<attrs>[^>]*)>.*?</option\s*>")
            .Where(m => IsSelected(m.Groups["attrs"].Value)).ToArray();
        if (selected.Length != 1 || Attribute(selected[0].Groups["attrs"].Value, "value") != query.PageSize.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("KIND response page size differs from request.");
        var body = bodies[0].Groups["body"].Value;
        var rowMatches = Matches(body, @"<tr\b[^>]*>(?<body>.*?)</tr\s*>");
        if (RemoveMatches(body, rowMatches).Trim().Length != 0 || rowMatches.Length != Math.Min(query.PageSize, total - (page - 1) * query.PageSize))
            throw new InvalidOperationException("KIND row structure/count mismatch.");
        var rows = rowMatches.Select((row, index) => ParseRow(row.Groups["body"].Value, query, total - (page - 1) * query.PageSize - index)).ToArray();
        if (rows.Select(Key).Distinct().Count() != rows.Length) throw new InvalidOperationException("KIND duplicate delisting event.");
        return (pageCount, total, rows);
    }

    private static KindDelistingRow ParseRow(string row, KindDelistingQuery query, int expectedNumber)
    {
        var cells = Matches(row, @"<td\b[^>]*>(?<body>.*?)</td\s*>");
        if (cells.Length != 5 || RemoveMatches(row, cells).Trim().Length != 0)
            throw new InvalidOperationException("KIND delisting row must contain five cells.");
        var text = cells.Select(c => PlainText(c.Groups["body"].Value)).ToArray();
        if (Integer(text[0]) != expectedNumber) throw new InvalidOperationException("KIND row number/page mismatch.");
        var companyCell = cells[1].Groups["body"].Value;
        var anchors = Matches(companyCell, @"<a\b(?<attrs>[^>]*)>(?<body>.*?)</a\s*>");
        if (anchors.Length != 1) throw new InvalidOperationException("KIND company anchor missing or ambiguous.");
        var action = Attribute(anchors[0].Groups["attrs"].Value, "onclick");
        var issuer = Match(action, @"^\s*companysummary_open\('(?<id>[A-Za-z0-9]{5})'\);\s*return\s+false;\s*$");
        var name = PlainText(anchors[0].Groups["body"].Value);
        if (!issuer.Success || name.Length is < 1 or > 200 || Attribute(anchors[0].Groups["attrs"].Value, "title") != name)
            throw new InvalidOperationException("KIND company identity/name mismatch.");
        var alts = Matches(companyCell, @"<img\b(?<attrs>[^>]*)>").Select(m => Attribute(m.Groups["attrs"].Value, "alt")).ToArray();
        var marketLabels = alts.Where(alt => alt is "유가증권" or "코스닥" or "코넥스").ToArray();
        var delistingBadges = alts.Count(alt => alt == "상장폐지");
        if (marketLabels.Length != 1 || delistingBadges > 1)
            throw new InvalidOperationException("KIND market/delisting status evidence missing or ambiguous.");
        var market = marketLabels[0] switch { "유가증권" => "KOSPI", "코스닥" => "KOSDAQ", _ => "KONEX" };
        // The verified historical delisting table and its event fields establish the listed event.
        // The current company-status badge is optional: historical transfer/merger rows can omit it.
        // Remarks may describe a related listing; they do not certify security identity or holder entitlement.
        if (query.Market != "ALL" && query.Market != market ||
            !DateOnly.TryParseExact(text[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date < query.From || date > query.Through || text[3].Length is < 1 or > 4000 || text[4].Length > 4000)
            throw new InvalidOperationException("KIND market/date/reason outside requested structure or range.");
        return new(issuer.Groups["id"].Value, name, market, date, text[3], text[4]);
    }

    public static void Validate(KindDelistingSnapshot snapshot, DateTimeOffset now)
    {
        ValidateQuery(snapshot.Query, now);
        if (!Guid.TryParseExact(snapshot.Id, "N", out _) || snapshot.ObservedAt > now || snapshot.Pages is not { Length: > 0 and <= MaximumPages })
            throw new ArgumentException("Invalid KIND snapshot identity, observation time or pages.");
        for (var i = 0; i < snapshot.Pages.Length; i++)
        {
            var page = snapshot.Pages[i];
            ValidatePage(page, now);
            if (page.Query != snapshot.Query || page.PageIndex != i + 1 || page.PageCount != snapshot.Pages.Length ||
                page.TotalCount != snapshot.Pages[0].TotalCount || page.ObservedAt > snapshot.ObservedAt ||
                i != 0 && page.ObservedAt < snapshot.Pages[i - 1].ObservedAt)
                throw new ArgumentException("KIND snapshot query/page/observation sequence mismatch.");
        }
        var rows = snapshot.Pages.SelectMany(p => p.Rows).ToArray();
        if (rows.Length != snapshot.Pages[0].TotalCount || rows.Select(Key).Distinct().Count() != rows.Length)
            throw new ArgumentException("KIND snapshot incomplete or duplicate events.");
    }

    public static void ValidatePage(KindDelistingPage page, DateTimeOffset now)
    {
        ValidateQuery(page.Query, now);
        ValidateQuery(page.Query, page.ObservedAt);
        if (page.ObservedAt > now || page.Source != RequestSource(page.Query, page.PageIndex) || page.RawHash != Hash(page.RawHtml))
            throw new ArgumentException("KIND page source/hash/observation mismatch.");
        var parsed = ParsePage(page.RawHtml, page.Query, page.PageIndex);
        if (page.PageCount != parsed.PageCount || page.TotalCount != parsed.TotalCount || !parsed.Rows.SequenceEqual(page.Rows))
            throw new ArgumentException("KIND raw/normalized page mismatch.");
    }

    private static void ValidateQuery(KindDelistingQuery query, DateTimeOffset now)
    {
        ValidateQueryShape(query);
        if (query.Through > DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(9)).DateTime))
            throw new ArgumentException("KIND request range must not end in the future.");
    }
    private static void ValidateQueryShape(KindDelistingQuery query)
    {
        if (query.From > query.Through || query.From.Year < 1900 || query.PageSize is not (15 or 30 or 50 or 100))
            throw new ArgumentException("Invalid KIND date range or page size.");
        _ = MarketCode(query.Market);
    }
    private static string MarketCode(string market) => market switch
    { "ALL" => "", "KOSPI" => "1", "KOSDAQ" => "2", "KONEX" => "6", _ => throw new ArgumentException("Invalid KIND market.") };
    private static (string, string, DateOnly) Key(KindDelistingRow row) => (row.Market, row.IssuerId, row.DelistingDate);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Match Match(string value, string pattern) => Regex.Match(value, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
    private static Match[] Matches(string value, string pattern) => Regex.Matches(value, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout).Cast<Match>().ToArray();
    private static string RemoveMatches(string value, Match[] matches)
    {
        var result = new StringBuilder(value);
        foreach (var match in matches.Reverse()) result.Remove(match.Index, match.Length);
        return result.ToString();
    }
    private static string Attribute(string attributes, string name)
    {
        return Attributes(attributes).GetValueOrDefault(name, "");
    }
    private static Dictionary<string, string> Attributes(string attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest = attributes.Trim();
        while (rest.Length != 0 && rest != "/")
        {
            var attribute = Match(rest, @"^(?<name>[A-Za-z_:][A-Za-z0-9_.:-]*)(?:\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'))?(?=\s|/|$)");
            if (!attribute.Success || !result.TryAdd(attribute.Groups["name"].Value,
                    WebUtility.HtmlDecode(attribute.Groups["value"].Value)))
                throw new InvalidOperationException("Malformed or duplicate KIND HTML attribute.");
            rest = rest[attribute.Length..].TrimStart();
        }
        return result;
    }
    private static string[] Classes(string attributes) => Attribute(attributes, "class").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static bool IsSelected(string attributes) => Attributes(attributes).TryGetValue("selected", out var selected) &&
        (selected.Length == 0 || selected.Equals("selected", StringComparison.OrdinalIgnoreCase));
    private static string PlainText(string html)
    {
        if (Match(html, @"<\s*(script|style|table|tr|td|iframe|form)\b").Success)
            throw new InvalidOperationException("Unexpected nested structure in KIND cell.");
        var text = WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]*>", " ", RegexOptions.Singleline, RegexTimeout));
        if (text.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c))) throw new InvalidOperationException("Invalid KIND text control character.");
        return Regex.Replace(text, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }
    private static int Integer(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        ? number : throw new InvalidOperationException("Invalid KIND integer field.");
    public void Dispose() => http.Dispose();
}
