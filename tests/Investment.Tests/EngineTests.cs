using Investment.Core;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Investment.Tests;

public sealed class EngineTests
{
    private static readonly StrategySpec Momentum = new("momentum", 2, 0, 2);
    private static readonly Costs Zero = new(0, 0, 0);
    private static readonly Risk Limits = new(.1m, .4m, .8m, .3m, .5m, .5m, .5m, .01m);
    private static Dataset Prices(params decimal[] prices)
    {
        var dates = Enumerable.Range(0, prices.Length).Select(i => new DateOnly(2024, 1, 2).AddDays(i)).ToArray();
        return new("test", true, false, prices.Select((p, i) => new Bar("A", "sector", dates[i], Clock.Close(dates[i]), p, p * 1.01m, p * .99m, p, 100000, p * 100000, true, true)).ToArray());
    }
    private static RunResult Run(Dataset data, Costs? cost = null, Risk? risk = null, DateOnly? end = null) =>
        new BacktestEngine().Run(data, [Momentum], data.Dates[0], end ?? data.Dates[^1], cost ?? Zero, risk ?? Limits, initial: 10000);

    [Fact] public void SignalsCannotReadFuture()
    {
        var data = Prices(100, 101, 102);
        Assert.Throws<ArgumentException>(() => new PriceStrategy(Momentum).Generate(data.Bars, Clock.Close(data.Dates[1])));
    }
    [Fact] public void SignalAtCloseFillsAtNextOpen()
    {
        var r = Run(Prices(100, 101, 102, 110, 111, 112, 113));
        var trade = Assert.Single(r.Trades);
        Assert.Equal(102, trade.SignalPrice);
        Assert.Equal(110, trade.EntryPrice);
        Assert.True(trade.SignalTime < trade.EntryTime);
        Assert.Equal(112, trade.ExitPrice);
    }
    [Fact] public void FutureMutationDoesNotChangeEarlierEquityOrTrades()
    {
        var data = DataFiles.Demo(90); var end = data.Dates[59];
        var changed = data with { Bars = data.Bars.Select(b => b.Date > end ? b with { Open = b.Open * 10, High = b.High * 10, Low = b.Low * 10, Close = b.Close * 10 } : b).ToArray() };
        var before = Run(data, end: end); var after = Run(changed, end: end);
        Assert.Equal(before.Equity, after.Equity); Assert.Equal(before.Trades, after.Trades); Assert.Equal(before.Metrics, after.Metrics);
    }
    [Fact] public void CostsAreDebitedFromBothSidesAndTaxOnlyOnExit()
    {
        var r = Run(Prices(100, 101, 102, 110, 111, 112, 113), new Costs(.01m, .02m, .01m));
        var trade = Assert.Single(r.Trades);
        var paid = trade.Quantity * 111.1m * 1.01m;
        var proceeds = trade.Quantity * 110.88m * .97m;
        Assert.Equal(proceeds - paid, trade.NetProfit); Assert.Equal(proceeds / paid - 1, trade.Return);
        Assert.True(r.Metrics.TotalReturn < Run(Prices(100, 101, 102, 110, 111, 112, 113)).Metrics.TotalReturn);
    }
    [Fact] public void CapacityUsesPreviousVolume()
    {
        var data = Prices(100, 101, 102, 110, 111, 112, 113);
        var changed = data with { Bars = data.Bars.Select((b, i) => i == 3 ? b with { Volume = 1 } : b).ToArray() };
        Assert.Equal(Run(data).Trades[0].Quantity, Run(changed).Trades[0].Quantity);
    }
    [Fact] public void PriorDailyDataPublishedNextMorningCanInformNextOpen()
    {
        var data = Prices(100, 101, 102, 110, 111, 112, 113);
        var delayed = data with { Bars = data.Bars.Select(b => b with { AvailableAt = Clock.Open(b.Date.AddDays(1)).AddHours(-1) }).ToArray() };
        Assert.Equal(Run(data).Metrics, Run(delayed).Metrics);
        var unavailable = data with { Bars = data.Bars.Select(b => b with { AvailableAt = Clock.Open(b.Date.AddDays(1)).AddHours(1) }).ToArray() };
        Assert.Empty(Run(unavailable).Trades);
    }
    [Fact] public void SuspendedSecurityIsNotLiquidatedAtInventedPrice()
    {
        var data = Prices(100, 101, 102, 110, 111, 112, 113, 114);
        data = data with { Bars = data.Bars.Select((b, i) => i is 5 or 6 ? b with { Tradable = false, Volume = 0 } : b).ToArray() };
        var trade = Assert.Single(Run(data).Trades);
        Assert.Equal(data.Dates[7], DateOnly.FromDateTime(trade.ExitTime.DateTime));
    }
    [Fact] public void ExitParticipationBudgetProducesPartialExecutionsWithoutInflatingTradeCount()
    {
        var data = Prices(100, 101, 102, 110, 111, 112, 113, 114);
        data = data with { Bars = data.Bars.Select((b, i) => i is 4 or 5 or 6 ? b with { Volume = 200 } : b).ToArray() };
        var run = Run(data);
        Assert.True(run.Trades.Length > 1); Assert.True(run.Trades.Sum(t => t.Quantity) <= 6);
        Assert.All(run.Trades, t => Assert.False(t.CompletesPosition));
        Assert.Equal(0, run.Metrics.NumberOfTrades); Assert.Equal(1, run.Metrics.OpenPositions);
        Assert.Contains(run.Events, e => e.Contains("PARTIAL_EXIT"));
    }
    [Fact] public void MissingAndDuplicateRowsAreRejected()
    {
        var data = DataFiles.Demo(10);
        Assert.Throws<ArgumentException>(() => (data with { Bars = data.Bars.Skip(1).ToArray() }).Validate());
        Assert.Throws<ArgumentException>(() => (data with { Bars = data.Bars.Append(data.Bars[0]).ToArray() }).Validate());
    }
    [Fact] public void CorporateActionsFailClosedInsteadOfFakingAdjustedPrices()
    {
        var data = Prices(100, 101, 102);
        Assert.Throws<ArgumentException>(() => (data with { Bars = data.Bars.Select(b => b with { CorporateAction = true }).ToArray() }).Validate());
    }
    [Fact] public void NetLiquidationValueIncludesOpenPositionCosts()
    {
        var r = Run(Prices(100, 101, 102, 110), new Costs(.01m, .02m, .01m));
        Assert.Equal(1, r.Metrics.OpenPositions); Assert.Empty(r.Trades); Assert.True(r.Metrics.TotalReturn < 0);
    }
    [Fact] public void DailyLossHaltsNewEntriesAndExitsAtNextAvailableOpen()
    {
        var data = Prices(100, 101, 102, 110, 70, 70, 72, 75, 80);
        var risk = Limits with { DailyLoss = .01m, Drawdown = .02m };
        var run = Run(data, risk: risk);
        Assert.Contains(run.Events, e => e.Contains("RISK_LIMIT"));
        Assert.Single(run.Trades); Assert.Equal(0, run.Metrics.OpenPositions);
        Assert.True(run.Metrics.MaximumDrawdown > .02m); // A limit is a trigger, not guaranteed protection against gaps.
    }
    [Fact] public void StrategyAndTickerExposureCapsApply()
    {
        var r = Run(Prices(100, 101, 102, 110), risk: Limits with { PositionCap = .05m });
        Assert.True(r.Equity[^1].Exposure <= .05m);
        Assert.Throws<ArgumentException>(() => (Limits with { StrategyCap = .6m }).Validate());
    }
    [Fact] public void ResultsAreDeterministicExceptIdentity()
    {
        var data = DataFiles.Demo(80); var a = Run(data); var b = Run(data);
        Assert.Equal(a.Equity, b.Equity); Assert.Equal(a.Trades, b.Trades); Assert.Equal(a.Metrics, b.Metrics);
        Assert.Equal(a.DataHash, b.DataHash);
    }
    [Fact] public void MetricsIncludeFlatAndLosingSessions()
    {
        EquityPoint[] curve = [new(new(2024, 1, 2), 90, -.1m, 0), new(new(2024, 1, 3), 100, 1m / 9, 0)];
        var metrics = Statistics.Measure(curve, [], 100, 0, 0);
        Assert.Equal(.1m, metrics.MaximumDrawdown); Assert.Equal(0, metrics.TotalReturn); Assert.Null(metrics.ProfitFactor); Assert.Equal(0, metrics.NumberOfTrades);
    }
    [Fact] public void BootstrapCorrectsForCandidateCountAndIsReproducible()
    {
        var values = Enumerable.Range(0, 120).Select(i => i % 3 == 0 ? -.01m : .005m).ToArray();
        Assert.True(Statistics.LowerMeanBound(values, 10) <= Statistics.LowerMeanBound(values, 1));
        Assert.Equal(Statistics.LowerMeanBound(values, 4), Statistics.LowerMeanBound(values, 4));
        Assert.Equal(decimal.MinValue, Statistics.LowerMeanBound(values.Take(10).ToArray(), 1));
    }
    [Fact] public void WalkForwardTestsAreDisjointAndHoldoutIsSealed()
    {
        var data = DataFiles.Demo(210); var plan = new ResearchPlan(60, 30, 30, 30, 1, 30);
        var r = new ResearchAgent().Run(data, new BaselineHypotheses().Generate(), plan, Zero, Limits, "test");
        Assert.True(r.Folds.Length >= 2);
        foreach (var f in r.Folds)
        { Assert.True(f.TrainEnd < f.ValidationStart); Assert.True(f.ValidationEnd < f.TestStart); Assert.True(f.TestEnd < r.Holdout.Start); }
        for (var i = 1; i < r.Folds.Length; i++) Assert.True(r.Folds[i - 1].TestEnd < r.Folds[i].TestStart);
        Assert.Contains(r.Evaluation.Reasons, x => x.Contains("SYNTHETIC")); Assert.NotEqual("PAPER_ELIGIBLE", r.Evaluation.Decision);
    }
    [Fact] public void AlteringHoldoutCannotChangeChosenParameters()
    {
        var data = DataFiles.Demo(180); var plan = new ResearchPlan(60, 30, 30, 30, 1, 30);
        var first = data.Dates[^30];
        var changed = data with { Bars = data.Bars.Select(b => b.Date >= first ? b with { Open = b.Open * 2, High = b.High * 2, Low = b.Low * 2, Close = b.Close * 2 } : b).ToArray() };
        var agent = new ResearchAgent(); var candidates = new BaselineHypotheses().Generate();
        var a = agent.Run(data, candidates, plan, Zero, Limits, "test"); var b = agent.Run(changed, candidates, plan, Zero, Limits, "test");
        Assert.Equal(a.Holdout.Strategies, b.Holdout.Strategies); Assert.Equal(a.FinalValidation.Metrics, b.FinalValidation.Metrics);
        Assert.Equal(a.Folds.Select(f => f.SelectedId), b.Folds.Select(f => f.SelectedId));
    }
    [Fact] public void SmallDatasetCannotSkipWalkForward()
    {
        Assert.Throws<ArgumentException>(() => new ResearchAgent().Run(DataFiles.Demo(100), new BaselineHypotheses().Generate(), new(), Zero, Limits, "test"));
    }
    [Fact] public void EvidenceFilesNeverOverwriteEarlierResults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "research-tests-" + Guid.NewGuid().ToString("N"));
        try { var store = new EvidenceStore(dir); store.Save("test", "1", new { Failed = true }); Assert.Throws<IOException>(() => store.Save("test", "1", new { Failed = false })); }
        finally { Directory.Delete(dir, true); }
    }
    [Fact] public void PostgreSqlSchemaIncludesCompositePriceKeyAndRestrictiveForeignKeys()
    {
        using var db = new ResearchDb(new DbContextOptionsBuilder<ResearchDb>().UseNpgsql("Host=localhost;Database=unused").Options);
        var sql = db.Database.GenerateCreateScript(); Assert.Contains("jsonb", sql); Assert.Contains("ON DELETE RESTRICT", sql); Assert.Contains("DataHash", sql);
        var revision = new DataRevision { Hash = "x", Source = "test", Json = "{}" }; db.Attach(revision); revision.Source = "changed";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }
}
