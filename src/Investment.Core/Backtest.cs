namespace Investment.Core;

public sealed class BacktestEngine
{
    private sealed record Position(StrategySpec Strategy, Bar EntryBar, Signal Signal, int Quantity, decimal Price, decimal Paid, int EntryIndex);

    public RunResult Run(Dataset data, StrategySpec[] specs, DateOnly start, DateOnly end, Costs costs, Risk risk,
        decimal initial = 100_000_000m, string codeVersion = "unversioned")
    {
        var net = Simulate(data, specs, start, end, costs, risk, initial, codeVersion);
        var gross = costs == new Costs(0, 0, 0) ? net : Simulate(data, specs, start, end, new Costs(0, 0, 0), risk, initial, codeVersion);
        return net with { GrossMetrics = gross.Metrics };
    }

    private RunResult Simulate(Dataset data, StrategySpec[] specs, DateOnly start, DateOnly end, Costs costs, Risk risk,
        decimal initial, string codeVersion)
    {
        data.Validate(); costs.Validate(); risk.Validate();
        if (initial <= 0 || specs.Length == 0 || specs.Select(s => s.Id).Distinct().Count() != specs.Length) throw new ArgumentException("Invalid capital/strategies.");
        foreach (var s in specs) s.Validate();
        var dates = data.Dates;
        if (start > end || !dates.Contains(start) || !dates.Contains(end)) throw new ArgumentException("Invalid period.");
        var byDate = data.Bars.GroupBy(b => b.Date).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Ticker, StringComparer.Ordinal).ToArray());
        var history = data.Bars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToArray());
        var holdings = new List<Position>(); var trades = new List<Trade>(); var curve = new List<EquityPoint>(); var events = new List<string>();
        decimal cash = initial, previous = initial, peak = initial, turnover = 0;
        bool halted = false; string? baselineRegime = null;
        var first = Array.IndexOf(dates, start); var last = Array.IndexOf(dates, end);
        for (var i = first; i <= last; i++)
        {
            var date = dates[i]; var today = byDate[date]; var map = today.ToDictionary(b => b.Ticker);
            var prior = i == 0 ? null : byDate[dates[i - 1]];
            var decisionTime = Clock.Open(date).AddTicks(-1);
            var regime = Regime(data, i - 1, decisionTime);
            if (baselineRegime == null && regime != "unknown") baselineRegime = regime;
            if (baselineRegime != null && regime != "unknown" && regime != baselineRegime && !halted)
            { halted = true; events.Add($"{date:yyyy-MM-dd}: REGIME_CHANGE {baselineRegime}->{regime}; halt and exit when tradable"); }

            decimal Mark(Func<Bar, decimal> price) => cash + holdings.Sum(p => p.Quantity * price(map[p.EntryBar.Ticker]));
            decimal LiquidationMark() => cash + holdings.Sum(p => p.Quantity * map[p.EntryBar.Ticker].Close * (1 - costs.Slippage) * (1 - costs.Commission - costs.SellTax));
            var liquidityUsed = new Dictionary<string, int>();
            int RemainingLiquidity(string ticker)
            {
                var published = history[ticker].LastOrDefault(b => b.Date < date && date.DayNumber - b.Date.DayNumber <= 30 && b.AvailableAt <= decisionTime && b.Tradable && b.Volume > 0);
                return published == null ? 0 : (int)Math.Min(int.MaxValue, Math.Max(0, decimal.Floor(published.Volume * risk.Participation) - liquidityUsed.GetValueOrDefault(ticker)));
            }
            void Exit(Position p, decimal raw, string reason)
            {
                var b = map[p.EntryBar.Ticker];
                var quantity = Math.Min(p.Quantity, RemainingLiquidity(b.Ticker));
                if (quantity == 0) { events.Add($"{date:yyyy-MM-dd}: UNFILLED_EXIT {b.Ticker}; prior published liquidity budget exhausted"); return; }
                var price = raw * (1 - costs.Slippage);
                var amount = quantity * price; var proceeds = amount * (1 - costs.Commission - costs.SellTax);
                var paid = p.Paid * quantity / p.Quantity;
                liquidityUsed[b.Ticker] = liquidityUsed.GetValueOrDefault(b.Ticker) + quantity;
                cash += proceeds; turnover += amount;
                trades.Add(new(p.Strategy.Id, b.Ticker, p.Signal.Time, p.Signal.Price, Clock.Open(p.EntryBar.Date), p.Price,
                    reason == "stop" ? Clock.Close(date) : Clock.Open(date), price, quantity, proceeds - paid,
                    proceeds / paid - 1, reason, p.Signal.EvidenceHash, quantity == p.Quantity));
                holdings.Remove(p);
                if (quantity < p.Quantity)
                {
                    holdings.Add(p with { Quantity = p.Quantity - quantity, Paid = p.Paid - paid });
                    events.Add($"{date:yyyy-MM-dd}: PARTIAL_EXIT {b.Ticker}; remaining {p.Quantity - quantity}");
                }
            }
            // Tradable must represent point-in-time execution eligibility (including suspension/limit locks).
            // Never infer an opening fill decision from the future full-session high/low/volume.
            bool Executable(Bar b) => b.Tradable;
            foreach (var p in holdings.ToArray())
            {
                var b = map[p.EntryBar.Ticker];
                if (!Executable(b)) continue;
                if (halted || !b.Member || i - p.EntryIndex >= p.Strategy.HoldDays)
                    Exit(p, b.Open, halted ? "risk-halt" : !b.Member ? "universe-exit" : "holding-period");
                else if (b.Open <= p.Price * (1 - risk.StopLoss)) Exit(p, b.Open, "gap-stop");
            }

            var openingEquity = Mark(b => b.Open);
            if (openingEquity / previous - 1 <= -risk.DailyLoss || 1 - openingEquity / peak >= risk.Drawdown)
            {
                halted = true; events.Add($"{date:yyyy-MM-dd}: OPEN_RISK_LIMIT; gaps can exceed limits");
                foreach (var p in holdings.ToArray()) if (Executable(map[p.EntryBar.Ticker])) Exit(p, map[p.EntryBar.Ticker].Open, "risk-halt");
            }
            if (!halted && prior != null)
            {
                var candidates = new List<(StrategySpec Spec, Signal Signal)>();
                foreach (var spec in specs)
                foreach (var ticker in history.Keys.Order(StringComparer.Ordinal))
                {
                    // No same-day OHLCV or membership is exposed to the signal generator.
                    var now = decisionTime;
                    var past = history[ticker].Where(b => b.Date <= dates[i - 1] && b.AvailableAt <= now).ToArray();
                    if (past.Length == 0 || past[^1].Date != dates[i - 1]) continue;
                    var signal = new PriceStrategy(spec).Generate(past, now);
                    if (signal != null) candidates.Add((spec, signal));
                }
                foreach (var candidate in candidates.OrderByDescending(c => c.Signal.Score).ThenBy(c => c.Spec.Id, StringComparer.Ordinal).ThenBy(c => c.Signal.Ticker, StringComparer.Ordinal))
                {
                    var b = map[candidate.Signal.Ticker];
                    // Today's volume is NOT used to size an open fill. Capacity uses the previously published bar.
                    if (!b.Member || !Executable(b) || holdings.Any(p => p.Strategy.Id == candidate.Spec.Id && p.EntryBar.Ticker == b.Ticker)) continue;
                    var price = b.Open * (1 + costs.Slippage);
                    var equity = Mark(x => x.Open);
                    var exposure = holdings.Sum(p => p.Quantity * map[p.EntryBar.Ticker].Open);
                    var strategyExposure = holdings.Where(p => p.Strategy.Id == candidate.Spec.Id).Sum(p => p.Quantity * map[p.EntryBar.Ticker].Open);
                    var sectorExposure = holdings.Where(p => map[p.EntryBar.Ticker].Sector == b.Sector).Sum(p => p.Quantity * map[p.EntryBar.Ticker].Open);
                    var tickerExposure = holdings.Where(p => p.EntryBar.Ticker == b.Ticker).Sum(p => p.Quantity * map[p.EntryBar.Ticker].Open);
                    var capacity = new[] { cash / (1 + costs.Commission), equity * risk.PositionCap - tickerExposure,
                        equity * risk.StrategyCap - strategyExposure, equity * risk.ExposureCap - exposure,
                        equity * risk.SectorCap - sectorExposure }.Min();
                    var quantity = (int)Math.Min(int.MaxValue, Math.Max(0, Math.Min(decimal.Floor(capacity / price), RemainingLiquidity(b.Ticker))));
                    if (quantity <= 0) continue;
                    var paid = quantity * price * (1 + costs.Commission);
                    cash -= paid; turnover += quantity * price;
                    liquidityUsed[b.Ticker] = liquidityUsed.GetValueOrDefault(b.Ticker) + quantity;
                    holdings.Add(new(candidate.Spec, b, candidate.Signal, quantity, price, paid, i));
                }
            }
            foreach (var p in holdings.ToArray())
            {
                var b = map[p.EntryBar.Ticker];
                if (Executable(b) && b.Low <= p.Price * (1 - risk.StopLoss)) Exit(p, Math.Min(b.Open, p.Price * (1 - risk.StopLoss)), "stop");
            }
            // Mark open positions at estimated net liquidation value; unrealized losses are not omitted.
            var closeEquity = LiquidationMark(); peak = Math.Max(peak, closeEquity);
            var daily = closeEquity / previous - 1;
            if (!halted && (daily <= -risk.DailyLoss || 1 - closeEquity / peak >= risk.Drawdown))
            { halted = true; events.Add($"{date:yyyy-MM-dd}: CLOSE_RISK_LIMIT; next tradable open exit"); }
            curve.Add(new(date, closeEquity, daily, holdings.Sum(p => p.Quantity * map[p.EntryBar.Ticker].Close) / Math.Max(1, closeEquity)));
            previous = closeEquity;
        }
        var metrics = Statistics.Measure(curve, trades, initial, turnover, holdings.Count);
        return new(Guid.NewGuid().ToString("N"), data.Hash, data.Synthetic, start, end, specs, costs, risk, initial, metrics,
            curve.ToArray(), trades.ToArray(), events.ToArray(), codeVersion);
    }

    public static string Regime(Dataset data, int throughIndex, DateTimeOffset? knownAt = null)
    {
        var dates = data.Dates;
        if (throughIndex < 20) return "unknown";
        // Point-in-time equal-weight proxy, using only continuously present members in the trailing window.
        var end = dates[throughIndex]; var begin = dates[throughIndex - 20]; var now = knownAt ?? Clock.Close(end);
        var moves = data.Bars.Where(b => b.Date >= begin && b.Date <= end).GroupBy(b => b.Ticker)
            .Where(g => g.Count() == 21 && g.All(b => b.Member && b.Tradable && b.AvailableAt <= now))
            .Select(g => { var a = g.OrderBy(b => b.Date).ToArray(); return a[^1].Close / a[0].Close - 1; }).ToArray();
        if (moves.Length == 0) return "unknown";
        var average = moves.Average();
        return average > .03m ? "bull" : average < -.03m ? "bear" : "sideways";
    }
}
