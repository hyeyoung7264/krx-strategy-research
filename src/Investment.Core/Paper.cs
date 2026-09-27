using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record Quote(string Ticker, string Sector, DateTimeOffset Time, decimal Bid, decimal Ask, bool Tradable);
public sealed record Observation(string Source, string Kind, DateTimeOffset ObservedAt, Quote[] Quotes, Bar[] ClosedBars, bool VerifiedFeed = false);
public interface IObservationFeed { Task<Observation> Observe(CancellationToken cancellationToken); }
public sealed record PaperPosition(StrategySpec Strategy, Signal Signal, DateTimeOffset EntryTime, decimal EntryPrice,
    int Quantity, decimal Paid, decimal StopLoss, string Sector);
public sealed record PaperFill(Trade Trade, decimal StopLoss, decimal? TakeProfit, decimal? ExpectedReturn, string MarketCondition, string Source);
public sealed record PaperAudit(int Sequence, string PreviousHash, string Hash, Observation Observation, string[] Actions);
public sealed record PaperState(string SessionId, string[] ResearchEvidenceIds, StrategySpec[] Strategies, Costs Costs, Risk Risk,
    decimal InitialCapital, decimal Cash, decimal Peak, decimal DayStartEquity, DateOnly? TradingDate,
    DateTimeOffset LastObservation, string? BaselineRegime, bool Halted, bool VerifiedFeed,
    Bar[] History, Signal[] PendingSignals, PaperPosition[] Positions, PaperFill[] Fills,
    EquityPoint[] Equity, PaperAudit[] Audit, decimal Turnover, Dictionary<string, int>? LiquidityUsed = null, string CodeVersion = "",
    int ResearchCandidateCount = 0, string CohortEvidenceId = "");

public static class PaperEngine
{
    public static PaperState Start(ExperimentArchive[] archives, Dataset history, Costs costs, Risk risk, DateTimeOffset now,
        SourceSnapshot currentSource, decimal initial = 100_000_000m)
    {
        history.Validate(); costs.Validate(); risk.Validate();
        if (archives.Length == 1 && archives[0].Cohort is { } cohort)
        {
            var archive = archives[0];
            if (history.Synthetic || !history.PointInTimeCertified || archive.Data.Hash != history.Hash)
                throw new ArgumentException("Paper cohort requires the same real certified dataset.");
            var replay = archive.Reproduce(currentSource);
            if (!replay.Matches) throw new ArgumentException("Paper cohort archive reproduction failed: " + string.Join(",", replay.Differences));
            if (cohort.Evaluation.Decision != "PAPER_ELIGIBLE" || cohort.CreatedAt > now)
                throw new ArgumentException("Ineligible or future cohort portfolio evidence.");
            return StartCore(cohort.Families, history, costs, risk, now, currentSource.Hash, initial, cohort);
        }
        if (history.Synthetic || !history.PointInTimeCertified || archives.Length < 2)
            throw new ArgumentException("Paper requires real certified history and at least two complete research archives.");
        var evidence = archives.Select(archive =>
        {
            if (archive.Research == null || archive.Data.Hash != history.Hash)
                throw new ArgumentException("Paper requires a research archive with the same certified dataset; standalone result JSON is insufficient.");
            var replay = archive.Reproduce(currentSource);
            if (!replay.Matches) throw new ArgumentException("Paper research archive reproduction failed: " + string.Join(",", replay.Differences));
            return archive.Research;
        }).ToArray();
        return StartCore(evidence, history, costs, risk, now, currentSource.Hash, initial);
    }
    private static PaperState StartCore(ResearchResult[] evidence, Dataset history, Costs costs, Risk risk, DateTimeOffset now,
        string codeVersion, decimal initial = 100_000_000m, CohortResult? cohort = null)
    {
        history.Validate(); costs.Validate(); risk.Validate();
        if (initial <= 0 || history.Synthetic || !history.PointInTimeCertified || evidence.Length < 2)
            throw new ArgumentException("Paper requires real certified history and at least two independent eligible strategies.");
        if (evidence.Select(e => e.Id).Distinct().Count() != evidence.Length ||
            evidence.Any(e => e.Evaluation.Decision != "PAPER_ELIGIBLE" || e.Synthetic || !e.PointInTimeCertified || e.DataHash != history.Hash || Clock.Close(e.Holdout.End) >= now || e.CreatedAt > now))
            throw new ArgumentException("Ineligible or future research evidence.");
        if (evidence.Any(e => e.Holdout.Costs != costs || e.Holdout.Risk != risk)) throw new ArgumentException("Paper settings must match validated evidence.");
        if (string.IsNullOrWhiteSpace(codeVersion) || evidence.Any(e => e.Holdout.CodeVersion != codeVersion)) throw new ArgumentException("Paper executable/source version must match validated research.");
        var specs = evidence.Select(e => e.Holdout.Strategies.Single()).ToArray();
        if (specs.Select(s => s.Id).Distinct().Count() != specs.Length || specs.Select(s => s.Family).Distinct().Count() < 2)
            throw new ArgumentException("Diversify distinct versions and strategy families; correlation still requires review.");
        if (history.Bars.Any(b => b.AvailableAt >= now || Clock.Close(b.Date) >= now)) throw new ArgumentException("Future paper seed.");
        if (cohort == null) throw new ArgumentException("Independent winning results require joint portfolio validation; use a complete cohort archive.");
        if (cohort.Holdout.Costs != costs || cohort.Holdout.Risk != risk || cohort.Holdout.CodeVersion != codeVersion ||
            !cohort.Holdout.Strategies.SequenceEqual(specs)) throw new ArgumentException("Cohort portfolio settings/version mismatch.");
        var signals = Signals(history.Bars, specs, now);
        return new(Guid.NewGuid().ToString("N"), new[] { cohort.Id }.Concat(evidence.Select(e => e.Id)).ToArray(), specs, costs, risk, initial, initial,
            initial, initial, null, now, BacktestEngine.Regime(history, history.Dates.Length - 1, now), false, false,
            history.Bars, signals, [], [], [], [], 0, CodeVersion: codeVersion,
            ResearchCandidateCount: cohort.DeclaredHypotheses, CohortEvidenceId: cohort.Id);
    }

    public static PaperState Step(PaperState state, Observation observation, DateTimeOffset now)
    {
        state.Costs.Validate(); state.Risk.Validate();
        if (observation.Kind is not ("open" or "quote" or "close") || string.IsNullOrWhiteSpace(observation.Source)) throw new ArgumentException("Invalid observation.");
        if (observation.ObservedAt > now || now - observation.ObservedAt > TimeSpan.FromMinutes(5) || observation.ObservedAt <= state.LastObservation)
            throw new ArgumentException("Stale, replayed or future observation: historical replay is not paper trading.");
        var local = observation.ObservedAt.ToOffset(TimeSpan.FromHours(9)); var date = DateOnly.FromDateTime(local.DateTime);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || local.TimeOfDay < TimeSpan.FromHours(9) || local.TimeOfDay > new TimeSpan(15, 40, 0)) throw new ArgumentException("Outside regular session window.");
        if (observation.Kind == "open" && (local.TimeOfDay > new TimeSpan(9, 5, 0) || state.TradingDate >= date)) throw new ArgumentException("Invalid/repeated opening.");
        if (observation.Kind != "open" && state.TradingDate != date) throw new ArgumentException("Open event required before quotes/close.");
        if (observation.Kind == "close" && local.TimeOfDay < new TimeSpan(15, 30, 0)) throw new ArgumentException("Close before session end.");
        if (observation.Quotes.GroupBy(q => q.Ticker).Any(g => g.Count() > 1) || observation.Quotes.Length == 0) throw new ArgumentException("Missing/duplicate quotes.");
        foreach (var q in observation.Quotes)
            if (q.Time > observation.ObservedAt || observation.ObservedAt - q.Time > TimeSpan.FromSeconds(10) || q.Bid <= 0 || q.Ask < q.Bid || string.IsNullOrWhiteSpace(q.Sector)) throw new ArgumentException("Invalid or stale quote.");
        var map = observation.Quotes.ToDictionary(q => q.Ticker); var positions = state.Positions.ToList(); var fills = state.Fills.ToList();
        if (positions.Any(p => !map.ContainsKey(p.Signal.Ticker))) throw new ArgumentException("Missing held-security quote; cannot hide losses.");
        var actions = new List<string>(); var cash = state.Cash; var turnover = state.Turnover; var halted = state.Halted;
        var costs = state.Costs; var risk = state.Risk;
        var liquidityUsed = observation.Kind == "open" ? new Dictionary<string, int>() : new Dictionary<string, int>(state.LiquidityUsed ?? []);
        int RemainingLiquidity(string ticker)
        {
            var published = state.History.Where(b => b.Ticker == ticker && b.Date < date && date.DayNumber - b.Date.DayNumber <= 30 && b.AvailableAt < Clock.Open(date) && b.Tradable && b.Volume > 0).OrderBy(b => b.Date).LastOrDefault();
            return published == null ? 0 : (int)Math.Min(int.MaxValue, Math.Max(0, decimal.Floor(published.Volume * risk.Participation) - liquidityUsed.GetValueOrDefault(ticker)));
        }
        decimal Mark() => cash + positions.Sum(p => p.Quantity * map[p.Signal.Ticker].Bid * (1 - costs.Slippage) * (1 - costs.Commission - costs.SellTax));
        var equity = Mark(); var dayStart = observation.Kind == "open" ? state.Equity.LastOrDefault()?.Equity ?? state.InitialCapital : state.DayStartEquity;
        var peak = Math.Max(state.Peak, equity);
        if (equity / dayStart - 1 <= -risk.DailyLoss || 1 - equity / peak >= risk.Drawdown)
        { halted = true; actions.Add("PORTFOLIO_RISK_HALT"); }
        var history = state.History; var baseline = state.BaselineRegime;
        var pending = state.PendingSignals;
        if (observation.Kind == "close")
        {
            var bars = observation.ClosedBars;
            if (bars.Length == 0 || bars.Any(b => b.Date != date || b.AvailableAt > observation.ObservedAt) || bars.Select(b => b.Ticker).Distinct().Count() != bars.Length)
                throw new ArgumentException("Invalid close bars.");
            if (!bars.Select(b => b.Ticker).Order().SequenceEqual(history.Select(b => b.Ticker).Distinct().Order())) throw new ArgumentException("Incomplete close universe.");
            history = history.Concat(bars).ToArray();
            var data = new Dataset(observation.Source, false, false, history); data.Validate();
            var regime = BacktestEngine.Regime(data, data.Dates.Length - 1);
            if (baseline == "unknown" || baseline == null) baseline = regime;
            else if (regime != baseline) { halted = true; actions.Add($"REGIME_CHANGE:{baseline}->{regime}"); }
            pending = halted ? [] : Signals(history, state.Strategies, observation.ObservedAt);
        }
        else if (observation.ClosedBars.Length != 0) throw new ArgumentException("Close bars may only arrive with close event.");
        foreach (var p in positions.ToArray())
        {
            var q = map[p.Signal.Ticker]; if (!q.Tradable) { actions.Add($"UNFILLABLE:{q.Ticker}"); continue; }
            var holdingSessions = history.Select(b => b.Date).Distinct().Count(d => d >= DateOnly.FromDateTime(p.EntryTime.ToOffset(TimeSpan.FromHours(9)).DateTime) && d < date);
            var due = observation.Kind == "open" && holdingSessions >= p.Strategy.HoldDays;
            if (!halted && !due && q.Bid > p.StopLoss) continue;
            var quantity = Math.Min(p.Quantity, RemainingLiquidity(q.Ticker));
            if (quantity == 0) { actions.Add($"UNFILLED_EXIT:{q.Ticker}:liquidity"); continue; }
            var price = q.Bid * (1 - costs.Slippage); var proceeds = quantity * price * (1 - costs.Commission - costs.SellTax);
            var paid = p.Paid * quantity / p.Quantity;
            cash += proceeds; turnover += quantity * price;
            liquidityUsed[q.Ticker] = liquidityUsed.GetValueOrDefault(q.Ticker) + quantity;
            var reason = halted ? "risk-halt" : due ? "holding-period" : "stop";
            var trade = new Trade(p.Strategy.Id, q.Ticker, p.Signal.Time, p.Signal.Price, p.EntryTime, p.EntryPrice,
                observation.ObservedAt, price, quantity, proceeds - paid, proceeds / paid - 1, reason, p.Signal.EvidenceHash, quantity == p.Quantity);
            fills.Add(new(trade, p.StopLoss, null, null, baseline ?? "unknown", observation.Source)); positions.Remove(p); actions.Add($"SELL:{q.Ticker}:{reason}");
            if (quantity < p.Quantity) { positions.Add(p with { Quantity = p.Quantity - quantity, Paid = p.Paid - paid }); actions.Add($"PARTIAL_EXIT:{q.Ticker}"); }
        }
        if (observation.Kind == "open" && !halted)
        {
            foreach (var signal in pending.OrderByDescending(s => s.Score).ThenBy(s => s.StrategyId, StringComparer.Ordinal).ThenBy(s => s.Ticker, StringComparer.Ordinal))
            {
                if (!map.TryGetValue(signal.Ticker, out var q) || !q.Tradable || q.Time <= signal.Time || positions.Any(p => p.Strategy.Id == signal.StrategyId && p.Signal.Ticker == q.Ticker)) continue;
                var bar = history.Where(b => b.Ticker == q.Ticker).OrderBy(b => b.Date).Last();
                if (bar.Date >= date || !bar.Member || date.DayNumber - bar.Date.DayNumber > 7) continue;
                var price = q.Ask * (1 + costs.Slippage); var value = Mark();
                decimal Exposure(Func<PaperPosition, bool> filter) => positions.Where(filter).Sum(p => p.Quantity * map[p.Signal.Ticker].Ask);
                var capacity = new[] { cash / (1 + costs.Commission), value * risk.PositionCap - Exposure(p => p.Signal.Ticker == q.Ticker),
                    value * risk.StrategyCap - Exposure(p => p.Strategy.Id == signal.StrategyId), value * risk.ExposureCap - Exposure(_ => true),
                    value * risk.SectorCap - Exposure(p => p.Sector == q.Sector) }.Min();
                var quantity = (int)Math.Max(0, Math.Min(int.MaxValue, Math.Min(decimal.Floor(capacity / price), RemainingLiquidity(q.Ticker))));
                if (quantity == 0) continue;
                var paid = quantity * price * (1 + costs.Commission); cash -= paid; turnover += quantity * price;
                liquidityUsed[q.Ticker] = liquidityUsed.GetValueOrDefault(q.Ticker) + quantity;
                var spec = state.Strategies.Single(s => s.Id == signal.StrategyId);
                positions.Add(new(spec, signal, observation.ObservedAt, price, quantity, paid, price * (1 - risk.StopLoss), q.Sector)); actions.Add($"BUY:{q.Ticker}:{quantity}");
            }
            pending = [];
        }
        equity = Mark(); peak = Math.Max(peak, equity);
        if (equity / dayStart - 1 <= -risk.DailyLoss || 1 - equity / peak >= risk.Drawdown) { halted = true; pending = []; actions.Add("POST_FILL_RISK_HALT"); }
        var curve = state.Equity;
        if (observation.Kind == "close")
        {
            if (curve.Any(e => e.Date == date)) throw new ArgumentException("Repeated close.");
            curve = curve.Append(new EquityPoint(date, equity, equity / dayStart - 1, positions.Sum(p => p.Quantity * map[p.Signal.Ticker].Bid) / Math.Max(1, equity))).ToArray();
        }
        var previousHash = state.Audit.LastOrDefault()?.Hash ?? "GENESIS";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + JsonSerializer.Serialize(observation) + JsonSerializer.Serialize(actions))));
        var audit = state.Audit.Append(new PaperAudit(state.Audit.Length, previousHash, hash, observation, actions.ToArray())).ToArray();
        // File ingress cannot attest a feed; even an asserted VerifiedFeed flag is not trusted.
        return state with { Cash = cash, Peak = peak, DayStartEquity = dayStart, TradingDate = date, LastObservation = observation.ObservedAt,
            BaselineRegime = baseline, Halted = halted, VerifiedFeed = false, History = history, PendingSignals = pending,
            Positions = positions.ToArray(), Fills = fills.ToArray(), Equity = curve, Audit = audit, Turnover = turnover, LiquidityUsed = liquidityUsed };
    }
    private static Signal[] Signals(Bar[] bars, StrategySpec[] specs, DateTimeOffset now) => specs
        .SelectMany(spec => bars.GroupBy(b => b.Ticker).Select(g => new PriceStrategy(spec).Generate(g.Where(b => b.AvailableAt <= now).OrderBy(b => b.Date).ToArray(), now)))
        .Where(s => s != null).Cast<Signal>().ToArray();

    public static Evaluation Evaluate(PaperState state, int declaredCandidates, int minimumSessions = 120, int minimumTrades = 60)
        => EvaluateMetrics(state, declaredCandidates, minimumSessions, minimumTrades, committed: false);
    internal static Evaluation EvaluateCommitted(PaperState state)
        => EvaluateMetrics(state, state.ResearchCandidateCount, 120, 60, committed: true);
    private static Evaluation EvaluateMetrics(PaperState state, int declaredCandidates, int minimumSessions, int minimumTrades, bool committed)
    {
        var trades = state.Fills.Select(f => f.Trade).ToArray();
        var metrics = Statistics.Measure(state.Equity, trades, state.InitialCapital, state.Turnover, state.Positions.Length);
        var bound = Statistics.LowerMeanBound(state.Equity.Select(e => e.DailyReturn).ToArray(), declaredCandidates, block: Math.Max(5, state.Strategies.Max(s => s.HoldDays)));
        var reasons = new List<string>();
        if (!committed) reasons.Add("UNCOMMITTED_PAPER_EVIDENCE: standalone state is diagnostic only; evaluation requires the latest verified journal");
        if (!state.VerifiedFeed) reasons.Add("UNVERIFIED_FEED: manual ingress cannot prove real-time market observations");
        if (state.Halted) reasons.Add("RISK_OR_REGIME_HALT");
        if (state.Equity.Length < minimumSessions || metrics.NumberOfTrades < minimumTrades) reasons.Add("INSUFFICIENT_FORWARD_PAPER_EVIDENCE");
        if (metrics.ExpectedValuePerTrade <= 0 || bound <= 0) reasons.Add("NO_STATISTICALLY_SUPPORTED_POSITIVE_EXPECTANCY");
        if (metrics.MaximumDrawdown > state.Risk.Drawdown) reasons.Add("DRAWDOWN_EXCEEDED");
        return new(reasons.Count == 0 ? "REVIEW_ELIGIBLE_PAPER_ONLY" : "HOLD_OR_DISABLE", reasons.ToArray(), bound);
    }
}
