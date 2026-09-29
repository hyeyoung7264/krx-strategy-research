using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class CostScheduleTests
{
    private static readonly StrategySpec Spec = new("momentum", 2, 0, 2);
    private static readonly Risk Limits = new(.1m, .4m, .8m, .3m, .5m, .5m, .5m, .01m);
    private static Dataset Prices()
    {
        DateOnly[] days = [new(2024, 12, 23), new(2024, 12, 24), new(2024, 12, 26),
            new(2024, 12, 27), new(2024, 12, 30), new(2025, 1, 2), new(2025, 1, 3)];
        decimal[] prices = [100, 101, 102, 110, 111, 112, 113];
        return new("synthetic cost accounting", true, false, days.Select((d, i) =>
            new Bar("A", "s", d, Clock.Close(d), prices[i], prices[i] * 1.01m,
                prices[i] * .99m, prices[i], 100000, prices[i] * 100000, true, true)).ToArray());
    }
    private static SellTaxSchedule Schedule(Dataset data) => new("synthetic rates, not legal policy",
        data.Dates.Select((d, i) => new SellTaxSession(d, d.AddDays(3), i < 3 ? .01m : i < 5 ? .02m : .03m,
            "synthetic settlement fixture")).ToArray());
    private static RunResult Run(Dataset data, Costs costs, DateOnly? end = null) =>
        new BacktestEngine().Run(data, [Spec], data.Dates[0], end ?? data.Dates[^1], costs, Limits, 10000);

    [Fact] public void LegacyCostsKeepTheirSerializedShapeAndScalarRate()
    {
        const string json = "{\"Commission\":0.01,\"SellTax\":0.02,\"Slippage\":0.03}";
        var costs = JsonSerializer.Deserialize<Costs>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(costs));
        Assert.Equal(.02m, costs.SellTaxOn(new(2025, 1, 2)));
    }

    [Fact] public void ScheduledCostsHaveStructuralEqualityAfterJsonRoundTrip()
    {
        var costs = new Costs(.001m, .5m, .002m, Schedule(Prices()));
        var restored = JsonSerializer.Deserialize<Costs>(JsonSerializer.Serialize(costs))!;
        Assert.True(costs == restored);
        Assert.Equal(costs.GetHashCode(), restored.GetHashCode());
        var rows = restored.TaxSchedule!.Sessions;
        rows[0] = rows[0] with { Evidence = "different evidence" };
        var changed = restored with { TaxSchedule = new(restored.TaxSchedule.Source, rows) };
        Assert.True(costs != changed);
    }

    [Fact] public void ScheduleCannotBeMutatedThroughConstructorOrReturnedArray()
    {
        var row = new SellTaxSession(new(2024, 12, 27), new(2025, 1, 2), .02m, "fixture");
        var rows = new[] { row };
        var schedule = new SellTaxSchedule("fixture", rows);
        rows[0] = row with { Rate = .7m };
        var returned = schedule.Sessions;
        returned[0] = row with { Rate = .8m };
        Assert.Equal(.02m, schedule.RateOn(row.TradeDate));
    }

    [Fact] public void ExplicitTradeMappingCanCrossYearAndNeverFallsBack()
    {
        var previous = new SellTaxSession(new(2024, 12, 26), new(2024, 12, 30), .01m, "fixture old rate");
        var next = new SellTaxSession(new(2024, 12, 27), new(2025, 1, 2), .02m, "fixture new rate");
        var costs = new Costs(0, .5m, 0, new("fixture", [previous, next]));
        Assert.Equal(.01m, costs.SellTaxOn(previous.TradeDate));
        Assert.Equal(.02m, costs.SellTaxOn(next.TradeDate));
        Assert.Throws<ArgumentException>(() => costs.SellTaxOn(new(2025, 1, 2)));
    }

    [Fact] public void MalformedScheduleAndImpossibleCombinedCostsAreRejected()
    {
        var row = new SellTaxSession(new(2025, 1, 2), new(2025, 1, 6), .02m, "fixture");
        Assert.Throws<ArgumentException>(() => new SellTaxSchedule("", [row]));
        Assert.Throws<ArgumentException>(() => new SellTaxSchedule("fixture", []));
        Assert.Throws<ArgumentException>(() => new SellTaxSchedule("fixture", [row, row]));
        Assert.Throws<ArgumentException>(() => new SellTaxSchedule("fixture", [row with { TradeDate = row.TradeDate.AddDays(1) }, row]));
        Assert.Throws<ArgumentException>(() => new SellTaxSchedule("fixture", [row with { SettlementDate = row.TradeDate }]));
        Assert.Throws<ArgumentException>(() => new SellTaxSchedule("fixture", [row with { Evidence = " " }]));
        Assert.Throws<ArgumentException>(() => new SellTaxSchedule("fixture", [row with { Rate = -.01m }]));
        Assert.Throws<ArgumentException>(() => new SellTaxSchedule("fixture", [row with { Rate = 1 }]));
        Assert.Throws<ArgumentException>(() => new Costs(.99m, 0, 0, new("fixture", [row])).Validate());
    }

    [Fact] public void BacktestRequiresEveryRequestedSessionEvenWhenNoTradeOccurs()
    {
        var data = Prices(); var rows = Schedule(data).Sessions.Take(2).ToArray();
        var costs = new Costs(0, 0, 0, new("fixture", rows));
        var failure = Assert.Throws<ArgumentException>(() => Run(data, costs, data.Dates[2]));
        Assert.Contains("2024-12-26", failure.Message);
        var covered = Run(data, costs, data.Dates[1]);
        Assert.Empty(covered.Trades);
    }

    [Fact] public void BacktestUsesExitDateRateAndGrossRunRemainsUntaxed()
    {
        var data = Prices(); var costs = new Costs(.001m, .5m, 0, Schedule(data));
        var run = Run(data, costs); var trade = Assert.Single(run.Trades);
        var paid = trade.Quantity * trade.EntryPrice * 1.001m;
        var proceeds = trade.Quantity * trade.ExitPrice * (1 - .001m - .03m);
        Assert.Equal(proceeds - paid, trade.NetProfit);
        Assert.Equal(Run(data, new(0, 0, 0)).Metrics, run.GrossMetrics);
    }

    [Fact] public void BacktestLiquidationUsesCurrentMappingBeforeAnyExit()
    {
        var data = Prices(); var costs = new Costs(0, .5m, 0, Schedule(data));
        var run = Run(data, costs, data.Dates[3]);
        Assert.Empty(run.Trades); Assert.Equal(1, run.Metrics.OpenPositions);
        Assert.Equal(10000 - 9 * 110 + 9 * 110 * .98m, run.Equity[^1].Equity);
    }

    [Fact] public void OpeningRiskUsesCurrentNetLiquidationCostsAndMatchesPaper()
    {
        var data = Prices(); var dates = data.Dates;
        // Deliberately large synthetic change exposes the accounting boundary; not a tax policy.
        var costs = new Costs(0, 0, 0, new("synthetic rate stress", dates.Select((date, offset) =>
            new SellTaxSession(date, date.AddDays(3), offset < 4 ? 0 : .4m, "fixture")).ToArray()));
        var risk = Limits with { DailyLoss = .01m };
        var run = new BacktestEngine().Run(data, [Spec], dates[0], dates[^1], costs, risk, 10000);
        var trade = Assert.Single(run.Trades);
        Assert.Equal(Clock.Open(dates[4]), trade.ExitTime); Assert.Equal("risk-halt", trade.Reason);
        Assert.Contains(run.Events, e => e.StartsWith($"{dates[4]:yyyy-MM-dd}: OPEN_RISK_LIMIT", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Events, e => e.Contains("CLOSE_RISK_LIMIT", StringComparison.Ordinal));
        var expectedCash = 10000 - 9 * 110 + 9 * 111 * .6m;
        Assert.Equal(expectedCash, run.Equity.Single(e => e.Date == dates[4]).Equity);

        var history = data.Bars.Where(b => b.Date <= dates[3]).ToArray();
        var signal = new PriceStrategy(Spec).Generate(history.Take(3).ToArray(), Clock.Open(dates[3]).AddTicks(-1))!;
        var state = new PaperState("fixture", ["research"], [Spec], costs, risk, 10000, 9010, 10000, 10000,
            dates[3], Clock.Close(dates[3]), "unknown", false, false, history, [],
            [new(Spec, signal, Clock.Open(dates[3]), 110, 9, 990, 55, "s")], [],
            [new(dates[3], 10000, 0, .099m)], [], 990);
        var paper = PaperEngine.Step(state, Quote(dates[4], 111), Clock.Open(dates[4]));
        Assert.True(paper.Halted); Assert.Empty(paper.Positions); Assert.Equal(expectedCash, paper.Cash);
    }

    [Fact] public void OpeningCapitalUsesNetEquityWhileExistingExposureRemainsGross()
    {
        var data = Prices(); var dates = data.Dates;
        decimal[] secondPrices = [100, 100, 100, 110, 100, 100, 100];
        var second = dates.Select((date, offset) => new Bar("B", "s", date, Clock.Close(date), secondPrices[offset],
            secondPrices[offset] * 1.01m, secondPrices[offset] * .99m, secondPrices[offset], 100000, secondPrices[offset] * 100000, true, true));
        data = data with { Bars = data.Bars.Concat(second).ToArray() };
        var costs = new Costs(0, 0, 0, new("synthetic rate stress", dates.Select((date, offset) =>
            new SellTaxSession(date, date.AddDays(3), offset < 4 ? 0 : .4m, "fixture")).ToArray()));
        var risk = Limits with { ExposureCap = .15m };
        var run = new BacktestEngine().Run(data, [Spec], dates[0], dates[^1], costs, risk, 10000);
        var trade = Assert.Single(run.Trades, t => t.Ticker == "B");
        Assert.Equal(Clock.Open(dates[4]), trade.EntryTime);
        Assert.Equal(4, trade.Quantity); // Gross equity permits 5; netting held exposure incorrectly permits 8.
        var equity = run.Equity.Single(e => e.Date == dates[4]);
        var rawExposure = 9 * 111 + 4 * 100;
        Assert.Equal(10000 - 9 * 110 - 4 * 100 + rawExposure * .6m, equity.Equity);
        Assert.Equal(rawExposure / equity.Equity, equity.Exposure);
        Assert.True(equity.Exposure <= risk.ExposureCap);
    }

    private static PaperState Holding(Costs costs, DateOnly day)
    {
        var bar = new Bar("A", "s", day.AddDays(-3), Clock.Close(day.AddDays(-3)), 100, 101, 99, 100,
            100000, 10000000, true, true);
        var signal = new Signal(Spec.Id, "A", bar.AvailableAt, 100, .1m, "fixture", "fixture");
        return new("fixture", ["research"], [Spec], costs, Limits, 10000, 9000, 10000, 10000, null,
            Clock.Open(day).AddMinutes(-1), "unknown", false, false, [bar], [],
            [new(Spec, signal, Clock.Open(day.AddDays(-3)), 100, 10, 1000, 50, "s")], [], [], [], 0);
    }
    private static Observation Quote(DateOnly day, decimal price) => new("fixture", "open", Clock.Open(day),
        [new("A", "s", Clock.Open(day), price, price, true)], []);

    [Fact] public void PaperUsesSameScheduledRateForLiquidationAndExit()
    {
        var day = new DateOnly(2026, 9, 28);
        var costs = new Costs(0, .5m, 0, new("fixture", [new(day, day.AddDays(2), .02m, "fixture")]));
        var state = Holding(costs, day);
        var held = PaperEngine.Step(state, Quote(day, 100), Clock.Open(day));
        var close = Clock.Close(day);
        var observation = new Observation("fixture", "close", close, [new("A", "s", close, 100, 100, true)],
            [new("A", "s", day, close, 100, 101, 99, 100, 100000, 10000000, true, true)]);
        var marked = PaperEngine.Step(held, observation, close);
        Assert.Empty(marked.Fills); Assert.Equal(9980, Assert.Single(marked.Equity).Equity);
        var sold = PaperEngine.Step(state with { Halted = true }, Quote(day, 100), Clock.Open(day));
        Assert.Empty(sold.Positions);
        Assert.Equal(9980, sold.Cash);
        Assert.Equal(-20, Assert.Single(sold.Fills).Trade.NetProfit);
    }

    [Fact] public void PaperRefusesUnmappedObservationDateEvenWithoutPositions()
    {
        var day = new DateOnly(2026, 9, 28);
        var costs = new Costs(0, 0, 0, new("fixture", [new(day.AddDays(-1), day.AddDays(1), .02m, "fixture")]));
        var state = Holding(costs, day) with { Positions = [], Cash = 10000 };
        Assert.Throws<ArgumentException>(() => PaperEngine.Step(state, Quote(day, 100), Clock.Open(day)));
    }
}
