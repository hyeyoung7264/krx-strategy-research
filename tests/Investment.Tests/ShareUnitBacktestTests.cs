using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class ShareUnitBacktestTests
{
    private static readonly DateOnly First = new(2024, 1, 2);
    private static DateOnly Day(int index) => First.AddDays(index);
    private static readonly StrategySpec Momentum = new("momentum", 2, .001m, 2);
    private static readonly Costs Zero = new(0, 0, 0);
    private static readonly Risk Risk = new(.1m, .4m, .8m, .3m, .5m, .5m, .5m, .01m);
    private static ShareUnitChange Change(int effective = 4, int price = 6, long shares = 10, long oldShares = 1) =>
        new("split-a", "A", Day(effective), Day(price), shares, oldShares, Clock.Open(Day(effective)), "synthetic reviewed share terms");
    private static ShareInventoryCredit Credit(int index) =>
        new("split-a", Clock.Open(Day(index)), Clock.Open(Day(index)), "synthetic account inventory confirmation");
    private static Dataset Prices(params decimal[] prices) => new("synthetic unit accounting", true, false,
        prices.Select((price, i) => new Bar("A", "sector", Day(i), Clock.Close(Day(i)), price, price, price, price,
            100000, price * 100000, true, true)).ToArray());
    private static Dataset SplitData() => Prices(100, 101, 102, 110, 110, 110, 11, 11, 11, 11) with
    {
        Bars = Prices(100, 101, 102, 110, 110, 110, 11, 11, 11, 11).Bars.Select((b, i) => i is 4 or 5
            ? b with { Open = 0, High = 0, Low = 0, Volume = 0, TradingValue = 0, Tradable = false, CorporateAction = i == 4 }
            : b with { CorporateAction = i == 6 }).ToArray(),
        ShareUnitChanges = [Change()], ShareInventoryCredits = [Credit(7)]
    };
    private static RunResult Run(Dataset data, DateOnly? start = null, DateOnly? end = null, Risk? risk = null) =>
        new BacktestEngine().Run(data, [Momentum], start ?? data.Dates[0], end ?? data.Dates[^1], Zero, risk ?? Risk, initial: 10000);

    [Fact] public void SplitSeparatesEconomicPriceAndInventoryDatesWithoutCreatingProfit()
    {
        var data = SplitData(); var originalBars = JsonSerializer.Serialize(data.Bars);
        var run = Run(data); var adjustment = Assert.Single(run.ShareUnitAdjustments!);
        Assert.Equal(Clock.Open(Day(4)), adjustment.AppliedAt);
        Assert.Equal(9, adjustment.PreviousQuantity); Assert.Equal(90, adjustment.Quantity);
        Assert.Equal(55, adjustment.PreviousStopLoss); Assert.Equal(5.5m, adjustment.StopLoss);
        Assert.Equal(990, adjustment.RemainingPaid); Assert.Equal(ShareUnits.Hash(Change()), adjustment.ChangeHash);
        Assert.All(run.Equity, p => Assert.Equal(10000, p.Equity));
        Assert.DoesNotContain(run.Events, e => e.Contains("RISK_LIMIT") || e.Contains("REGIME_CHANGE"));
        Assert.Contains(run.Events, e => e.Contains("inventory credit unavailable"));
        var trade = Assert.Single(run.Trades);
        Assert.Equal(Clock.Open(Day(7)), trade.ExitTime); Assert.Equal(110, trade.EntryPrice); Assert.Equal(11, trade.ExitPrice);
        Assert.Equal(9, trade.OriginalEntryQuantity); Assert.Equal(90, trade.Quantity); Assert.Equal(990m, trade.AllocatedCost);
        Assert.Equal(0, trade.NetProfit); Assert.Equal(new[] { "split-a" }, trade.ShareUnitActionKeys);
        Assert.Equal(originalBars, JsonSerializer.Serialize(data.Bars));
    }

    [Fact] public void ExactConsolidationKeepsOriginalFillAndBasisButFractionalLotStops()
    {
        var data = Prices(100, 101, 102, 110, 330, 330, 330) with
        { ShareUnitChanges = [Change(4, 4, 1, 3)], ShareInventoryCredits = [Credit(4)] };
        var run = Run(data); var trade = Assert.Single(run.Trades);
        Assert.Equal(3, trade.Quantity); Assert.Equal(9, trade.OriginalEntryQuantity);
        Assert.Equal(110, trade.EntryPrice); Assert.Equal(330, trade.ExitPrice); Assert.Equal(990m, trade.AllocatedCost);
        Assert.Equal(165, Assert.Single(run.ShareUnitAdjustments!).StopLoss);
        Assert.All(run.Equity, p => Assert.Equal(10000, p.Equity));
        Assert.Throws<InvalidOperationException>(() => Run(data with { ShareUnitChanges = [Change(4, 4, 1, 2)] }));
        Assert.Throws<InvalidOperationException>(() => ShareUnits.Adjust(2, 100, Change(shares: int.MaxValue)));
    }

    [Fact] public void SplitStillAppliesAfterRiskHaltDuringSuspension()
    {
        var data = Prices(100, 101, 102, 110, 50, 50, 50, 5, 5) with
        { ShareUnitChanges = [Change(5, 7)], ShareInventoryCredits = [Credit(8)] };
        data = data with { Bars = data.Bars.Select((b, i) => i is >= 4 and <= 6
            ? b with { Open = 0, High = 0, Low = 0, Volume = 0, TradingValue = 0, Tradable = false } : b).ToArray() };
        var run = Run(data, risk: Risk with { DailyLoss = .02m });
        Assert.Contains(run.Events, e => e.StartsWith($"{Day(4):yyyy-MM-dd}:") && e.Contains("CLOSE_RISK_LIMIT"));
        Assert.Equal(Clock.Open(Day(5)), Assert.Single(run.ShareUnitAdjustments!).AppliedAt);
        Assert.Equal(run.Equity[4].Equity, run.Equity[5].Equity);
        var trade = Assert.Single(run.Trades); Assert.Equal("risk-halt", trade.Reason);
        Assert.Equal(90, trade.Quantity); Assert.Equal(-540, trade.NetProfit);
    }

    [Fact] public void NewLotAfterPriceTransitionDoesNotRequireAnEarlierHoldersCredit()
    {
        var data = SplitData() with { ShareInventoryCredits = null };
        data = data with { Bars = data.Bars.Select((b, i) => i >= 6
            ? b with { Open = 12, High = 12, Low = 12, Close = 12 } : b).ToArray() };
        var run = Run(data, start: Day(7)); var trade = Assert.Single(run.Trades);
        Assert.Equal(Clock.Open(Day(7)), trade.EntryTime); Assert.Equal(Clock.Open(Day(9)), trade.ExitTime);
        Assert.Null(trade.ShareUnitActionKeys); Assert.Null(run.ShareUnitAdjustments);
        Assert.DoesNotContain(run.Events, e => e.Contains("inventory credit"));
    }

    [Fact] public void ZeroOpeningPrintUsesPriorQuotesOwnUnitDateForCapitalCapacity()
    {
        var data = SplitData();
        var companion = data.Bars.Select((b, i) =>
        {
            var price = i < 5 ? 100 : i == 5 ? 102 : 110;
            return b with { Ticker = "B", Open = price, High = price, Low = price, Close = price,
                Volume = 100000, TradingValue = price * 100000, Tradable = true, CorporateAction = false };
        });
        data = data with { Bars = data.Bars.Select((b, i) => i == 6 ? b with
            { Open = 0, High = 0, Low = 0, Volume = 0, TradingValue = 0, Tradable = false } : b).Concat(companion).ToArray() };
        var run = Run(data);
        Assert.Contains(run.Trades, t => t.Ticker == "B" && t.EntryTime == Clock.Open(Day(6)));
        Assert.True(run.Equity.Single(p => p.Date == Day(6)).Exposure > .15m);
        Assert.DoesNotContain(run.Events, e => e.Contains("RISK_LIMIT"));
    }

    [Fact] public void CapacityTransformsPublishedVolumeAndFloorsOnlyAfterParticipation()
    {
        var change = Change(4, 6, 3, 2); var now = Clock.Open(Day(6));
        Assert.Equal(4.5m, ShareUnits.CapacityVolume("A", Day(3), Day(6), 3, now, [change]));
        Assert.Equal(4.5m, ShareUnits.CapacityVolume("A", Day(3), Day(4), 3, now, [change]));
        Assert.Equal(3, ShareUnits.CapacityVolume("A", Day(6), Day(6), 3, now, [change]));
        var data = SplitData() with { ShareInventoryCredits = [Credit(6)] };
        data = data with { Bars = data.Bars.Select((b, i) => i == 3 ? b with { Volume = 100 } : b).ToArray() };
        var run = Run(data); var firstExit = run.Trades[0];
        Assert.Equal(Clock.Open(Day(6)), firstExit.ExitTime); Assert.Equal(10, firstExit.Quantity);
        Assert.False(firstExit.CompletesPosition); Assert.Equal(110m, firstExit.AllocatedCost);
        Assert.Equal(990m, run.Trades.Sum(t => t.AllocatedCost!.Value));
    }

    [Fact] public void KnownSplitCannotCreateMomentumOrBearRegimeFromRawUnitJump()
    {
        var data = Prices(Enumerable.Range(0, 22).Select(i => i < 10 ? 300m : 100m).ToArray()) with
        { ShareUnitChanges = [Change(10, 10, 3)] };
        Assert.Equal("sideways", BacktestEngine.Regime(data, 21, Clock.Close(Day(21))));
        var zeroThreshold = new StrategySpec("momentum", 2, 0, 2);
        Assert.Null(ShareUnits.GenerateSignal(zeroThreshold, data.Bars.Take(12).ToArray(), Day(11), Clock.Close(Day(11)), data.ShareUnitChanges));
        Assert.Null(ShareUnits.GenerateSignal(zeroThreshold with { Family = "reversion" }, data.Bars.Take(12).ToArray(), Day(11), Clock.Close(Day(11)), data.ShareUnitChanges));
    }

    [Fact] public void SignalRetainsRawObservedPriceAndFutureActionsCannotChangeItsEvidence()
    {
        var raw = Prices(100, 101, 102).Bars;
        var change = Change(2, 4, 10); var now = Clock.Close(Day(2));
        var signal = ShareUnits.GenerateSignal(Momentum, raw, Day(2), now, [change])!;
        Assert.Equal(102, signal.Price); Assert.Equal(.02m, signal.Score);
        var future = Change(8, 8, 5) with { ActionKey = "future" };
        Assert.Equal(signal, ShareUnits.GenerateSignal(Momentum, raw, Day(2), now, [change, future]));
        Assert.Equal(new PriceStrategy(Momentum).Generate(raw, now), ShareUnits.GenerateSignal(Momentum, raw, Day(2), now, [future]));
    }

    [Fact] public void FutureUnitsAndCreditsDoNotChangeEarlierRun()
    {
        var data = Prices(100, 101, 102, 110, 111, 112, 113, 114, 115, 116);
        var changed = data with { ShareUnitChanges = [Change(8, 8)], ShareInventoryCredits = [Credit(9)] };
        var before = Run(data, end: Day(6)); var after = Run(changed, end: Day(6));
        Assert.Equal(before.Equity, after.Equity); Assert.Equal(before.Trades, after.Trades);
        Assert.Equal(before.Metrics, after.Metrics); Assert.Null(after.ShareUnitAdjustments);
    }

    [Fact] public void ValidationRejectsAmbiguousUnitsLateEvidenceAndUnsupportedFlags()
    {
        var data = SplitData(); data.Validate();
        Assert.Throws<ArgumentException>(() => (data with { ShareUnitChanges = [Change(), Change() with { ActionKey = "duplicate-date" }] }).Validate());
        Assert.Throws<ArgumentException>(() => (data with { ShareUnitChanges = [Change() with { AvailableAt = Clock.Close(Day(4)) }] }).Validate());
        Assert.Throws<ArgumentException>(() => (data with { ShareUnitChanges = [Change() with { PriceUnitDate = Day(3) }] }).Validate());
        Assert.Throws<ArgumentException>(() => (data with { ShareInventoryCredits = [Credit(7) with { AvailableAt = Clock.Open(Day(6)) }] }).Validate());
        Assert.Throws<ArgumentException>(() => (data with { ShareInventoryCredits = [Credit(7) with { ActionKey = "unknown" }] }).Validate());
        Assert.Throws<ArgumentException>(() => (data with { Bars = data.Bars.Select((b, i) => i == 4 ? b with { Tradable = true } : b).ToArray() }).Validate());
        Assert.Throws<ArgumentException>(() => (data with { Bars = data.Bars.Select((b, i) => i == 2 ? b with { CorporateAction = true } : b).ToArray() }).Validate());
        Assert.Throws<ArgumentException>(() => ShareUnits.Validate([Change()], null, lifecycle:
            [new("A", Day(5), "DELISTED", Clock.Open(Day(5)), "synthetic lifecycle") ]));
    }

    [Fact] public void MissingAccountCreditDoesNotProhibitLaterDelistingButHeldDisappearanceStops()
    {
        var data = SplitData();
        var companion = data.Bars.Select(b => b with { Ticker = "B", Open = 100, High = 100, Low = 100, Close = 100,
            Volume = 100000, TradingValue = 10000000, Tradable = true, CorporateAction = false });
        data = data with
        {
            Bars = data.Bars.Where(b => b.Date < Day(8)).Concat(companion).ToArray(), ShareInventoryCredits = null,
            LifecycleEvents = [new("A", Day(8), "DELISTED", Clock.Open(Day(8)), "synthetic delisting")]
        };
        data.Validate();
        Assert.Throws<InvalidOperationException>(() => Run(data));
        Assert.Empty(Run(data, start: Day(8)).Trades);
    }

    [Fact] public void NullExtensionsPreserveLegacyJsonAndUnitDataRoundTrips()
    {
        var legacy = Prices(100, 101, 102); var json = JsonSerializer.Serialize(legacy);
        Assert.DoesNotContain("ShareUnitChanges", json); Assert.DoesNotContain("ShareInventoryCredits", json);
        var data = SplitData(); var restored = JsonSerializer.Deserialize<Dataset>(JsonSerializer.Serialize(data))!;
        restored.Validate(); Assert.Equal(data.Hash, restored.Hash);
        Assert.Equal(Run(data).Equity, Run(restored).Equity);
        Assert.Equal(JsonSerializer.Serialize(Run(data).ShareUnitAdjustments), JsonSerializer.Serialize(Run(restored).ShareUnitAdjustments));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellingShareChangesPreserveExactGapAndIntradayStopBoundary(bool gap)
    {
        var split = Change(4, 4, 3); var consolidation = Change(5, 5, 1, 3) with { ActionKey = "consolidation-a" };
        var data = Prices(450, 460, 470, 500, 500m / 3m, gap ? 475 : 500) with
        {
            ShareUnitChanges = [split, consolidation],
            ShareInventoryCredits = [Credit(4), Credit(5) with { ActionKey = consolidation.ActionKey }]
        };
        if (!gap) data = data with { Bars = data.Bars.Select((b, i) => i == 5 ? b with { Low = 475 } : b).ToArray() };
        var run = new BacktestEngine().Run(data, [Momentum with { HoldDays = 10 }], Day(0), Day(5), Zero,
            Risk with { StopLoss = .05m }, initial: 10000);
        Assert.Equal(475, run.ShareUnitAdjustments![^1].StopLoss);
        var trade = Assert.Single(run.Trades);
        Assert.Equal(gap ? "gap-stop" : "stop", trade.Reason); Assert.Equal(475, trade.ExitPrice);
        Assert.Equal(500, trade.EntryPrice); Assert.Equal(2, trade.OriginalEntryQuantity); Assert.Equal(2, trade.Quantity);
        Assert.Equal(-50, trade.NetProfit); Assert.Equal(new[] { split.ActionKey, consolidation.ActionKey }, trade.ShareUnitActionKeys);
    }

    [Fact] public void AggregateRatiosCancelBeforePriceVolumeAndStopRounding()
    {
        var split = Change(4, 4, 3); var consolidation = Change(5, 5, 1, 3) with { ActionKey = "consolidation-a" };
        var changes = new[] { split, consolidation }; var now = Clock.Close(Day(5));
        Assert.Equal(475, ShareUnits.StopLoss(475, changes));
        Assert.Equal(1, ShareUnits.EconomicPrice(1, "A", Day(3), Day(5), now, changes));
        Assert.Equal(1, ShareUnits.PriceFactor("A", Day(3), Day(5), now, changes));
        Assert.Equal(1, ShareUnits.CapacityVolume("A", Day(3), Day(5), 1, now, changes));
        var inverseOrder = new[] { split with { NewShares = 1, OldShares = 3 }, consolidation with { NewShares = 3, OldShares = 1 } };
        Assert.Equal(1, decimal.Floor(ShareUnits.CapacityVolume("A", Day(3), Day(5), 1, now, inverseOrder)));
        var raw = Prices(1, 1, 1, 1, 1m / 3m, 1).Bars;
        Assert.Null(ShareUnits.GenerateSignal(Momentum with { Threshold = 0 }, raw, Day(5), now, changes));
        Assert.Null(ShareUnits.GenerateSignal(Momentum with { Family = "reversion", Threshold = 0 }, raw, Day(5), now, changes));
        Assert.Throws<InvalidOperationException>(() => ShareUnits.StopLoss(decimal.MaxValue, [Change(4, 4, 1, 2)]));
        Assert.Throws<InvalidOperationException>(() => ShareUnits.StopLoss(.0000000000000000000000000001m, [Change(4, 4, 3)]));
        Assert.Equal(decimal.MaxValue, ShareUnits.StopLoss(decimal.MaxValue, changes));
        Assert.Equal(decimal.MaxValue, ShareUnits.StopLoss(decimal.MaxValue, inverseOrder));
        var large = split with { NewShares = long.MaxValue - 1, OldShares = long.MaxValue };
        var inverseLarge = consolidation with { NewShares = large.OldShares, OldShares = large.NewShares };
        Assert.Equal(decimal.MaxValue, ShareUnits.StopLoss(decimal.MaxValue, [large, inverseLarge]));
        Assert.Throws<InvalidOperationException>(() => ShareUnits.StopLoss(.0000000000000000000000000002m, [Change(4, 4, 3)]));
    }
}
