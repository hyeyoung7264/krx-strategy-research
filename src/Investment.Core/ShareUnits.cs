using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Investment.Core;

// Reviewed execution inputs, not automatic certification of a disclosure ledger or a feed.
public sealed record ShareUnitChange(string ActionKey, string Ticker, DateOnly EffectiveDate, DateOnly PriceUnitDate,
    long NewShares, long OldShares, DateTimeOffset AvailableAt, string Evidence);
public sealed record ShareInventoryCredit(string ActionKey, DateTimeOffset CreditedAt, DateTimeOffset AvailableAt, string Evidence);
public sealed record ShareUnitAdjustment(string ActionKey, string Ticker, DateTimeOffset AppliedAt, string StrategyId,
    DateTimeOffset EntryTime, int PreviousQuantity, int Quantity, decimal PreviousStopLoss, decimal StopLoss,
    decimal RemainingPaid, string ChangeHash);

public static class ShareUnits
{
    public static void Validate(ShareUnitChange[]? changes, ShareInventoryCredit[]? credits,
        Bar[]? bars = null, SecurityLifecycleEvent[]? lifecycle = null)
    {
        var actions = changes ?? []; var inventory = credits ?? [];
        if (actions.Any(c => c is null) || inventory.Any(c => c is null)) throw new ArgumentException("Missing share-unit input.");
        if (actions.GroupBy(c => c.ActionKey, StringComparer.Ordinal).Any(g => g.Count() != 1) ||
            actions.GroupBy(c => (c.Ticker, c.EffectiveDate)).Any(g => g.Count() != 1) ||
            inventory.GroupBy(c => c.ActionKey, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new ArgumentException("Share-unit changes and inventory credits require unique action keys.");
        foreach (var change in actions)
        {
            ValidateChange(change);
            if (bars is not null && !bars.Any(b => b.Ticker == change.Ticker))
                throw new ArgumentException("Share-unit change refers to a missing security.");
        }
        foreach (var credit in inventory)
        {
            var change = actions.SingleOrDefault(c => c.ActionKey == credit.ActionKey);
            if (change is null || string.IsNullOrWhiteSpace(credit.Evidence) ||
                credit.CreditedAt < Clock.Open(change.EffectiveDate) || credit.AvailableAt < credit.CreditedAt)
                throw new ArgumentException("Inventory credit must reference a change and cannot precede economic effectiveness or observation.");
        }
        foreach (var change in actions)
        {
            // Account credits constrain held lots, not the historical existence of a security.
            if ((lifecycle ?? []).Any(e => e.Ticker == change.Ticker && e.Date >= change.EffectiveDate && e.Date <= change.PriceUnitDate))
                throw new ArgumentException("Security lifecycle transition crosses an unresolved share-unit price conversion.");
        }
        foreach (var bar in bars ?? [])
        {
            if (bar.CorporateAction && !actions.Any(c => c.Ticker == bar.Ticker &&
                    (c.EffectiveDate == bar.Date || c.PriceUnitDate == bar.Date)))
                throw new ArgumentException("Corporate-action bar lacks an explicit matching share-unit change.");
            if (bar.Tradable && actions.Any(c => c.Ticker == bar.Ticker && c.EffectiveDate <= bar.Date && bar.Date < c.PriceUnitDate))
                throw new ArgumentException("Tradable bar inside economic-to-price-unit transition.");
        }
    }

    private static void ValidateChange(ShareUnitChange change)
    {
        if (change is null || string.IsNullOrWhiteSpace(change.ActionKey) || change.ActionKey.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(change.Ticker) || string.IsNullOrWhiteSpace(change.Evidence) ||
            change.EffectiveDate == default || change.PriceUnitDate < change.EffectiveDate ||
            change.NewShares <= 0 || change.OldShares <= 0 || change.NewShares == change.OldShares ||
            change.AvailableAt == default || change.AvailableAt > Clock.Open(change.EffectiveDate))
            throw new ArgumentException("Invalid or late share-unit change evidence.");
    }

    public static (int Quantity, decimal StopLoss) Adjust(int quantity, decimal stop, ShareUnitChange change)
    {
        if (stop <= 0) throw new ArgumentException("Invalid share-unit holding stop.");
        return (AdjustQuantity(quantity, change), StopLoss(stop, [change]));
    }

    public static int AdjustQuantity(int quantity, ShareUnitChange change)
    {
        ValidateChange(change);
        if (quantity <= 0) throw new ArgumentException("Invalid share-unit holding quantity.");
        var scaled = BigInteger.DivRem((BigInteger)quantity * change.NewShares, change.OldShares, out var fraction);
        if (!fraction.IsZero || scaled <= 0 || scaled > int.MaxValue)
            throw new InvalidOperationException("Fractional or out-of-range share-unit holding requires explicit settlement; stopped.");
        return (int)scaled;
    }

    public static decimal StopLoss(decimal originalStop, IEnumerable<ShareUnitChange> appliedChanges)
    {
        if (originalStop <= 0) throw new ArgumentException("Invalid original share-unit stop.");
        BigInteger numerator = 1, denominator = 1;
        foreach (var change in appliedChanges)
        {
            ValidateChange(change);
            MultiplyRatio(ref numerator, ref denominator, change.OldShares, change.NewShares);
        }
        return Scale(originalStop, numerator, denominator);
    }

    public static decimal PriceFactor(string ticker, DateOnly rawDate, DateOnly economicDate,
        DateTimeOffset knownAt, ShareUnitChange[]? changes)
    {
        BigInteger numerator = 1, denominator = 1;
        foreach (var c in Known(ticker, knownAt, changes))
        {
            var rawChanged = c.PriceUnitDate <= rawDate; var economicChanged = c.EffectiveDate <= economicDate;
            if (rawChanged == economicChanged) continue;
            MultiplyRatio(ref numerator, ref denominator, rawChanged ? c.NewShares : c.OldShares,
                rawChanged ? c.OldShares : c.NewShares);
        }
        return Scale(1, numerator, denominator);
    }

    public static decimal CapacityVolume(string ticker, DateOnly rawDate, DateOnly targetDate, long volume,
        DateTimeOffset knownAt, ShareUnitChange[]? changes)
    {
        if (volume < 0) throw new ArgumentException("Negative published volume.");
        BigInteger numerator = 1, denominator = 1;
        foreach (var c in Known(ticker, knownAt, changes))
        {
            // Economic target units also serve AI comparisons; execution is blocked until raw units catch up.
            var rawChanged = c.PriceUnitDate <= rawDate; var targetChanged = c.EffectiveDate <= targetDate;
            if (rawChanged == targetChanged) continue;
            MultiplyRatio(ref numerator, ref denominator, targetChanged ? c.NewShares : c.OldShares,
                targetChanged ? c.OldShares : c.NewShares);
        }
        return Scale(volume, numerator, denominator);
    }

    // Comparison closes only. Raw OHLCV remains in Dataset; unchanged Volume is used solely for zero-volume eligibility.
    public static Bar[] SignalHistory(Bar[] rawBars, DateOnly economicDate, DateTimeOffset knownAt, ShareUnitChange[]? changes)
        => rawBars.Select(b => b with { Close = EconomicPrice(b.Close, b.Ticker, b.Date, economicDate, knownAt, changes) }).ToArray();

    // Reduce the complete rational ratio first; cancelling actions must not leave a decimal residue.
    public static decimal EconomicPrice(decimal rawPrice, string ticker, DateOnly rawDate, DateOnly economicDate,
        DateTimeOffset knownAt, ShareUnitChange[]? changes)
    {
        BigInteger numerator = 1, denominator = 1;
        foreach (var c in Known(ticker, knownAt, changes))
        {
            var rawChanged = c.PriceUnitDate <= rawDate; var economicChanged = c.EffectiveDate <= economicDate;
            if (rawChanged == economicChanged) continue;
            MultiplyRatio(ref numerator, ref denominator, rawChanged ? c.NewShares : c.OldShares,
                rawChanged ? c.OldShares : c.NewShares);
        }
        return Scale(rawPrice, numerator, denominator);
    }

    public static Signal? GenerateSignal(StrategySpec spec, Bar[] rawBars, DateOnly economicDate,
        DateTimeOffset knownAt, ShareUnitChange[]? changes)
    {
        var relevant = (changes ?? []).Where(c => c.AvailableAt <= knownAt && rawBars.Any(b => b.Ticker == c.Ticker &&
                (c.PriceUnitDate <= b.Date) != (c.EffectiveDate <= economicDate)))
            .OrderBy(c => c.EffectiveDate).ThenBy(c => c.ActionKey, StringComparer.Ordinal).ToArray();
        if (relevant.Length == 0) return new PriceStrategy(spec).Generate(rawBars, knownAt);
        var signal = new PriceStrategy(spec).Generate(SignalHistory(rawBars, economicDate, knownAt, relevant), knownAt);
        if (signal is null) return null;
        var evidence = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { RawBars = rawBars, ShareUnitChanges = relevant })));
        return signal with { Price = rawBars[^1].Close, EvidenceHash = evidence };
    }

    public static bool CanSell(string[] appliedKeys, ShareInventoryCredit[]? credits, DateTimeOffset now)
        => appliedKeys.All(key => (credits ?? []).Any(c => c.ActionKey == key && c.CreditedAt <= now && c.AvailableAt <= now));

    public static bool CanTrade(string ticker, DateOnly date, DateTimeOffset knownAt, ShareUnitChange[]? changes)
        => !Known(ticker, knownAt, changes).Any(c => c.EffectiveDate <= date && date < c.PriceUnitDate);

    private static IEnumerable<ShareUnitChange> Known(string ticker, DateTimeOffset knownAt, ShareUnitChange[]? changes)
        => (changes ?? []).Where(c => c.Ticker == ticker && c.AvailableAt <= knownAt)
            .OrderBy(c => c.EffectiveDate).ThenBy(c => c.ActionKey, StringComparer.Ordinal);

    private static void MultiplyRatio(ref BigInteger numerator, ref BigInteger denominator, long top, long bottom)
    {
        if (top <= 0 || bottom <= 0) throw new ArgumentException("Invalid share-unit ratio.");
        numerator *= top; denominator *= bottom;
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor; denominator /= divisor;
    }

    private static decimal Scale(decimal value, BigInteger numerator, BigInteger denominator)
    {
        if (value < 0) throw new ArgumentException("Negative share-unit price or volume.");
        if (value == 0 || numerator == denominator) return value;
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        numerator *= coefficient;
        denominator *= BigInteger.Pow(10, (bits[3] >> 16) & 0xFF);
        var maximum = (BigInteger.One << 96) - 1;
        if (numerator > maximum * denominator)
            throw new InvalidOperationException("Share-unit result exceeds supported decimal range.");
        if (numerator * BigInteger.Pow(10, 28) < denominator)
            throw new InvalidOperationException("Share-unit result underflows supported decimal precision.");
        // Decimal's 96-bit coefficient supports up to scale 28. Round once, to nearest/even,
        // only after composing the exact original decimal and all share ratios.
        for (var scale = 28; scale >= 0; scale--)
        {
            var rounded = BigInteger.DivRem(numerator * BigInteger.Pow(10, scale), denominator, out var remainder);
            if (remainder * 2 > denominator || remainder * 2 == denominator && !rounded.IsEven) rounded++;
            if (rounded > maximum) continue;
            if (rounded.IsZero) throw new InvalidOperationException("Share-unit result underflows supported decimal precision.");
            var finalScale = scale;
            while (finalScale > 0 && rounded % 10 == 0) { rounded /= 10; finalScale--; }
            return new decimal(unchecked((int)(uint)(rounded & uint.MaxValue)),
                unchecked((int)(uint)((rounded >> 32) & uint.MaxValue)),
                unchecked((int)(uint)((rounded >> 64) & uint.MaxValue)), false, (byte)finalScale);
        }
        throw new InvalidOperationException("Share-unit result exceeds supported decimal range.");
    }
    public static string Hash(ShareUnitChange change) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(change)));
}
