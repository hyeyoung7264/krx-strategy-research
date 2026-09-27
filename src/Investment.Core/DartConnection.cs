using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Investment.Core;

public sealed record DartConnectionReceipt(string Id, string CorpCode, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, string Status, bool Authenticated, int? HttpStatus = null,
    string? ApiStatus = null, string? RedirectClass = null, string? ResponseHash = null,
    CompanySnapshot? Company = null,
    string Note = "One read-only request; no automatic retry. Authentication does not certify historical data or investment eligibility.");

public static class DartConnection
{
    public static async Task<DartConnectionReceipt> Check(HttpClient http, string apiKey, string corpCode,
        string id, CancellationToken ct = default)
    {
        if (apiKey.Length != 40 || !apiKey.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Set a 40-character OPENDART_API_KEY locally; do not pass keys in arguments.");
        if (corpCode.Length != 8 || !corpCode.All(char.IsAsciiDigit))
            throw new ArgumentException("corp_code must be eight digits.");
        var started = DateTimeOffset.UtcNow;
        int? status = null; string? hash = null;
        DartConnectionReceipt Result(string outcome, string? apiStatus = null, string? redirect = null,
            CompanySnapshot? company = null) => new(id, corpCode, started, DateTimeOffset.UtcNow,
                outcome, outcome == "AUTHENTICATED", status, apiStatus, redirect, hash, company);
        // Caller must disable automatic redirects. Never publish query strings, arbitrary response
        // messages, Location values or exceptions: each may contain the authentication key.
        var uri = new Uri("https://opendart.fss.or.kr/api/company.json?crtfc_key=" +
            Uri.EscapeDataString(apiKey) + "&corp_code=" + corpCode);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                var destination = location == null ? null : new Uri(uri, location);
                var category = destination == null ? "MISSING_LOCATION" :
                    destination.Scheme != "https" || destination.Host != "opendart.fss.or.kr" || destination.Port != 443
                        ? "UNTRUSTED_DESTINATION" : destination.AbsolutePath == "/error1.html"
                        ? "OFFICIAL_ERROR_PAGE" : "OTHER_OFFICIAL_DESTINATION";
                return Result("REDIRECT_REFUSED", redirect: category);
            }
            if (response.StatusCode != HttpStatusCode.OK) return Result("HTTP_REJECTED");
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var body = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, ct); if (count == 0) break;
                if (body.Length + count > 1_000_000) return Result("RESPONSE_TOO_LARGE");
                body.Write(buffer, 0, count);
            }
            var bytes = body.ToArray(); hash = Convert.ToHexString(SHA256.HashData(bytes));
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            if (json.Contains(apiKey, StringComparison.Ordinal)) return Result("RESPONSE_CONTAINS_CREDENTIAL");
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var apiCode) ||
                apiCode.ValueKind != JsonValueKind.String) return Result("INVALID_RESPONSE");
            var code = apiCode.GetString();
            if (code is not ("000" or "010" or "011" or "012" or "013" or "014" or "020" or "021" or "100" or "101" or "800" or "900" or "901"))
                return Result("INVALID_RESPONSE");
            if (code != "000") return Result("API_REJECTED", code);
            if (!root.TryGetProperty("corp_code", out var corp) || corp.ValueKind != JsonValueKind.String || corp.GetString() != corpCode ||
                !root.TryGetProperty("corp_name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                !root.TryGetProperty("stock_code", out var stock) || stock.ValueKind != JsonValueKind.String)
                return Result("INVALID_RESPONSE");
            var stockCode = stock.GetString()!;
            if (stockCode != "" && (stockCode.Length != 6 || !stockCode.All(char.IsAsciiDigit))) return Result("INVALID_RESPONSE");
            return Result("AUTHENTICATED", code, company: new(corpCode, name.GetString()!, stockCode, DateTimeOffset.UtcNow, json));
        }
        catch (HttpRequestException) { return Result("NETWORK_ERROR"); }
        catch (OperationCanceledException) { return Result(ct.IsCancellationRequested ? "CANCELLED" : "TIMEOUT"); }
        catch (JsonException) { return Result("INVALID_RESPONSE"); }
        catch (UriFormatException) { return Result("INVALID_REDIRECT"); }
        catch (IOException) { return Result("READ_FAILED"); }
    }
}
