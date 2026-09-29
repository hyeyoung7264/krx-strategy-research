using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Investment.Core;

public sealed record CorporateSecurityIdentity(string Market, string Ticker, string StandardCode, string ListingEpisodeKey);
public sealed record ShareChangeTerms(long NewShares, long OldShares, string FractionTreatmentEvidence,
    DateOnly? LegalEffectiveDate = null, DateOnly? RawPriceUnitDate = null, DateOnly? TradingResumptionDate = null);
public sealed record CashDividendTerms(decimal? GrossAmountPerShare, string Currency = "KRW",
    DateOnly? ExDate = null, DateOnly? RecordDate = null, DateOnly? PlannedPaymentDate = null);
public sealed record CorporateActionEvidenceReference(string Source, string SourceHash, DateTimeOffset ObservedAt,
    string FieldLocator, string ReviewNote);
/// <summary>A manual claim that the entire revision was publicly available at AvailableAt, supported by the named source; not source or completeness certification.</summary>
public sealed record HistoricalAvailabilityProof(DateTimeOffset AvailableAt, string SourceHash, string Locator, string ReviewEvidence);
public sealed record CorporateActionRevision(string ActionKey, string RevisionKey, string? SupersedesRevisionKey,
    CorporateSecurityIdentity Security, string Kind, string Status, DateTimeOffset RecordedAt,
    CorporateActionEvidenceReference[] Evidence, ShareChangeTerms? ShareChange = null,
    CashDividendTerms? CashDividend = null, HistoricalAvailabilityProof? HistoricalAvailability = null);
public sealed record CorporateActionView(CorporateActionRevision Revision, DateTimeOffset AvailableAt,
    DateTimeOffset ObservedAt, string RevisionHash);

/// <summary>A frozen, visible-only projection. Neither publication review nor manual terms certify accounting or execution.</summary>
public sealed class CorporateActionQueryResult
{
    public const string Limitation = "Manual interpretation of source evidence, not certification of security mapping, historical completeness, legal effect, entitlement, net tax, settlement, cash credit or execution. REVIEWED_PUBLICATION timing is retrospective; missing publication proof retains observed timing.";
    private readonly CorporateActionView[] actions;
    public DateTimeOffset MarketKnowledgeCutoff { get; }
    public DateTimeOffset LedgerRecordedCutoff { get; }
    public string TimingMode { get; }
    public bool RetrospectiveTiming => TimingMode == "REVIEWED_PUBLICATION" && actions.Any(a => a.Revision.HistoricalAvailability is not null);
    public CorporateActionView[] Actions => actions.Select(Clone).ToArray();
    public string Note => Limitation;
    public string Hash => CorporateActionLedger.Digest(new
    {
        MarketKnowledgeCutoff, LedgerRecordedCutoff, TimingMode, RetrospectiveTiming, Actions = actions, Note
    });

    [JsonConstructor]
    public CorporateActionQueryResult(DateTimeOffset marketKnowledgeCutoff, DateTimeOffset ledgerRecordedCutoff,
        string timingMode, CorporateActionView[] actions)
    {
        CorporateActionLedger.ValidateMode(timingMode);
        if (actions is null || actions.Any(a => a is null)) throw new ArgumentException("Missing corporate-action views.");
        foreach (var view in actions)
        {
            var revision = CorporateActionLedger.Copy(view.Revision);
            CorporateActionLedger.ValidateViewRevision(revision, ledgerRecordedCutoff);
            if (view.RevisionHash != CorporateActionLedger.Digest(revision) ||
                view.ObservedAt != CorporateActionLedger.Observed(revision) ||
                view.AvailableAt != CorporateActionLedger.Availability(revision, timingMode) ||
                view.AvailableAt > marketKnowledgeCutoff || revision.RecordedAt > ledgerRecordedCutoff)
                throw new ArgumentException("Corporate-action view hash, timing or cutoff differs from its revision.");
        }
        var keys = actions.Select(a => a.Revision.ActionKey).ToArray();
        if (!keys.SequenceEqual(keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw new ArgumentException("Corporate-action views require unique ordered action keys.");
        MarketKnowledgeCutoff = marketKnowledgeCutoff; LedgerRecordedCutoff = ledgerRecordedCutoff;
        TimingMode = timingMode; this.actions = actions.Select(Clone).ToArray();
    }

    private static CorporateActionView Clone(CorporateActionView view) => view with { Revision = CorporateActionLedger.Copy(view.Revision) };
}

/// <summary>
/// Immutable evidence/revision ledger only. No raw-price adjustment, eligibility inference,
/// fractional-share treatment, position mutation, dividend accrual or cash settlement is performed.
/// </summary>
public sealed class CorporateActionLedger
{
    private readonly CorporateActionRevision[] revisions;
    private readonly KindNoticeSnapshot[] snapshots;
    public CorporateActionRevision[] Revisions => revisions.Select(Copy).ToArray();
    public KindNoticeSnapshot[] Snapshots => snapshots.Select(s => s with { }).ToArray();
    [JsonIgnore]
    public string Hash => Digest(new { Revisions = revisions, Snapshots = snapshots });

    public CorporateActionLedger() : this([], []) { }

    [JsonConstructor]
    public CorporateActionLedger(CorporateActionRevision[] revisions, KindNoticeSnapshot[] snapshots)
    {
        if (revisions is null || snapshots is null || revisions.Any(r => r is null) || snapshots.Any(s => s is null))
            throw new ArgumentException("Missing corporate-action ledger arrays or entries.");
        this.revisions = revisions.Select(Copy).OrderBy(r => r.ActionKey, StringComparer.Ordinal)
            .ThenBy(r => r.RecordedAt).ThenBy(r => r.RevisionKey, StringComparer.Ordinal).ToArray();
        this.snapshots = snapshots.Select(s => s with { }).OrderBy(s => s.Source, StringComparer.Ordinal)
            .ThenBy(s => s.ObservedAt).ThenBy(s => s.RawHash, StringComparer.Ordinal).ToArray();
        Validate(DateTimeOffset.UtcNow);
    }

    public CorporateActionLedger Append(CorporateActionRevision revision, KindNoticeSnapshot[] snapshots, DateTimeOffset? now = null)
    {
        Validate(now);
        var next = new CorporateActionLedger(revisions.Append(revision).ToArray(), this.snapshots.Concat(snapshots).ToArray());
        next.Validate(now);
        return next;
    }

    public CorporateActionQueryResult At(DateTimeOffset cutoff, DateTimeOffset recordedCutoff,
        string mode = "OBSERVED", DateTimeOffset? now = null)
    {
        var auditNow = now ?? DateTimeOffset.UtcNow;
        // Validate the complete ledger before selecting a prefix: a future fork is not hidden by a cutoff.
        Validate(auditNow); ValidateMode(mode);
        if (cutoff > auditNow || recordedCutoff > auditNow)
            throw new ArgumentException("Corporate-action query cutoffs cannot exceed audit time.");
        var visible = revisions.Where(r => r.RecordedAt <= recordedCutoff && Availability(r, mode) <= cutoff)
            .GroupBy(r => r.ActionKey, StringComparer.Ordinal).Select(g => g.OrderBy(r => r.RecordedAt).Last())
            .OrderBy(r => r.ActionKey, StringComparer.Ordinal)
            .Select(r => new CorporateActionView(Copy(r), Availability(r, mode), Observed(r), Digest(r))).ToArray();
        // Do not filter by terms/status after selection: cancellations must remain visible,
        // and a future legal date must not resurrect a superseded predecessor.
        return new(cutoff, recordedCutoff, mode, visible);
    }

    public void Validate(DateTimeOffset? now = null)
    {
        var auditNow = now ?? DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots) snapshot.Validate(auditNow);
        var sourceGroups = snapshots.GroupBy(s => (s.Source, s.RawHash, s.ObservedAt)).ToArray();
        if (sourceGroups.Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate corporate-action source snapshot binding.");
        var sourceKeys = sourceGroups.Select(g => g.Key).ToHashSet();
        if (revisions.GroupBy(r => r.RevisionKey, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new ArgumentException("Corporate-action revision keys must be globally unique.");
        foreach (var revision in revisions) ValidateRevision(revision, sourceKeys, auditNow);
        foreach (var chain in revisions.GroupBy(r => r.ActionKey, StringComparer.Ordinal))
        {
            var ordered = chain.OrderBy(r => r.RecordedAt).ToArray();
            if (ordered.Count(r => r.SupersedesRevisionKey is null) != 1 || ordered[0].SupersedesRevisionKey is not null)
                throw new ArgumentException("Corporate-action chain requires exactly one first root.");
            for (var i = 1; i < ordered.Length; i++)
            {
                var prior = ordered[i - 1]; var current = ordered[i];
                if (current.SupersedesRevisionKey != prior.RevisionKey || current.RecordedAt <= prior.RecordedAt ||
                    current.Security != prior.Security || current.Kind != prior.Kind || prior.Status == "CANCELLED")
                    throw new ArgumentException("Corporate-action chain has a fork, missing ancestor, nonmonotone record, identity change or cancelled predecessor.");
                if (Observed(current) < Observed(prior) || Availability(current, "REVIEWED_PUBLICATION") < Availability(prior, "REVIEWED_PUBLICATION"))
                    throw new ArgumentException("Corporate-action observed and reviewed availability must be monotone through revisions.");
            }
        }
    }

    private static void ValidateRevision(CorporateActionRevision r,
        HashSet<(string Source, string RawHash, DateTimeOffset ObservedAt)> sourceKeys, DateTimeOffset now)
    {
        if (!Key(r.ActionKey) || !Key(r.RevisionKey) || r.SupersedesRevisionKey is not null && !Key(r.SupersedesRevisionKey) ||
            r.Security is null || r.Security.Market is not ("KOSPI" or "KOSDAQ") ||
            r.Security.Ticker is not { Length: 6 } || !r.Security.Ticker.All(char.IsAsciiLetterOrDigit) ||
            !IsKoreanIsin(r.Security.StandardCode) || !Key(r.Security.ListingEpisodeKey) ||
            r.Kind is not ("SHARE_UNIT_CHANGE" or "CASH_DIVIDEND") || r.Status is not ("ANNOUNCED" or "CONFIRMED" or "CANCELLED") ||
            r.RecordedAt > now || r.Evidence.Length == 0)
            throw new ArgumentException("Invalid corporate-action identity, kind, status, time or evidence.");
        foreach (var evidence in r.Evidence)
        {
            if (evidence is null || string.IsNullOrWhiteSpace(evidence.Source) || !ValidHash(evidence.SourceHash) || evidence.ObservedAt == default ||
                !sourceKeys.Contains((evidence.Source, evidence.SourceHash, evidence.ObservedAt)) ||
                string.IsNullOrWhiteSpace(evidence.FieldLocator) || string.IsNullOrWhiteSpace(evidence.ReviewNote) ||
                r.RecordedAt < evidence.ObservedAt)
                throw new ArgumentException("Corporate-action fields require bound, observed source evidence and manual review notes.");
        }
        if (r.HistoricalAvailability is { } proof &&
            (proof.AvailableAt == default || string.IsNullOrWhiteSpace(proof.Locator) || string.IsNullOrWhiteSpace(proof.ReviewEvidence) ||
             !r.Evidence.Any(e => e.SourceHash == proof.SourceHash) || proof.AvailableAt > r.RecordedAt ||
             r.Evidence.Any(e => e.SourceHash == proof.SourceHash && proof.AvailableAt > e.ObservedAt)))
            throw new ArgumentException("Invalid reviewed historical-availability evidence.");
        if (r.Status == "CANCELLED")
        {
            if (r.ShareChange is not null || r.CashDividend is not null)
                throw new ArgumentException("Cancelled corporate-action revisions cannot carry active terms.");
            return;
        }
        if (r.Kind == "SHARE_UNIT_CHANGE")
        {
            if (r.CashDividend is not null || r.ShareChange is not { } terms || terms.NewShares <= 0 || terms.OldShares <= 0 ||
                terms.NewShares == terms.OldShares || string.IsNullOrWhiteSpace(terms.FractionTreatmentEvidence) ||
                new[] { terms.LegalEffectiveDate, terms.RawPriceUnitDate, terms.TradingResumptionDate }.Any(d => d == default(DateOnly)))
                throw new ArgumentException("Share-unit changes require a positive unequal ratio and fraction-treatment evidence.");
        }
        else if (r.ShareChange is not null || r.CashDividend is not { } terms || terms.Currency != "KRW" ||
            terms.GrossAmountPerShare is <= 0 || new[] { terms.ExDate, terms.RecordDate, terms.PlannedPaymentDate }.Any(d => d == default(DateOnly)))
            throw new ArgumentException("Cash dividends require KRW terms; known gross amounts must be positive.");
    }

    internal static CorporateActionRevision Copy(CorporateActionRevision revision)
    {
        if (revision is null || revision.Evidence is null) throw new ArgumentException("Missing corporate-action revision or evidence array.");
        return revision with { Evidence = revision.Evidence.ToArray() };
    }
    internal static void ValidateViewRevision(CorporateActionRevision revision, DateTimeOffset recordedCutoff)
    {
        if (revision.Evidence.Any(e => e is null)) throw new ArgumentException("Missing corporate-action view evidence.");
        // A standalone view can prove internal consistency, not source bytes or chain completeness.
        ValidateRevision(revision, revision.Evidence.Select(e => (e.Source, e.SourceHash, e.ObservedAt)).ToHashSet(), recordedCutoff);
    }
    internal static DateTimeOffset Observed(CorporateActionRevision revision) => revision.Evidence.Max(e => e.ObservedAt);
    internal static DateTimeOffset Availability(CorporateActionRevision revision, string mode) =>
        mode == "REVIEWED_PUBLICATION" && revision.HistoricalAvailability is { } proof ? proof.AvailableAt : Observed(revision);
    internal static void ValidateMode(string mode)
    {
        if (mode is not ("OBSERVED" or "REVIEWED_PUBLICATION"))
            throw new ArgumentException("Explicit corporate-action timing mode must be OBSERVED or REVIEWED_PUBLICATION.");
    }
    private static bool Key(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');
    private static bool IsKoreanIsin(string? value)
    {
        if (value is not { Length: 12 } || !value.StartsWith("KR", StringComparison.Ordinal) || !char.IsAsciiDigit(value[^1]) ||
            value.Any(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetterUpper(c))) return false;
        var digits = string.Concat(value.Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var sum = 0; var twice = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0'; if (twice) digit *= 2;
            sum += digit / 10 + digit % 10; twice = !twice;
        }
        return sum % 10 == 0;
    }
    internal static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
