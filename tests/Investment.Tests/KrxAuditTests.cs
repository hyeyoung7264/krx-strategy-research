using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class KrxAuditTests
{
    private static readonly DateOnly First = new(2026, 9, 21);
    private static readonly DateTimeOffset Now = Clock.Close(First.AddDays(3));
    private static KrxRow Row(DateOnly date, string ticker = "000001", decimal close = 100, long shares = 1000) =>
        new(date, ticker, "fixture", "KOSPI", "section", close, close + 1, close - 1, close, 100, close * 100, close * shares, shares);
    private static KrxSnapshot Snapshot(DateOnly date, params KrxRow[] rows)
    {
        string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var raw = JsonSerializer.Serialize(new { OutBlock_1 = rows.Select(r => new
        {
            BAS_DD = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), ISU_CD = r.Ticker, ISU_NM = r.Company, MKT_NM = r.Market,
            SECT_TP_NM = r.ListingSection, TDD_OPNPRC = Number(r.Open), TDD_HGPRC = Number(r.High), TDD_LWPRC = Number(r.Low),
            TDD_CLSPRC = Number(r.Close), ACC_TRDVOL = Number(r.Volume), ACC_TRDVAL = Number(r.TradingValue), MKTCAP = Number(r.MarketCap), LIST_SHRS = Number(r.SharesOutstanding)
        }).ToArray() });
        return new(Guid.NewGuid().ToString("N"), "KOSPI", date, Clock.Close(date).AddHours(1),
            "https://data-dbg.krx.co.kr/svc/apis/sto/stk_bydd_trd", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), raw,
            KrxClient.Parse(raw, date, "KOSPI"));
    }

    [Fact] public void AuditDistinguishesQuotedOrPriorCloseWithoutTurnoverFromMissingOrBrokenPrices()
    {
        var noTrade = Row(First) with { Open = 0, High = 0, Low = 0, Volume = 0, TradingValue = 0 };
        var missing = Row(First, "000002") with { Open = null };
        var broken = Row(First, "000003") with { High = 10 };
        var result = KrxDataAudit.Run([Snapshot(First, noTrade, missing, broken)], [First], ["KOSPI"], Now);
        Assert.Equal(1, Assert.Single(result.Days).NoTradeRows);
        Assert.Contains(result.Issues, i => i.Code == "MISSING_OHLCV" && i.Ticker == "000002");
        Assert.Contains(result.Issues, i => i.Code == "INVALID_OHLCV" && i.Ticker == "000003");
        Assert.DoesNotContain(result.Issues, i => i.Ticker == "000001");
    }

    [Fact] public void ZeroRegularOhlWithPositiveTurnoverRemainsAnExplicitReviewWithoutChangingRawRowsOrNoTradeCounts()
    {
        // The reported close need not equal the average execution price of the non-regular turnover.
        var mixed = Row(First) with { Open = 0, High = 0, Low = 0, Volume = 7, TradingValue = 665 };
        var zeroTurnover = Row(First, "000002") with { Open = 0, High = 0, Low = 0, Volume = 0, TradingValue = 0 };
        var source = Snapshot(First, mixed, zeroTurnover, Row(First, "000003"));
        var before = JsonSerializer.Serialize(source);
        var result = KrxDataAudit.Run([source], [First], ["KOSPI"], Now);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("NON_REGULAR_TRADING_REQUIRES_REVIEW", issue.Code);
        Assert.Equal(mixed.Ticker, issue.Ticker);
        Assert.Equal("REVIEW_REQUIRED", result.Status);
        Assert.Equal(1, Assert.Single(result.Days).NoTradeRows);
        Assert.Equal(3, Assert.Single(result.Days).Rows);
        Assert.Contains("quotation-based or a prior price", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("separate evidence", issue.Detail, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(source));
        Assert.Equal(source.RawHash, Assert.Single(result.Inputs).RawHash);
        Assert.Equal(source.ObservedAt, Assert.Single(result.Inputs).ObservedAt);
    }

    [Theory]
    [InlineData("zero-volume")]
    [InlineData("zero-value")]
    [InlineData("zero-close")]
    [InlineData("positive-open-only")]
    [InlineData("positive-high-only")]
    [InlineData("positive-low-only")]
    [InlineData("zero-open-only")]
    [InlineData("zero-high-only")]
    [InlineData("zero-low-only")]
    public void InconsistentTurnoverOrPartialZeroOhlDoesNotBecomeNonRegularTrading(string mutation)
    {
        var mixed = Row(First) with { Open = 0, High = 0, Low = 0, Volume = 7, TradingValue = 665 };
        var row = mutation switch
        {
            "zero-volume" => mixed with { Volume = 0 },
            "zero-value" => mixed with { TradingValue = 0 },
            "zero-close" => mixed with { Close = 0 },
            "positive-open-only" => mixed with { Open = 100 },
            "positive-high-only" => mixed with { High = 100 },
            "positive-low-only" => mixed with { Low = 100 },
            "zero-open-only" => Row(First) with { Open = 0 },
            "zero-high-only" => Row(First) with { High = 0 },
            _ => Row(First) with { Low = 0 }
        };
        var result = KrxDataAudit.Run([Snapshot(First, row)], [First], ["KOSPI"], Now);
        Assert.Equal("INVALID_OHLCV", Assert.Single(result.Issues).Code);
        Assert.Equal(0, Assert.Single(result.Days).NoTradeRows);
        Assert.Equal("REVIEW_REQUIRED", result.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingTurnoverFieldStillRequiresMissingDataReview(bool missingVolume)
    {
        var mixed = Row(First) with { Open = 0, High = 0, Low = 0, Volume = 7, TradingValue = 665 };
        var row = missingVolume ? mixed with { Volume = null } : mixed with { TradingValue = null };
        var result = KrxDataAudit.Run([Snapshot(First, row)], [First], ["KOSPI"], Now);
        Assert.Equal("MISSING_OHLCV", Assert.Single(result.Issues).Code);
        Assert.Equal(0, Assert.Single(result.Days).NoTradeRows);
    }

    [Fact] public void NonRegularClassificationCannotBypassRawProvenanceOrDatasetPriceValidation()
    {
        var mixed = Row(First) with { Open = 0, High = 0, Low = 0, Volume = 7, TradingValue = 665 };
        var source = Snapshot(First, mixed);
        var zeroTurnover = Snapshot(First, mixed with { Volume = 0, TradingValue = 0 });
        Assert.Throws<ArgumentException>(() => KrxDataAudit.Run([source with { Rows = zeroTurnover.Rows }], [First], ["KOSPI"], Now));
        Assert.Throws<ArgumentException>(() => KrxDataAudit.Run([source with { RawJson = zeroTurnover.RawJson }], [First], ["KOSPI"], Now));
        Assert.Throws<ArgumentException>(() => KrxDataAudit.Run([source with { RawJson = zeroTurnover.RawJson, RawHash = zeroTurnover.RawHash }], [First], ["KOSPI"], Now));
        var manifest = new KrxManifest("synthetic-review", [], [First],
            [new(mixed.Ticker, First, "UNREVIEWED", false, false, source.ObservedAt)]);
        Assert.Throws<ArgumentException>(() => KrxDatasetBuilder.Build([source], manifest));
    }

    [Fact] public void ObservedChangesRemainReviewFlagsRatherThanLifecycleProof()
    {
        var second = First.AddDays(1);
        var a = Snapshot(First, Row(First), Row(First, "000002"));
        var b = Snapshot(second, Row(second, close: 200, shares: 500), Row(second, "000003"));
        var result = KrxDataAudit.Run([a, b], [First, second], ["KOSPI"], Now);
        Assert.Equal(4, result.Changes.Length);
        Assert.Contains(result.Changes, c => c.Kind == "APPEARED_REQUIRES_LISTING_EVIDENCE" && c.Ticker == "000003");
        Assert.Contains(result.Changes, c => c.Kind == "DISAPPEARED_REQUIRES_EXIT_EVIDENCE" && c.Ticker == "000002");
        Assert.Equal("REVIEW_REQUIRED", result.Status);
        Assert.Equal(result.InputHash, KrxDataAudit.Run([b, a], [First, second], ["KOSPI"], Now).InputHash);
    }

    [Fact] public void MissingAndEmptySessionsNeverBecomeHolidaysOrContinuousComparisons()
    {
        var last = First.AddDays(2);
        var a = Snapshot(First, Row(First)); var b = Snapshot(last, Row(last, "000002"));
        var missing = KrxDataAudit.Run([a, b], [First, First.AddDays(1), last], ["KOSPI"], Now);
        Assert.Equal("MISSING_SNAPSHOT", Assert.Single(missing.Issues).Code);
        Assert.Empty(missing.Changes);
        var empty = KrxDataAudit.Run([a, Snapshot(First.AddDays(1)), b], [First, First.AddDays(1), last], ["KOSPI"], Now);
        Assert.Equal("EMPTY_RESPONSE", Assert.Single(empty.Issues).Code);
        Assert.Empty(empty.Changes);
    }

    [Fact] public void AuditRejectsEditedProvenanceAndAmbiguousRevisions()
    {
        var valid = Snapshot(First, Row(First));
        Assert.Throws<ArgumentException>(() => KrxDataAudit.Run([valid with { RawHash = "changed" }], [First], ["KOSPI"], Now));
        Assert.Throws<ArgumentException>(() => KrxDataAudit.Run([valid with { Rows = [] }], [First], ["KOSPI"], Now));
        Assert.Throws<ArgumentException>(() => KrxDataAudit.Run([valid with { Source = "https://untrusted.invalid" }], [First], ["KOSPI"], Now));
        Assert.Throws<ArgumentException>(() => KrxDataAudit.Run([valid with { ObservedAt = Now.AddDays(1) }], [First], ["KOSPI"], Now));
        Assert.Throws<ArgumentException>(() => KrxDataAudit.Run([valid, valid], [First], ["KOSPI"], Now));
    }
}
