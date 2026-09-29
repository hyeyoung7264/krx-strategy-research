using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class CorporateActionAdversarialTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly CorporateSecurityIdentity Security = new("KOSPI", "005930", "KR7005930003", "synthetic-listing-episode");
    private static DateTimeOffset Time(int month, int day) => new(2024, month, day, 0, 0, 0, TimeSpan.Zero);

    [Fact] public void RetrospectivePublicationNeverBypassesTheRecordedCutoffOrBecomesObservedEvidence()
    {
        var notice = Notice(1, Time(6, 1));
        var revision = Revision("initial", null, notice, Time(6, 2), Time(1, 2));
        var ledger = new CorporateActionLedger([revision], [notice]);
        Assert.Empty(ledger.At(Time(1, 3), Time(6, 1), "REVIEWED_PUBLICATION", Now).Actions);
        Assert.Empty(ledger.At(Time(1, 3), Time(6, 2), "OBSERVED", Now).Actions);
        var historical = ledger.At(Time(1, 3), Time(6, 2), "REVIEWED_PUBLICATION", Now);
        Assert.Equal("initial", Assert.Single(historical.Actions).Revision.RevisionKey);
        Assert.Equal(notice.ObservedAt, historical.Actions[0].ObservedAt);
        Assert.Equal(Time(1, 2), historical.Actions[0].AvailableAt);
        Assert.True(historical.RetrospectiveTiming);
        Assert.Contains("not certification", historical.Note);
        Assert.Contains("retrospective", historical.Note);
    }

    [Fact] public void UnreviewedCancellationKeepsObservedTimingAndCannotRewriteAnEarlierQuery()
    {
        var firstNotice = Notice(1, Time(6, 1));
        var first = Revision("initial", null, firstNotice, Time(6, 1).AddHours(1), Time(1, 2));
        var original = new CorporateActionLedger([first], [firstNotice]);
        var cancellationNotice = Notice(2, Time(6, 2));
        var cancellation = Revision("cancelled", "initial", cancellationNotice,
            Time(6, 2).AddHours(1), null) with { Status = "CANCELLED", ShareChange = null };
        var expanded = original.Append(cancellation, [cancellationNotice], Now);
        var oldQuery = original.At(Time(1, 20), Time(7, 1), "REVIEWED_PUBLICATION", Now);
        var stillOld = expanded.At(Time(1, 20), Time(7, 1), "REVIEWED_PUBLICATION", Now);
        Assert.Equal(oldQuery.Hash, stillOld.Hash);
        Assert.Equal("initial", Assert.Single(stillOld.Actions).Revision.RevisionKey);
        var current = expanded.At(Time(6, 3), Time(7, 1), "REVIEWED_PUBLICATION", Now);
        Assert.Equal("CANCELLED", Assert.Single(current.Actions).Revision.Status);
        Assert.Equal(cancellationNotice.ObservedAt, current.Actions[0].AvailableAt);
        Assert.False(current.RetrospectiveTiming);
        Assert.Equal("REVIEWED_PUBLICATION", current.TimingMode);
        Assert.Contains("missing publication proof retains observed timing", current.Note);
    }

    [Fact] public void ChangedFutureLegalDateAndCancellationNeverResurrectOldTerms()
    {
        var firstNotice = Notice(1, Time(1, 2));
        var first = Revision("initial", null, firstNotice, Time(1, 2).AddHours(1), null) with
        { ShareChange = new(2, 1, "synthetic fraction review", new(2024, 1, 20)) };
        var revisedNotice = Notice(2, Time(1, 10));
        var revised = Revision("postponed", "initial", revisedNotice, Time(1, 10).AddHours(1), null) with
        { ShareChange = new(3, 1, "synthetic revised fraction review", new(2024, 12, 20)) };
        var ledger = new CorporateActionLedger([first, revised], [firstNotice, revisedNotice]);
        var current = Assert.Single(ledger.At(Time(2, 1), Time(2, 1), now: Now).Actions);
        Assert.Equal("postponed", current.Revision.RevisionKey);
        Assert.Equal(new DateOnly(2024, 12, 20), current.Revision.ShareChange!.LegalEffectiveDate);
        var cancelledNotice = Notice(3, Time(1, 20));
        var cancelled = Revision("cancelled", "postponed", cancelledNotice, Time(1, 20).AddHours(1), null) with
        { Status = "CANCELLED", ShareChange = null };
        var terminal = ledger.Append(cancelled, [cancelledNotice], Now);
        var cancellation = Assert.Single(terminal.At(Time(2, 1), Time(2, 1), now: Now).Actions);
        Assert.Equal("cancelled", cancellation.Revision.RevisionKey);
        Assert.Null(cancellation.Revision.ShareChange);
        Assert.Equal("CANCELLED", cancellation.Revision.Status);
    }

    [Fact] public void MutatingInputsAndReturnedNestedArraysCannotRewriteTheLedgerOrFrozenQuery()
    {
        var notice = Notice(1, Time(1, 2));
        var revision = Revision("initial", null, notice, Time(1, 3), null);
        var revisions = new[] { revision }; var snapshots = new[] { notice };
        var ledger = new CorporateActionLedger(revisions, snapshots);
        var query = ledger.At(Time(2, 1), Time(2, 1), now: Now);
        var ledgerHash = ledger.Hash; var queryHash = query.Hash;
        revision.Evidence[0] = revision.Evidence[0] with { ReviewNote = "caller changed evidence" };
        revisions[0] = revision with { Status = "CANCELLED", ShareChange = null };
        snapshots[0] = notice with { RawBase64 = "AAAA" };
        ledger.Revisions[0].Evidence[0] = revision.Evidence[0];
        ledger.Snapshots[0] = snapshots[0];
        query.Actions[0].Revision.Evidence[0] = revision.Evidence[0];
        ledger.Validate(Now);
        Assert.Equal(ledgerHash, ledger.Hash);
        Assert.Equal(queryHash, query.Hash);
        Assert.Equal("synthetic manual field review", query.Actions[0].Revision.Evidence[0].ReviewNote);
        Assert.Equal("ANNOUNCED", ledger.Revisions[0].Status);
    }

    [Fact] public void MissingHistoricalPublicationTimeInJsonCannotMeanKnownSinceYearOne()
    {
        var notice = Notice(1, Time(6, 1));
        var revision = Revision("initial", null, notice, Time(6, 2), Time(1, 2));
        var ledger = new CorporateActionLedger([revision], [notice]);
        var json = JsonNode.Parse(JsonSerializer.Serialize(ledger))!;
        Assert.True(json["Revisions"]![0]!["HistoricalAvailability"]!.AsObject().Remove("AvailableAt"));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<CorporateActionLedger>(json.ToJsonString()));
    }

    [Fact] public void AddingFutureActionSourcesChangesLedgerHashButNeitherEarlierQueryMode()
    {
        var notice = Notice(1, Time(1, 2));
        var first = Revision("initial", null, notice, Time(1, 3), Time(1, 1));
        var ledger = new CorporateActionLedger([first], [notice]);
        var futureNotice = Notice(2, Time(8, 1));
        var future = Revision("future", null, futureNotice, Time(8, 2), Time(7, 31)) with { ActionKey = "other-action" };
        var expanded = ledger.Append(future, [futureNotice], Now);
        Assert.NotEqual(ledger.Hash, expanded.Hash);
        foreach (var mode in new[] { "OBSERVED", "REVIEWED_PUBLICATION" })
            Assert.Equal(ledger.At(Time(6, 1), Time(9, 1), mode, Now).Hash,
                expanded.At(Time(6, 1), Time(9, 1), mode, Now).Hash);
    }

    [Fact] public void SameBodyHashCannotSubstituteAnotherSourceOrObservationInEvidence()
    {
        var original = Notice(1, Time(1, 2));
        var revision = Revision("initial", null, original, Time(1, 4), null);
        var otherUrl = original with { Source = Notice(2, Time(1, 2)).Source };
        var otherObservation = original with { ObservedAt = Time(1, 3) };
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision], [otherUrl]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision], [otherObservation]));
    }

    [Fact] public void LedgerAndItsVisibleQueriesSurviveJsonWithoutCertifyingOrRevivingCancelledTerms()
    {
        var original = Notice(1, Time(1, 2)); var cancelled = Notice(2, Time(1, 10));
        var first = Revision("initial", null, original, Time(1, 3), Time(1, 1));
        var last = Revision("cancelled", "initial", cancelled, Time(1, 11), Time(1, 9)) with
        { Status = "CANCELLED", ShareChange = null };
        var ledger = new CorporateActionLedger([last, first], [cancelled, original]);
        var restored = JsonSerializer.Deserialize<CorporateActionLedger>(JsonSerializer.Serialize(ledger))!;
        restored.Validate(Now); Assert.Equal(ledger.Hash, restored.Hash);
        foreach (var mode in new[] { "OBSERVED", "REVIEWED_PUBLICATION" })
        {
            var expected = ledger.At(Time(2, 1), Time(2, 1), mode, Now);
            var actual = restored.At(Time(2, 1), Time(2, 1), mode, Now);
            var roundTrip = JsonSerializer.Deserialize<CorporateActionQueryResult>(JsonSerializer.Serialize(actual))!;
            Assert.Equal(expected.Hash, actual.Hash); Assert.Equal(actual.Hash, roundTrip.Hash);
            Assert.Equal("CANCELLED", Assert.Single(roundTrip.Actions).Revision.Status);
            Assert.Null(roundTrip.Actions[0].Revision.ShareChange);
            Assert.Equal(mode == "REVIEWED_PUBLICATION", roundTrip.RetrospectiveTiming);
        }
    }

    private static KindNoticeSnapshot Notice(int number, DateTimeOffset observed)
    {
        var raw = Encoding.UTF8.GetBytes($"<html>synthetic evidence notice {number}</html>");
        return new($"https://kind.krx.co.kr/external/2024/01/01/000001/2024010100{number:0000}/00001.htm",
            observed, "text/html", "UTF-8", Convert.ToHexString(SHA256.HashData(raw)), Convert.ToBase64String(raw));
    }
    private static CorporateActionRevision Revision(string key, string? prior, KindNoticeSnapshot notice,
        DateTimeOffset recorded, DateTimeOffset? published) => new("synthetic-action", key, prior, Security,
            "SHARE_UNIT_CHANGE", "ANNOUNCED", recorded,
            [new(notice.Source, notice.RawHash, notice.ObservedAt, "synthetic terms field", "synthetic manual field review")],
            new(2, 1, "synthetic fraction review"), HistoricalAvailability: published is { } known
                ? new(known, notice.RawHash, "synthetic publication field", "synthetic manual timing review") : null);
}
