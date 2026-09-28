using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record Bar(string Ticker, string Sector, DateOnly Date, DateTimeOffset AvailableAt,
    decimal Open, decimal High, decimal Low, decimal Close, long Volume, decimal TradingValue,
    bool Tradable, bool Member, bool CorporateAction = false);
public sealed record SecurityLifecycleEvent(string Ticker, DateOnly Date, string Kind, DateTimeOffset AvailableAt, string Evidence);
public sealed record Dataset(string Source, bool Synthetic, bool PointInTimeCertified, Bar[] Bars, DateOnly[]? Sessions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SecurityLifecycleEvent[]? LifecycleEvents = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    [System.Text.Json.Serialization.JsonIgnore]
    public DateOnly[] Dates => Bars.Select(b => b.Date).Distinct().Order().ToArray();
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source) || Bars.Length == 0) throw new ArgumentException("Missing dataset/source.");
        if (Bars.GroupBy(b => (b.Ticker, b.Date)).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate bar.");
        foreach (var b in Bars)
        {
            // KRX no-trade rows can have zero open/high/low and a retained positive close.
            // Preserve these reported zeros; execution must never treat them as prices.
            var noTrade = b.Open == 0 && b.High == 0 && b.Low == 0 && b.Volume == 0 && b.TradingValue == 0;
            var priced = b.Open > 0 && b.Low > 0 && b.High >= Math.Max(b.Open, b.Close) &&
                b.Low <= Math.Min(b.Open, b.Close) && b.Low <= b.High;
            if (string.IsNullOrWhiteSpace(b.Ticker) || string.IsNullOrWhiteSpace(b.Sector) || b.Close <= 0 ||
                b.Volume < 0 || b.TradingValue < 0 || !(priced || noTrade))
                throw new ArgumentException("Invalid OHLCV.");
            if (b.AvailableAt < Clock.Close(b.Date)) throw new ArgumentException("Daily bar available before close.");
            if (b.CorporateAction) throw new ArgumentException("Corporate action requires explicit point-in-time position adjustment; unsupported dataset.");
        }
        // Absence outside a security's listed interval needs an explicit event; gaps inside it are errors.
        var dates = Dates;
        if (PointInTimeCertified && (Sessions == null || !Sessions.SequenceEqual(dates))) throw new ArgumentException("Certified data requires an explicit ordered exchange session calendar matching all bars.");
        if (Sessions != null && (!Sessions.SequenceEqual(Sessions.Distinct().Order()) || !Sessions.SequenceEqual(dates))) throw new ArgumentException("Session calendar is incomplete/unordered.");
        var events = LifecycleEvents ?? [];
        if (events.GroupBy(e => (e.Ticker, e.Date)).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate lifecycle event.");
        var tickers = Bars.Select(b => b.Ticker).ToHashSet(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (!tickers.Contains(e.Ticker) || !dates.Contains(e.Date) || e.Kind is not ("LISTED" or "DELISTED") ||
                string.IsNullOrWhiteSpace(e.Evidence) || (PointInTimeCertified && e.AvailableAt > Clock.Open(e.Date)))
                throw new ArgumentException("Invalid or late security lifecycle evidence.");
        }
        var eventByKey = events.ToDictionary(e => (e.Ticker, e.Date));
        var present = Bars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.Select(b => b.Date).ToHashSet());
        foreach (var (ticker, rows) in present)
        {
            var active = rows.Contains(dates[0]);
            foreach (var date in dates)
            {
                if (eventByKey.TryGetValue((ticker, date), out var change))
                {
                    if (change.Kind == "LISTED" && active || change.Kind == "DELISTED" && !active)
                        throw new ArgumentException("Inconsistent security lifecycle transition.");
                    active = change.Kind == "LISTED";
                }
                if (rows.Contains(date) != active)
                    throw new ArgumentException("Incomplete listed-session grid or unexplained listing boundary.");
            }
        }
    }
}
public static class Clock
{
    public static DateTimeOffset Open(DateOnly d) => new(d.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(9));
    public static DateTimeOffset Close(DateOnly d) => new(d.ToDateTime(new TimeOnly(15, 30)), TimeSpan.FromHours(9));
}
public sealed record Costs(decimal Commission = .00015m, decimal SellTax = .002m, decimal Slippage = .001m)
{
    public void Validate() { if (Commission < 0 || SellTax < 0 || Slippage < 0 || Commission + SellTax >= 1 || Slippage >= 1) throw new ArgumentException("Invalid costs."); }
}
public sealed record Risk(decimal PositionCap = .10m, decimal StrategyCap = .40m, decimal ExposureCap = .80m,
    decimal SectorCap = .30m, decimal DailyLoss = .02m, decimal Drawdown = .10m, decimal StopLoss = .05m,
    decimal Participation = .01m)
{
    public void Validate()
    {
        var values = new[] { PositionCap, StrategyCap, ExposureCap, SectorCap, DailyLoss, Drawdown, StopLoss, Participation };
        if (values.Any(v => v <= 0 || v > 1) || StrategyCap > .5m || PositionCap > StrategyCap) throw new ArgumentException("Invalid risk; strategy capital capped at 50%.");
    }
}
public sealed record StrategySpec(string Family, int Lookback, decimal Threshold, int HoldDays, int Version = 1)
{
    public string Id => $"{Family}:v{Version}:L{Lookback}:T{Threshold.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)}:H{HoldDays}";
    public void Validate() { if (Family is not ("momentum" or "reversion") || Lookback < 2 || HoldDays < 1 || Threshold < 0 || Threshold >= 1 || Version < 1) throw new ArgumentException("Invalid strategy."); }
}
public sealed record Signal(string StrategyId, string Ticker, DateTimeOffset Time, decimal Price, decimal Score, string Reason, string EvidenceHash);
public interface IStrategy { StrategySpec Spec { get; } Signal? Generate(IReadOnlyList<Bar> history, DateTimeOffset now); }
public sealed class PriceStrategy(StrategySpec spec) : IStrategy
{
    public StrategySpec Spec { get; } = spec;
    public Signal? Generate(IReadOnlyList<Bar> history, DateTimeOffset now)
    {
        Spec.Validate();
        if (history.Any(b => b.AvailableAt > now || Clock.Close(b.Date) > now)) throw new ArgumentException("Future observation.");
        if (history.Count < Spec.Lookback + 1) return null;
        var last = history[^1];
        if (!last.Tradable || !last.Member || last.Volume == 0) return null;
        var ret = last.Close / history[^(Spec.Lookback + 1)].Close - 1;
        var score = Spec.Family == "momentum" ? ret : -ret;
        if (score <= Spec.Threshold) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(history))));
        return new(Spec.Id, last.Ticker, now, last.Close, score, FormattableString.Invariant($"{Spec.Family}; trailing return={ret}; lookback={Spec.Lookback}"), hash);
    }
}
public sealed record Trade(string StrategyId, string Ticker, DateTimeOffset SignalTime, decimal SignalPrice,
    DateTimeOffset EntryTime, decimal EntryPrice, DateTimeOffset ExitTime, decimal ExitPrice, int Quantity,
    decimal NetProfit, decimal Return, string Reason, string EvidenceHash, bool CompletesPosition = true);
public sealed record EquityPoint(DateOnly Date, decimal Equity, decimal DailyReturn, decimal Exposure);
public sealed record Metrics(decimal TotalReturn, double? Cagr, decimal AverageDailyReturn, decimal WinRate,
    decimal? ProfitFactor, decimal AverageProfit, decimal AverageLoss, decimal ExpectedValuePerTrade,
    decimal MaximumDrawdown, double? Sharpe, double? Sortino, int NumberOfTrades, decimal Turnover,
    double AverageHoldingDays, int OpenPositions);
public sealed record RunResult(string Id, string DataHash, bool Synthetic, DateOnly Start, DateOnly End,
    StrategySpec[] Strategies, Costs Costs, Risk Risk, decimal InitialCapital, Metrics Metrics,
    EquityPoint[] Equity, Trade[] Trades, string[] Events, string CodeVersion, Metrics? GrossMetrics = null);
