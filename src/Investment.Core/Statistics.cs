namespace Investment.Core;

public static class Statistics
{
    public static Metrics Measure(IReadOnlyList<EquityPoint> curve, IReadOnlyList<Trade> trades, decimal initial, decimal turnover, int open)
    {
        // Partial executions must not inflate independent trade counts or per-trade expectancy.
        // Fill quantities can be in different share units after a split. Aggregate only
        // the economic profit and completed position's holding interval used by metrics.
        var completed = trades.GroupBy(t => (t.StrategyId, t.Ticker, t.EntryTime))
            .Where(g => g.Any(t => t.CompletesPosition))
            .Select(g => new { NetProfit = g.Sum(t => t.NetProfit), g.Key.EntryTime, ExitTime = g.Last().ExitTime }).ToArray();
        var r = curve.Select(p => (double)p.DailyReturn).ToArray();
        var mean = r.Length == 0 ? 0 : r.Average();
        var sd = r.Length < 2 ? 0 : Math.Sqrt(r.Sum(x => Math.Pow(x - mean, 2)) / (r.Length - 1));
        var downside = r.Length == 0 ? 0 : Math.Sqrt(r.Sum(x => Math.Pow(Math.Min(x, 0), 2)) / r.Length);
        decimal peak = initial, mdd = 0;
        foreach (var p in curve) { peak = Math.Max(peak, p.Equity); mdd = Math.Max(mdd, 1 - p.Equity / peak); }
        var profit = completed.Where(t => t.NetProfit > 0).Sum(t => t.NetProfit);
        var loss = -completed.Where(t => t.NetProfit < 0).Sum(t => t.NetProfit);
        var winners = completed.Where(t => t.NetProfit > 0).ToArray(); var losers = completed.Where(t => t.NetProfit < 0).ToArray();
        var total = curve.Count == 0 ? 0 : curve[^1].Equity / initial - 1;
        return new(total, curve.Count > 0 && total > -1 ? Math.Pow((double)(1 + total), 252d / curve.Count) - 1 : null,
            (decimal)mean, completed.Length == 0 ? 0 : (decimal)winners.Length / completed.Length,
            loss == 0 ? null : profit / loss, winners.Length == 0 ? 0 : winners.Average(t => t.NetProfit),
            losers.Length == 0 ? 0 : losers.Average(t => t.NetProfit), completed.Length == 0 ? 0 : completed.Average(t => t.NetProfit),
            mdd, sd == 0 ? null : mean / sd * Math.Sqrt(252), downside == 0 ? null : mean / downside * Math.Sqrt(252),
            completed.Length, turnover / initial, completed.Length == 0 ? 0 : completed.Average(t => (t.ExitTime - t.EntryTime).TotalDays), open);
    }
    // Moving-block bootstrap preserves short-range dependence. Bonferroni corrects the declared candidate count.
    public static decimal LowerMeanBound(decimal[] returns, int candidates, int seed = 271828, int samples = 4000, int block = 5)
    {
        if (returns.Length < 30 || candidates < 1 || samples < 1000 || block < 1 || samples * .05 / candidates < 1) return decimal.MinValue;
        var random = new Random(seed); var means = new decimal[samples];
        for (var s = 0; s < samples; s++)
        {
            decimal sum = 0; int count = 0;
            while (count < returns.Length)
            {
                var start = random.Next(returns.Length);
                for (var j = 0; j < block && count < returns.Length; j++, count++) sum += returns[(start + j) % returns.Length];
            }
            means[s] = sum / returns.Length;
        }
        Array.Sort(means);
        return means[Math.Max(0, (int)Math.Floor(samples * .05 / candidates) - 1)];
    }
}
