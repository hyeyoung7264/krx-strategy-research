using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

// These are manual accounting fixtures, not verified market/broker observations or promotion evidence.
public sealed class ShareUnitPaperTests
{
    private static readonly DateOnly Day = new(2026, 9, 28);
    private static readonly DateTimeOffset Open = Clock.Open(Day);
    private static readonly StrategySpec Spec = new("momentum", 2, 0, 100);
    private const string ActionKey = "fixture-A-10-for-1";

    private static PaperState Held(int quantity = 10, long volume = 100_000)
    {
        var history = Enumerable.Range(0, 3).Select(i =>
        {
            var date = Day.AddDays(i - 5);
            return new Bar("A", "sector", date, Clock.Close(date), 100, 100, 100, 100,
                volume, volume * 100m, true, true);
        }).ToArray();
        var signal = new Signal(Spec.Id, "A", Clock.Close(Day.AddDays(-4)), 100, .1m,
            "manual accounting fixture", "fixture-signal-evidence");
        var entry = Clock.Open(Day.AddDays(-3));
        return new("share-units", ["diagnostic-only"], [Spec], new(0, 0, 0), new(),
            10_000, 10_000 - quantity * 100m, 10_000, 10_000, null, Open.AddMinutes(-1),
            "unknown", false, false, history, [],
            [new(Spec, signal, entry, 100, quantity, quantity * 100m, 95, "sector", OriginalEntryQuantity: quantity)],
            [], [], [], 0, CodeVersion: "fixture-source-v1", ResearchCandidateCount: 4);
    }

    private static ShareUnitChange Split(DateOnly? effective = null, DateOnly? priceUnit = null,
        long newShares = 10, long oldShares = 1) =>
        new(ActionKey, "A", effective ?? Day, priceUnit ?? Day.AddDays(2), newShares, oldShares,
            Open.AddDays(-2), "manual reviewed terms; not a publication-time attestation");

    private static Observation Event(string kind, DateTimeOffset time, decimal price, bool tradable = true) =>
        new("manual-accounting-fixture", kind, time,
            [new("A", "sector", time, price, price, tradable)], []);

    private static PaperState Step(PaperState before, Observation observation) =>
        PaperEngine.Step(before, observation, observation.ObservedAt);

    private static Observation Closing(DateOnly day, decimal price, bool tradable)
    {
        var time = Clock.Close(day);
        var bar = tradable
            ? new Bar("A", "sector", day, time, price, price, price, price, 100_000, price * 100_000, true, true)
            : new Bar("A", "sector", day, time, 0, 0, 0, price, 0, 0, false, true);
        return Event("close", time, price, tradable) with { ClosedBars = [bar] };
    }

    [Fact]
    public void DelayedRawPriceUnitChangePreservesEquityAndDoesNotApplyTheSplitTwice()
    {
        var before = Held();
        var change = Split();
        var first = Step(before, Event("open", Open, 100, false) with { ShareUnitChanges = [change] });
        var position = Assert.Single(first.Positions);
        Assert.Equal(100, position.Quantity);
        Assert.Equal(9.5m, position.StopLoss);
        Assert.Equal(100m, position.EntryPrice);
        Assert.Equal(before.Positions[0].Signal, position.Signal);
        Assert.Equal(1_000m, position.Paid);
        Assert.Equal(new[] { ActionKey }, position.ShareUnitActionKeys!);
        Assert.Equal(change, Assert.Single(first.ShareUnitChanges!));
        var adjustment = Assert.Single(first.ShareUnitAdjustments!);
        Assert.Equal(10, adjustment.PreviousQuantity);
        Assert.Equal(100, adjustment.Quantity);
        Assert.Equal(95m, adjustment.PreviousStopLoss);
        Assert.Equal(9.5m, adjustment.StopLoss);
        Assert.Equal(1_000m, adjustment.RemainingPaid);
        Assert.Equal(before.Positions[0].EntryTime, adjustment.EntryTime);

        var next = Step(first, Event("quote", Open.AddMinutes(1), 100, false));
        Assert.Single(next.ShareUnitAdjustments!);
        Assert.Equal(100, Assert.Single(next.Positions).Quantity);
        Assert.Throws<InvalidOperationException>(() => Step(first with
        { Positions = [first.Positions[0] with { OriginalStopLoss = null }] }, Event("quote", Open.AddMinutes(1), 100, false)));
        Assert.Throws<ArgumentException>(() => Step(first, first.Audit[^1].Observation));

        var closed = Step(next, Closing(Day, 100, false));
        Assert.Equal(10_000m, Assert.Single(closed.Equity).Equity);
        var secondOpen = Step(closed, Event("open", Clock.Open(Day.AddDays(1)), 100, false));
        var secondClose = Step(secondOpen, Closing(Day.AddDays(1), 100, false));
        Assert.Equal(10_000m, secondClose.Equity[^1].Equity);
        var resumed = Step(secondClose, Event("open", Clock.Open(Day.AddDays(2)), 10));
        var resumedClose = Step(resumed, Closing(Day.AddDays(2), 10, true));

        Assert.False(resumedClose.Halted);
        Assert.Equal(10_000m, resumedClose.Peak);
        Assert.All(resumedClose.Equity, point => Assert.Equal(10_000m, point.Equity));
        Assert.All(resumedClose.Equity, point => Assert.Equal(0m, point.DailyReturn));
        Assert.Equal(before.Cash, resumedClose.Cash);
        Assert.Equal(0m, resumedClose.Turnover);
        Assert.Empty(resumedClose.Fills);
        Assert.Single(resumedClose.ShareUnitAdjustments!);
    }

    [Fact]
    public void MarketResumeDoesNotReleaseConvertedInventoryButObservedCreditDoes()
    {
        var first = Step(Held() with { Halted = true },
            Event("open", Open, 100, false) with { ShareUnitChanges = [Split()] });
        var resume = Clock.Open(Day.AddDays(2));
        var uncredited = Step(first, Event("open", resume, 10));
        Assert.Empty(uncredited.Fills);
        Assert.Equal(100, Assert.Single(uncredited.Positions).Quantity);
        Assert.Equal(9_000m, uncredited.Cash);

        var creditedAt = resume.AddMinutes(1);
        var receivedAt = resume.AddMinutes(2);
        var stillUncredited = Step(uncredited, Event("quote", creditedAt, 10));
        Assert.Empty(stillUncredited.Fills);
        var credit = new ShareInventoryCredit(ActionKey, creditedAt, receivedAt,
            "manual account-specific credit observation, no execution authority");
        var released = Step(stillUncredited, Event("quote", receivedAt, 10) with { ShareInventoryCredits = [credit] });

        Assert.Empty(released.Positions);
        var fill = Assert.Single(released.Fills).Trade;
        Assert.Equal(100, fill.Quantity);
        Assert.Equal(100m, fill.EntryPrice);
        Assert.Equal(10m, fill.ExitPrice);
        Assert.Equal(0m, fill.NetProfit);
        Assert.Equal(0m, fill.Return);
        Assert.Equal(new[] { ActionKey }, fill.ShareUnitActionKeys!);
        Assert.Equal(10, fill.OriginalEntryQuantity);
        Assert.Equal(1_000m, fill.AllocatedCost);
        Assert.Equal(10_000m, released.Cash);
        Assert.Equal(1_000m, released.Turnover);
        Assert.Equal(credit, Assert.Single(released.ShareInventoryCredits!));
    }

    [Fact]
    public void CreditWithoutAnExitCreatesNeitherCashNorTurnover()
    {
        var first = Step(Held(), Event("open", Open, 100, false) with { ShareUnitChanges = [Split()] });
        var resume = Clock.Open(Day.AddDays(2));
        var uncredited = Step(first, Event("open", resume, 10));
        var time = resume.AddMinutes(1);
        var credit = new ShareInventoryCredit(ActionKey, time, time, "manual credit observation");
        var credited = Step(uncredited, Event("quote", time, 10) with { ShareInventoryCredits = [credit] });
        Assert.Equal(uncredited.Cash, credited.Cash);
        Assert.Equal(uncredited.Turnover, credited.Turnover);
        Assert.Equal(uncredited.Positions[0], Assert.Single(credited.Positions));
        Assert.Empty(credited.Fills);
        Assert.Single(credited.ShareUnitAdjustments!);
    }

    [Fact]
    public void ThreeForOneRetainedQuoteDoesNotRoundAFactorBeforePricing()
    {
        var before = Held();
        var old = before.Positions[0];
        before = before with
        {
            InitialCapital = 3_000, Cash = 0, Peak = 3_000, DayStartEquity = 3_000,
            Positions = [old with { EntryPrice = 300, Paid = 3_000, StopLoss = 285,
                Signal = old.Signal with { Price = 300 } }],
            History = before.History.Select(bar => bar with
            {
                Open = 300, High = 300, Low = 300, Close = 300, TradingValue = bar.Volume * 300m
            }).ToArray()
        };
        var change = Split(newShares: 3) with { ActionKey = "fixture-A-3-for-1" };
        var opened = Step(before, Event("open", Open, 300, false) with { ShareUnitChanges = [change] });
        var closed = Step(opened, Closing(Day, 300, false));
        Assert.Equal(30, Assert.Single(closed.Positions).Quantity);
        Assert.Equal(95m, closed.Positions[0].StopLoss);
        Assert.Equal(3_000m, Assert.Single(closed.Equity).Equity);
        Assert.Equal(0m, closed.Equity[0].DailyReturn);
        Assert.Equal(3_000m, closed.Positions[0].Paid);
        Assert.False(closed.Halted);
    }

    [Fact]
    public void SplitThenConsolidationRestoresExactStopBoundaryAndOriginalCost()
    {
        var before = Held(quantity: 3);
        var original = before.Positions[0];
        before = before with
        {
            Cash = 8_500,
            Positions = [original with { EntryPrice = 500, Paid = 1_500, StopLoss = 475,
                Signal = original.Signal with { Price = 500 } }],
            History = before.History.Select(bar => bar with
            {
                Open = 500, High = 500, Low = 500, Close = 500, TradingValue = bar.Volume * 500m
            }).ToArray()
        };
        var split = Split(Day, Day, newShares: 3) with { ActionKey = "fixture-A-three-for-one" };
        var consolidation = Split(Day.AddDays(1), Day.AddDays(1), newShares: 1, oldShares: 3) with
        { ActionKey = "fixture-A-one-for-three" };
        var splitCredit = new ShareInventoryCredit(split.ActionKey, Open, Open, "manual split inventory evidence");
        var divided = Step(before, Event("open", Open, 170) with
        { ShareUnitChanges = [split, consolidation], ShareInventoryCredits = [splitCredit] });
        Assert.Equal(9, Assert.Single(divided.Positions).Quantity);
        Assert.Equal(1_500m, divided.Positions[0].Paid);
        Assert.Empty(divided.Fills); Assert.Equal(0m, divided.Turnover);
        var closed = Step(divided, Closing(Day, 170, true));

        var nextOpen = Clock.Open(Day.AddDays(1));
        var consolidationCredit = new ShareInventoryCredit(consolidation.ActionKey, nextOpen, nextOpen,
            "manual consolidation inventory evidence");
        var restored = Step(closed, Event("open", nextOpen, 500) with { ShareInventoryCredits = [consolidationCredit] });
        var position = Assert.Single(restored.Positions);
        Assert.Equal(3, position.Quantity);
        Assert.Equal(475m, position.StopLoss); // 475 / 3 * 3 must not drift below the original stop.
        Assert.Equal(500m, position.EntryPrice); Assert.Equal(3, position.OriginalEntryQuantity);
        Assert.Equal(1_500m, position.Paid); Assert.Empty(restored.Fills); Assert.Equal(0m, restored.Turnover);
        Assert.Equal(2, restored.ShareUnitAdjustments!.Length);
        Assert.All(restored.ShareUnitAdjustments!, adjustment => Assert.Equal(1_500m, adjustment.RemainingPaid));

        // Use a quote after the open so the assertion isolates the exact stop boundary,
        // without allowing an opening entry from yesterday's pending signal.
        var stopped = Step(restored, Event("quote", nextOpen.AddMinutes(1), 475));
        Assert.Empty(stopped.Positions); Assert.False(stopped.Halted);
        var fill = Assert.Single(stopped.Fills).Trade;
        Assert.Equal("stop", fill.Reason); Assert.True(fill.CompletesPosition);
        Assert.Equal(475m, fill.ExitPrice); Assert.Equal(500m, fill.EntryPrice); Assert.Equal(500m, fill.SignalPrice);
        Assert.Equal(3, fill.Quantity); Assert.Equal(3, fill.OriginalEntryQuantity);
        Assert.Equal(1_500m, fill.AllocatedCost); Assert.Equal(-75m, fill.NetProfit); Assert.Equal(-.05m, fill.Return);
        Assert.Equal(new[] { split.ActionKey, consolidation.ActionKey }, fill.ShareUnitActionKeys);
        Assert.Equal(9_925m, stopped.Cash); Assert.Equal(1_425m, stopped.Turnover);
    }

    [Fact]
    public void PartialExitsAcrossDifferentUnitsPreserveOriginalFillAndTotalCost()
    {
        var change = Split(Day.AddDays(1), Day.AddDays(1));
        var beforeSplit = Step(Held(volume: 200), Event("open", Open, 90) with { ShareUnitChanges = [change] });
        var oldFill = Assert.Single(beforeSplit.Fills).Trade;
        Assert.Equal(2, oldFill.Quantity);
        Assert.Equal(-20m, oldFill.NetProfit);
        Assert.False(oldFill.CompletesPosition);
        Assert.Equal(8, Assert.Single(beforeSplit.Positions).Quantity);
        Assert.Equal(800m, beforeSplit.Positions[0].Paid);

        var effective = Clock.Open(Day.AddDays(1));
        var credit = new ShareInventoryCredit(ActionKey, effective, effective, "manual credit observation");
        var afterSplit = Step(beforeSplit, Event("open", effective, 9) with { ShareInventoryCredits = [credit] });
        Assert.Equal(2, afterSplit.Fills.Length);
        var newFill = afterSplit.Fills[^1].Trade;
        Assert.Equal(20, newFill.Quantity);
        Assert.Equal(-20m, newFill.NetProfit);
        Assert.Equal(-.1m, newFill.Return);
        Assert.Equal(100m, newFill.EntryPrice);
        Assert.Equal(100m, newFill.SignalPrice);
        Assert.Equal(oldFill.EntryTime, newFill.EntryTime);
        Assert.Equal(new[] { ActionKey }, newFill.ShareUnitActionKeys!);
        Assert.Equal(10, newFill.OriginalEntryQuantity);
        Assert.Equal(200m, newFill.AllocatedCost);
        Assert.Equal(60, Assert.Single(afterSplit.Positions).Quantity);
        Assert.Equal(600m, afterSplit.Positions[0].Paid);
        Assert.Equal(9.5m, afterSplit.Positions[0].StopLoss);

        var noExtraLiquidity = Step(afterSplit, Event("quote", effective.AddMinutes(1), 9));
        Assert.Equal(2, noExtraLiquidity.Fills.Length);
        var completed = noExtraLiquidity;
        for (var i = 2; i <= 4; i++)
            completed = Step(completed, Event("open", Clock.Open(Day.AddDays(i)), 9));

        Assert.Empty(completed.Positions);
        Assert.Equal(5, completed.Fills.Length);
        Assert.Single(completed.Fills, fill => fill.Trade.CompletesPosition);
        Assert.All(completed.Fills, fill => Assert.Equal(100m, fill.Trade.EntryPrice));
        Assert.All(completed.Fills, fill => Assert.Equal(10, fill.Trade.OriginalEntryQuantity));
        Assert.Equal(1_000m, completed.Fills.Sum(fill => fill.Trade.AllocatedCost));
        Assert.Equal(-100m, completed.Fills.Sum(fill => fill.Trade.NetProfit));
        Assert.Equal(9_900m, completed.Cash);
        Assert.Equal(900m, completed.Turnover);
        var metrics = Statistics.Measure([], completed.Fills.Select(fill => fill.Trade).ToArray(), 10_000, completed.Turnover, 0);
        Assert.Equal(1, metrics.NumberOfTrades);
        Assert.Equal(-100m, metrics.ExpectedValuePerTrade);
        Assert.Single(completed.ShareUnitAdjustments!);
    }

    [Fact]
    public void FractionalReverseSplitRequiresSeparateTreatmentInsteadOfRounding()
    {
        var before = Held(quantity: 3);
        var observation = Event("open", Open, 100, false) with
        {
            ShareUnitChanges = [Split(newShares: 1, oldShares: 2)]
        };
        Assert.Throws<InvalidOperationException>(() => Step(before, observation));
        Assert.Equal(3, before.Positions[0].Quantity);
        Assert.Equal(300m, before.Positions[0].Paid);
        Assert.Equal(9_700m, before.Cash);
    }

    [Fact]
    public void RepeatedActionKeyAndLateEffectiveActionCannotRewritePaperHistory()
    {
        var change = Split();
        var before = Held();
        Assert.ThrowsAny<ArgumentException>(() => Step(before,
            Event("open", Open, 100, false) with { ShareUnitChanges = [change, change] }));

        var applied = Step(before, Event("open", Open, 100, false) with { ShareUnitChanges = [change] });
        Assert.ThrowsAny<ArgumentException>(() => Step(applied,
            Event("quote", Open.AddMinutes(1), 100, false) with { ShareUnitChanges = [change] }));

        var alreadyObserved = Step(before, Event("open", Open, 100, false));
        Assert.ThrowsAny<ArgumentException>(() => Step(alreadyObserved,
            Event("quote", Open.AddMinutes(1), 100, false) with { ShareUnitChanges = [change] }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FutureCreditOrFutureAvailabilityCannotUnlockInventory(bool futureCredit)
    {
        var first = Step(Held() with { Halted = true }, Event("open", Open, 100, false) with { ShareUnitChanges = [Split()] });
        var time = Clock.Open(Day.AddDays(2));
        var credit = new ShareInventoryCredit(ActionKey,
            futureCredit ? time.AddMinutes(1) : time,
            futureCredit ? time : time.AddMinutes(1), "manual invalid future observation");
        Assert.ThrowsAny<ArgumentException>(() => Step(first,
            Event("open", time, 10) with { ShareInventoryCredits = [credit] }));
    }

    [Fact]
    public void LotBoughtInNewUnitsDoesNotInheritAnOlderLotsInventoryRestriction()
    {
        var change = Split(Day, Day);
        var before = Held();
        var signal = before.Positions[0].Signal with { Price = 10, Time = Open.AddSeconds(-1) };
        before = before with
        {
            Cash = 9_900, TradingDate = Day, LastObservation = Open,
            ShareUnitChanges = [change],
            Positions = [new(Spec, signal, Open, 10, 10, 100, 9.5m, "sector", OriginalEntryQuantity: 10)]
        };
        var after = Step(before, Event("quote", Open.AddMinutes(1), 9));
        Assert.Empty(after.Positions);
        var trade = Assert.Single(after.Fills).Trade;
        Assert.Equal(10, trade.Quantity);
        Assert.Equal(10m, trade.EntryPrice);
        Assert.Equal(-10m, trade.NetProfit);
        Assert.True(trade.ShareUnitActionKeys == null || trade.ShareUnitActionKeys.Length == 0);
        Assert.True(after.ShareUnitAdjustments == null || after.ShareUnitAdjustments.Length == 0);
        Assert.Equal(9_990m, after.Cash);
    }

    [Fact]
    public void JournalReplayAndJsonPreserveActionCreditAndAdjustmentEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "share-unit-paper-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = Held();
            var seed = fixture with { Cash = fixture.InitialCapital, Positions = [], PendingSignals = [fixture.Positions[0].Signal] };
            var journal = new PaperJournal(directory);
            var change = Split(Day.AddDays(1), Day.AddDays(2));
            var forged = new ShareUnitAdjustment(ActionKey, "A", Clock.Open(Day.AddDays(1)), Spec.Id,
                Open, 10, 100, 95, 9.5m, 1_000, ShareUnits.Hash(change));
            Assert.Throws<ArgumentException>(() => journal.Start(seed with { ShareUnitAdjustments = [forged] }));
            Assert.False(Directory.Exists(directory));
            var genesis = journal.Start(seed);
            var bought = journal.Commit(genesis, Event("open", Open, 100) with { ShareUnitChanges = [change] }, Open, seed.CodeVersion);
            Assert.Equal(10, Assert.Single(bought.Positions).Quantity);

            var effective = Clock.Open(Day.AddDays(1));
            var adjusted = journal.Commit(bought, Event("open", effective, 100, false), effective, seed.CodeVersion);
            var json = JsonSerializer.Serialize(adjusted);
            var restored = JsonSerializer.Deserialize<PaperState>(json)!;
            Assert.Equal(json, JsonSerializer.Serialize(restored));
            Assert.Single(restored.ShareUnitAdjustments!);
            Assert.Equal(change, Assert.Single(restored.ShareUnitChanges!));
            Assert.Equal(JsonSerializer.Serialize(adjusted), JsonSerializer.Serialize(journal.Recover(bought)));

            var resume = Clock.Open(Day.AddDays(2));
            var resumed = journal.Commit(restored, Event("open", resume, 10), resume, seed.CodeVersion);
            var time = resume.AddMinutes(1);
            var credit = new ShareInventoryCredit(ActionKey, time, time, "manual credit evidence");
            var credited = journal.Commit(resumed,
                Event("quote", time, 10) with { ShareInventoryCredits = [credit] }, time, seed.CodeVersion);
            Assert.Single(credited.ShareInventoryCredits!);
            Assert.Single(credited.ShareUnitAdjustments!);
            Assert.Equal(100, credited.Positions[0].Quantity);
            Assert.Equal(100m, credited.Positions[0].EntryPrice);
            var review = journal.Evaluate(credited, seed.CodeVersion);
            Assert.Contains(review.Reasons, reason => reason.Contains("UNVERIFIED_FEED", StringComparison.Ordinal));
            Assert.Equal("HOLD_OR_DISABLE", review.Decision);

            var altered = credited with { ShareUnitAdjustments = [] };
            Assert.Throws<ArgumentException>(() => journal.Evaluate(altered, seed.CodeVersion));
        }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath());
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("share-unit-paper-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
