using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class CorporateActionTests
{
    private const string Source = "https://kind.krx.co.kr/external/2024/11/15/000107/20241115000180/68155.htm";
    private static DateTimeOffset Time(int day) => new(2025, 1, day, 12, 0, 0, TimeSpan.FromHours(9));
    private static readonly CorporateSecurityIdentity Security = new("KOSPI", "005930", "KR7005930003", "samsung-listing-fixture");
    private static KindNoticeSnapshot Notice(int day)
    {
        var bytes = Encoding.UTF8.GetBytes($"<html>synthetic notice {day}</html>");
        return new(Source, Time(day), "text/html", "UTF-8", Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToBase64String(bytes));
    }
    private static CorporateActionRevision Split(KindNoticeSnapshot notice, string revision = "r1", string? supersedes = null) =>
        new("action-one", revision, supersedes, Security, "SHARE_UNIT_CHANGE", "ANNOUNCED", notice.ObservedAt.AddMinutes(1),
            [new(notice.Source, notice.RawHash, notice.ObservedAt, "table:ratio", "manually reviewed synthetic fixture")],
            new(5, 1, "fraction handling has not been certified"));

    [Fact] public void HistoricalPublicationRequiresExplicitModeAndBothCutoffs()
    {
        var notice = Notice(10); var revision = Split(notice) with
        {
            HistoricalAvailability = new(Time(2), notice.RawHash, "heading:publication", "manual publication review")
        };
        var ledger = new CorporateActionLedger([revision], [notice]);
        var observed = ledger.At(Time(5), Time(20), now: Time(30));
        Assert.Empty(observed.Actions); Assert.False(observed.RetrospectiveTiming);
        var reviewed = ledger.At(Time(5), Time(20), "REVIEWED_PUBLICATION", Time(30));
        var visible = Assert.Single(reviewed.Actions);
        Assert.Equal(Time(2), visible.AvailableAt); Assert.Equal(Time(10), visible.ObservedAt);
        Assert.True(reviewed.RetrospectiveTiming); Assert.Contains("not certification", reviewed.Note);
        Assert.Empty(ledger.At(Time(5), Time(9), "REVIEWED_PUBLICATION", Time(30)).Actions);
        Assert.Throws<ArgumentException>(() => ledger.At(Time(31), Time(20), now: Time(30)));
        Assert.Throws<ArgumentException>(() => ledger.At(Time(20), Time(31), now: Time(30)));
        Assert.Throws<ArgumentException>(() => ledger.At(Time(20), Time(20), "AUTO", Time(30)));
    }

    [Fact] public void UnreviewedTimingUsesLatestReferencedObservation()
    {
        var first = Notice(1); var second = Notice(3);
        var revision = Split(first) with { RecordedAt = Time(3).AddMinutes(1), Evidence =
            [Split(first).Evidence[0], new(second.Source, second.RawHash, second.ObservedAt, "table:date", "fixture review")] };
        var ledger = new CorporateActionLedger([revision], [first, second]);
        Assert.Empty(ledger.At(Time(2), Time(5), now: Time(30)).Actions);
        var result = ledger.At(Time(5), Time(5), "REVIEWED_PUBLICATION", Time(30));
        Assert.Equal(Time(3), Assert.Single(result.Actions).AvailableAt);
        Assert.False(result.RetrospectiveTiming);
    }

    [Fact] public void FutureCorrectionsCannotChangeOldViewsAndFutureLegalDatesDoNotRestoreOldTerms()
    {
        var first = Notice(1); var second = Notice(10); var original = Split(first);
        var before = new CorporateActionLedger([original], [first]);
        var correction = Split(second, "r2", "r1") with
        {
            Status = "CONFIRMED", ShareChange = new(2, 1, "fixture fractions", LegalEffectiveDate: new(2026, 12, 1))
        };
        var after = before.Append(correction, [second], Time(30));
        var oldView = before.At(Time(5), Time(5), now: Time(30));
        var sameView = after.At(Time(5), Time(5), now: Time(30));
        Assert.Equal(oldView.Hash, sameView.Hash); Assert.Equal(JsonSerializer.Serialize(oldView), JsonSerializer.Serialize(sameView));
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.DoesNotContain("r2", JsonSerializer.Serialize(sameView));
        var latest = Assert.Single(after.At(Time(20), Time(20), now: Time(30)).Actions).Revision;
        Assert.Equal("r2", latest.RevisionKey); Assert.Equal(new DateOnly(2026, 12, 1), latest.ShareChange!.LegalEffectiveDate);
    }

    [Fact] public void CancellationRemainsVisibleAndCannotBeSuperseded()
    {
        var first = Notice(1); var cancelNotice = Notice(5); var later = Notice(10);
        var cancellation = Split(cancelNotice, "r2", "r1") with { Status = "CANCELLED", ShareChange = null };
        var ledger = new CorporateActionLedger([Split(first), cancellation], [first, cancelNotice]);
        var latest = Assert.Single(ledger.At(Time(8), Time(8), now: Time(30)).Actions).Revision;
        Assert.Equal("CANCELLED", latest.Status); Assert.Null(latest.ShareChange); Assert.Null(latest.CashDividend);
        Assert.Equal("r1", Assert.Single(ledger.At(Time(3), Time(3), now: Time(30)).Actions).Revision.RevisionKey);
        Assert.Throws<ArgumentException>(() => ledger.Append(Split(later, "r3", "r2"), [later], Time(30)));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([cancellation with { ShareChange = new(2, 1, "fixture") }], [cancelNotice]));
    }

    [Fact] public void SourceBindingsAndRecordedTimesMustMatchValidatedSnapshots()
    {
        var notice = Notice(1); var revision = Split(notice);
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision], [notice with { RawHash = new('0', 64) }]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision with { Evidence = [revision.Evidence[0] with { ObservedAt = Time(2) }] }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision with { RecordedAt = Time(1).AddTicks(-1) }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision with { Evidence = [revision.Evidence[0] with { FieldLocator = " " }] }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision], [notice, notice]));
        var ledger = new CorporateActionLedger([revision], [notice]);
        Assert.Throws<ArgumentException>(() => ledger.At(Time(1), Time(1), now: Time(1)));
    }

    [Fact] public void HistoricalProofCannotBeMissingFutureUnboundOrBackdateAChild()
    {
        var first = Notice(1); var next = Notice(5); var revision = Split(first);
        HistoricalAvailabilityProof Proof(DateTimeOffset available, string? hash = null) => new(available, hash ?? first.RawHash, "publication", "reviewed fixture");
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision with { HistoricalAvailability = Proof(default) }], [first]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision with { HistoricalAvailability = Proof(Time(2)) }], [first]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision with { HistoricalAvailability = Proof(Time(1), new('F', 64)) }], [first]));
        var child = Split(next, "r2", "r1") with { HistoricalAvailability = Proof(Time(1).AddTicks(-1), next.RawHash) };
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([revision, child], [first, next]));
    }

    [Fact] public void ObservedOnlyHistoryCannotBeRetrofittedWithEarlierPublicationTiming()
    {
        var notice = Notice(10); var original = Split(notice) with { Status = "CONFIRMED" };
        var ledger = new CorporateActionLedger([original], [notice]);
        var ledgerHash = ledger.Hash; var originalQuery = ledger.At(Time(20), Time(20), now: Time(30));
        var retrospectiveUpgrade = original with
        {
            RevisionKey = "r2", SupersedesRevisionKey = "r1", RecordedAt = Time(15),
            HistoricalAvailability = new(Time(2), notice.RawHash, "publication",
                "later manual review asserting earlier availability of the entire revision")
        };
        var rejected = Assert.Throws<ArgumentException>(() => ledger.Append(retrospectiveUpgrade, [], Time(30)));
        Assert.Contains("availability must be monotone", rejected.Message);
        Assert.Equal(ledgerHash, ledger.Hash);
        Assert.Equal(originalQuery.Hash, ledger.At(Time(20), Time(20), now: Time(30)).Hash);
        Assert.Empty(ledger.At(Time(5), Time(20), "REVIEWED_PUBLICATION", Time(30)).Actions);
    }

    [Fact] public void LaterDiscoveryOfOlderSourceMayEnrichCurrentTermsWithoutRewritingRecordedHistory()
    {
        var finalNotice = Notice(10);
        var final = Split(finalNotice) with
        {
            Status = "CONFIRMED", ShareChange = new(2, 1, "latest confirmed fraction interpretation",
                LegalEffectiveDate: new(2024, 11, 15), RawPriceUnitDate: new(2024, 11, 20))
        };
        var ledger = new CorporateActionLedger([final], [finalNotice]);
        // An older source is discovered later; its URL date never supplies availability.
        var olderSource = Notice(15) with
        {
            Source = "https://kind.krx.co.kr/external/2024/10/30/000001/20241030000001/00001.htm"
        };
        var enriched = final with
        {
            RevisionKey = "r2", SupersedesRevisionKey = "r1", RecordedAt = Time(16),
            Evidence = [final.Evidence[0], new(olderSource.Source, olderSource.RawHash, olderSource.ObservedAt,
                "historical context", "older source reviewed without replacing latest confirmed terms")]
        };
        var expanded = ledger.Append(enriched, [olderSource], Time(30));
        foreach (var mode in new[] { "OBSERVED", "REVIEWED_PUBLICATION" })
        {
            Assert.Equal(ledger.At(Time(20), Time(12), mode, Time(30)).Hash,
                expanded.At(Time(20), Time(12), mode, Time(30)).Hash);
            var result = expanded.At(Time(20), Time(20), mode, Time(30));
            var current = Assert.Single(result.Actions);
            Assert.Equal("r2", current.Revision.RevisionKey); Assert.Equal("CONFIRMED", current.Revision.Status);
            Assert.Equal(final.ShareChange, current.Revision.ShareChange); Assert.Equal(2, current.Revision.Evidence.Length);
            Assert.Equal(Time(15), current.AvailableAt); Assert.False(result.RetrospectiveTiming);
        }
    }

    [Fact] public void CompleteChainValidationRejectsForksMissingRootsIdentityChangesAndTimeReversal()
    {
        var a = Notice(1); var b = Notice(3); var c = Notice(5);
        var root = Split(a); var child = Split(b, "r2", "r1"); var fork = Split(c, "r3", "r1");
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([root, child, fork], [a, b, c]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([child], [b]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([root, child with { SupersedesRevisionKey = null }], [a, b]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([root, child with { Security = Security with { ListingEpisodeKey = "different" } }], [a, b]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([root, child with { RevisionKey = "r1" }], [a, b]));
        var earlierObservedChild = Split(a, "r3", "r2") with { RecordedAt = Time(6) };
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([root, child, earlierObservedChild], [a, b]));
        var sameRecord = root with { RevisionKey = "r2", SupersedesRevisionKey = "r1" };
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([root, sameRecord], [a]));
    }

    [Fact] public void AmendmentPublicationMayFollowPreviouslyObservedSupportingOriginal()
    {
        var originalNotice = Notice(2); var amendmentNotice = Notice(11);
        var original = Split(originalNotice);
        var amended = Split(amendmentNotice, "r2", "r1") with
        {
            RecordedAt = Time(12),
            Evidence = [original.Evidence[0], Split(amendmentNotice).Evidence[0]],
            ShareChange = new(2, 1, "amended fixture terms"),
            HistoricalAvailability = new(Time(10), amendmentNotice.RawHash, "amendment:publication",
                "manual review that all terms of this revision were publicly available by the amendment publication")
        };
        var ledger = new CorporateActionLedger([original, amended], [originalNotice, amendmentNotice]);
        Assert.Equal("r1", Assert.Single(ledger.At(Time(9), Time(20), "REVIEWED_PUBLICATION", Time(30)).Actions).Revision.RevisionKey);
        var reviewed = Assert.Single(ledger.At(Time(10), Time(20), "REVIEWED_PUBLICATION", Time(30)).Actions);
        Assert.Equal("r2", reviewed.Revision.RevisionKey); Assert.Equal(Time(10), reviewed.AvailableAt);
        Assert.Equal(Time(11), reviewed.ObservedAt);
        Assert.Equal("r1", Assert.Single(ledger.At(Time(10), Time(20), now: Time(30)).Actions).Revision.RevisionKey);
        Assert.Equal("r1", Assert.Single(ledger.At(Time(10), Time(11), "REVIEWED_PUBLICATION", Time(30)).Actions).Revision.RevisionKey);
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger(
            [original, amended with { HistoricalAvailability = amended.HistoricalAvailability! with { AvailableAt = Time(12) } }],
            [originalNotice, amendmentNotice]));
    }

    [Fact] public void NullableTermsStayUnknownAndInvalidUnitsCurrencyAndSentinelsAreRejected()
    {
        var notice = Notice(1); var split = Split(notice);
        var dividend = split with { Kind = "CASH_DIVIDEND", ShareChange = null, CashDividend = new(null) };
        var ledger = new CorporateActionLedger([dividend], [notice]);
        var terms = Assert.Single(ledger.At(Time(2), Time(2), now: Time(30)).Actions).Revision.CashDividend!;
        Assert.Null(terms.GrossAmountPerShare); Assert.Null(terms.ExDate); Assert.Null(terms.RecordDate); Assert.Null(terms.PlannedPaymentDate);
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([dividend with { CashDividend = new(0) }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([dividend with { CashDividend = new(10, "USD") }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([dividend with { CashDividend = new(10, ExDate: default(DateOnly)) }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([split with { ShareChange = new(1, 1, "fixture") }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([split with { ShareChange = new(2, 0, "fixture") }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([split with { ShareChange = new(2, 1, "fixture", RawPriceUnitDate: default(DateOnly)) }], [notice]));
        Assert.Throws<ArgumentException>(() => new CorporateActionLedger([split with { Security = Security with { StandardCode = "KR7005930004" } }], [notice]));
    }

    [Fact] public void InputOutputAndQueryArraysCannotMutateCommittedEvidence()
    {
        var notice = Notice(1); var revision = Split(notice);
        var revisions = new[] { revision }; var snapshots = new[] { notice };
        var ledger = new CorporateActionLedger(revisions, snapshots); var hash = ledger.Hash;
        revision.Evidence[0] = revision.Evidence[0] with { ReviewNote = "mutated caller array" };
        revisions[0] = revision with { ActionKey = "changed" }; snapshots[0] = notice with { Source = "changed" };
        var returned = ledger.Revisions; returned[0].Evidence[0] = returned[0].Evidence[0] with { ReviewNote = "changed" };
        var query = ledger.At(Time(2), Time(2), now: Time(30)); var queryHash = query.Hash;
        var views = query.Actions; views[0].Revision.Evidence[0] = views[0].Revision.Evidence[0] with { ReviewNote = "changed" };
        Assert.Equal(hash, ledger.Hash); Assert.Equal(queryHash, query.Hash);
        Assert.DoesNotContain("changed", JsonSerializer.Serialize(ledger));
    }

    [Fact] public void JsonRoundTripPreservesLedgerAndQueryAndCriticalKeysAffectHashes()
    {
        var a = Notice(1); var b = Notice(3); var root = Split(a); var child = Split(b, "r2", "r1");
        var ledger = new CorporateActionLedger([root, child], [a, b]);
        var restored = JsonSerializer.Deserialize<CorporateActionLedger>(JsonSerializer.Serialize(ledger))!;
        Assert.Equal(ledger.Hash, restored.Hash);
        Assert.Equal(ledger.Hash, new CorporateActionLedger([child, root], [b, a]).Hash);
        var query = ledger.At(Time(5), Time(5), now: Time(30));
        var restoredQuery = JsonSerializer.Deserialize<CorporateActionQueryResult>(JsonSerializer.Serialize(query))!;
        Assert.Equal(query.Hash, restoredQuery.Hash);
        Assert.NotEqual(new CorporateActionLedger([root], [a]).Hash,
            new CorporateActionLedger([root with { RevisionKey = "changed-key" }], [a]).Hash);
    }

    [Fact] public void StandaloneQueryRejectsInconsistentHashesTimesCutoffsAndDuplicateActions()
    {
        var notice = Notice(1); var ledger = new CorporateActionLedger([Split(notice)], [notice]);
        var query = ledger.At(Time(5), Time(5), now: Time(30)); var view = Assert.Single(query.Actions);
        Assert.Throws<ArgumentException>(() => new CorporateActionQueryResult(Time(5), Time(5), "OBSERVED", [view with { RevisionHash = "changed" }]));
        Assert.Throws<ArgumentException>(() => new CorporateActionQueryResult(Time(5), Time(5), "OBSERVED", [view with { AvailableAt = Time(2) }]));
        Assert.Throws<ArgumentException>(() => new CorporateActionQueryResult(Time(5), Time(5), "OBSERVED", [view with { ObservedAt = Time(2) }]));
        Assert.Throws<ArgumentException>(() => new CorporateActionQueryResult(Time(1).AddTicks(-1), Time(5), "OBSERVED", [view]));
        Assert.Throws<ArgumentException>(() => new CorporateActionQueryResult(Time(5), Time(1), "OBSERVED", [view]));
        Assert.Throws<ArgumentException>(() => new CorporateActionQueryResult(Time(5), Time(5), "OBSERVED", [view, view]));
    }
}
