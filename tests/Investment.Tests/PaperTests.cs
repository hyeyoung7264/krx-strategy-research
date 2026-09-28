using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class PaperTests
{
    private static readonly DateOnly Day = new(2026, 9, 28);
    private static readonly DateTimeOffset Open = Clock.Open(Day);
    private static readonly StrategySpec Spec = new("momentum", 2, 0, 2);
    private static PaperState Empty()
    {
        var history = new[] { 100m, 101m, 102m }.Select((p, i) => new Bar("A", "s", Day.AddDays(i - 3), Clock.Close(Day.AddDays(i - 3)), p, p * 1.01m, p * .99m, p, 100000, p * 100000, true, true)).ToArray();
        var signal = new PriceStrategy(Spec).Generate(history, history[^1].AvailableAt)!;
        return new("test", ["research"], [Spec], new(0, 0, 0), new(.1m, .4m, .8m, .3m, .02m, .10m, .05m, .01m),
            10000, 10000, 10000, 10000, null, Open.AddMinutes(-1), "unknown", false, false, history, [signal], [], [], [], [], 0);
    }
    private static Observation Event(string kind, DateTimeOffset time, decimal bid, bool tradable = true) => new("manual-fixture", kind, time, [new("A", "s", time, bid, bid, tradable)], []);
    [Fact] public void HistoricalReplayAndFutureQuotesAreRejected()
    {
        var state = Empty();
        Assert.Throws<ArgumentException>(() => PaperEngine.Step(state, Event("open", Open, 110), Open.AddHours(1)));
        Assert.Throws<ArgumentException>(() => PaperEngine.Step(state, Event("open", Open.AddSeconds(1), 110), Open));
        var e = Event("open", Open, 110) with { Quotes = [new("A", "s", Open.AddSeconds(1), 110, 110, true)] };
        Assert.Throws<ArgumentException>(() => PaperEngine.Step(state, e, Open));
    }
    [Fact] public void PaperRecordsActualSignalAndFillContext()
    {
        var state = PaperEngine.Step(Empty(), Event("open", Open, 110), Open);
        var pos = Assert.Single(state.Positions); Assert.Equal(9, pos.Quantity); Assert.True(pos.Signal.Time < pos.EntryTime);
        Assert.Equal(104.5m, pos.StopLoss); Assert.False(state.VerifiedFeed);
        Assert.Single(state.Audit); Assert.Equal("GENESIS", state.Audit[0].PreviousHash);
    }
    [Fact] public void PaperStopLossAndHaltedLiquidationRespectSuspensions()
    {
        var opened = PaperEngine.Step(Empty(), Event("open", Open, 110), Open);
        var halted = PaperEngine.Step(opened, Event("quote", Open.AddMinutes(1), 70, false), Open.AddMinutes(1));
        Assert.True(halted.Halted); Assert.Single(halted.Positions); Assert.Empty(halted.Fills);
        var exited = PaperEngine.Step(halted, Event("quote", Open.AddMinutes(2), 70), Open.AddMinutes(2));
        Assert.Empty(exited.Positions); var fill = Assert.Single(exited.Fills);
        Assert.Equal(-360, fill.Trade.NetProfit); Assert.Equal("risk-halt", fill.Trade.Reason);
        Assert.Equal(halted.Audit[^1].Hash, exited.Audit[^1].PreviousHash);
    }
    [Fact] public void RepeatedOpenAndMissingQuotesAreRejected()
    {
        var opened = PaperEngine.Step(Empty(), Event("open", Open, 110), Open);
        Assert.Throws<ArgumentException>(() => PaperEngine.Step(opened, Event("open", Open.AddSeconds(1), 110), Open.AddSeconds(1)));
        var e = Event("quote", Open.AddSeconds(1), 110) with { Quotes = [new("B", "s", Open.AddSeconds(1), 100, 100, true)] };
        Assert.Throws<ArgumentException>(() => PaperEngine.Step(opened, e, Open.AddSeconds(1)));
    }
    [Fact] public void PaperPartialExitSharesDailyLiquidityBudgetAcrossObservations()
    {
        var state = Empty(); var signal = Assert.Single(state.PendingSignals);
        state = state with { Cash = 9010, PendingSignals = [], History = state.History.Select(b => b with { Volume = 200 }).ToArray(),
            Positions = [new(Spec, signal, Open.AddDays(-1), 110, 9, 990, 104.5m, "s")] };
        var first = PaperEngine.Step(state, Event("open", Open, 70), Open);
        Assert.True(first.Halted); Assert.Equal(7, Assert.Single(first.Positions).Quantity);
        Assert.Equal(2, Assert.Single(first.Fills).Trade.Quantity); Assert.False(first.Fills[0].Trade.CompletesPosition);
        var second = PaperEngine.Step(first, Event("quote", Open.AddMinutes(1), 70), Open.AddMinutes(1));
        Assert.Single(second.Fills); Assert.Equal(7, Assert.Single(second.Positions).Quantity);
        Assert.Contains(second.Audit[^1].Actions, a => a.StartsWith("UNFILLED_EXIT", StringComparison.Ordinal));
        var evaluated = second with { Equity = Enumerable.Range(0, 30).Select(i => new EquityPoint(Day.AddDays(i), 10000 + i * 100, .01m, 0)).ToArray() };
        Assert.Contains(PaperEngine.Evaluate(evaluated, 1, minimumSessions: 30, minimumTrades: 1).Reasons, r => r.Contains("INSUFFICIENT_FORWARD_PAPER_EVIDENCE"));
    }
    [Fact] public void ManualFeedCannotApproveStrategyEvenIfCallerClaimsVerified()
    {
        var state = PaperEngine.Step(Empty(), Event("open", Open, 110) with { VerifiedFeed = true }, Open);
        Assert.False(state.VerifiedFeed);
        var review = PaperEngine.Evaluate(state, 4);
        Assert.Equal("HOLD_OR_DISABLE", review.Decision); Assert.Contains(review.Reasons, r => r.Contains("UNVERIFIED_FEED"));
    }
    [Fact] public void SyntheticResearchCannotStartForwardPaper()
    {
        Assert.Throws<ArgumentException>(() => PaperEngine.Start([], DataFiles.Demo(30), new(), new(), Open, new SourceSnapshot("test-version", "fixture", [])));
    }
    [Fact] public void BacktestEvidenceDoesNotSkipVersionOrStateGate()
    {
        var data = DataFiles.Demo(30);
        var run = new BacktestEngine().Run(data, [Spec], data.Dates[0], data.Dates[^1], new(), new());
        var version = new StrategyVersion(Spec, StrategyStatus.EXPERIMENTAL, []);
        var promotion = PromotionGate.Backtested(version, run, Open);
        Assert.Equal(StrategyStatus.BACKTESTED, promotion.After.Status); Assert.Contains(run.Id, promotion.After.EvidenceIds);
        Assert.Throws<ArgumentException>(() => PromotionGate.Backtested(version with { Spec = Spec with { Version = 2 } }, run, Open));
        Assert.Throws<ArgumentException>(() => PromotionGate.Backtested(promotion.After, run, Open));
    }
    [Fact] public void CloseRequiresCompleteDataAndUpdatesDailyEquity()
    {
        var opened = PaperEngine.Step(Empty(), Event("open", Open, 110), Open);
        var time = Clock.Close(Day);
        var e = Event("close", time, 111) with { ClosedBars = [new("A", "s", Day, time, 110, 112, 109, 111, 100000, 11100000, true, true)] };
        var closed = PaperEngine.Step(opened, e, time);
        Assert.Single(closed.Equity); Assert.Equal(10009, closed.Equity[0].Equity);
        Assert.Throws<ArgumentException>(() => PaperEngine.Step(opened, e with { ClosedBars = [] }, time));
    }
    [Fact] public void PaperCloseAcceptsObservedNewListingAndCarriesItsHistory()
    {
        var opened = PaperEngine.Step(Empty() with { PendingSignals = [] }, Event("open", Open, 110), Open);
        var time = Clock.Close(Day);
        var first = new Bar("A", "s", Day, time, 110, 112, 109, 111, 100000, 11100000, true, true);
        var listed = new Bar("B", "s", Day, time, 100, 101, 99, 100, 100000, 10000000, true, true);
        var close = Event("close", time, 111) with { ClosedBars = [first, listed],
            LifecycleEvents = [new("B", Day, "LISTED", time, "observed-listing-notice")] };
        Assert.Throws<ArgumentException>(() => PaperEngine.Step(opened, close with { LifecycleEvents = null }, time));
        var after = PaperEngine.Step(opened, close, time);
        Assert.Equal(2, after.History.Count(b => b.Date == Day));
        Assert.Single(after.LifecycleEvents!);
        Assert.DoesNotContain(after.PendingSignals, s => s.Ticker == "B");
    }
    [Fact] public void PaperCannotEraseAHeldSecurityOnDelisting()
    {
        var opened = PaperEngine.Step(Empty(), Event("open", Open, 110), Open);
        var time = Clock.Close(Day);
        var close = Event("close", time, 110) with { ClosedBars = [new("B", "s", Day, time, 100, 101, 99, 100, 100000, 10000000, true, true)],
            LifecycleEvents = [new("A", Day, "DELISTED", time, "notice"), new("B", Day, "LISTED", time, "notice")] };
        Assert.Throws<InvalidOperationException>(() => PaperEngine.Step(opened, close, time));
    }
    [Fact] public void PaperJournalRecoversCommittedTransitionWithoutExecutingTwice()
    {
        var root = Path.Combine(Path.GetTempPath(), "paper-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new PaperJournal(root); var before = journal.Start(Empty() with { CodeVersion = "source-v1", ResearchCandidateCount = 4 });
            Assert.Throws<ArgumentException>(() => journal.Commit(before with { Cash = 20000 }, Event("open", Open, 110), Open, "source-v1"));
            Assert.Throws<ArgumentException>(() => journal.Commit(before, Event("open", Open, 110), Open, "source-v2"));
            var after = journal.Commit(before, Event("open", Open, 110), Open, "source-v1");
            var recovered = journal.Recover(before);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(after), System.Text.Json.JsonSerializer.Serialize(recovered));
            Assert.Throws<InvalidOperationException>(() => journal.Commit(before, Event("open", Open.AddSeconds(1), 111), Open.AddSeconds(1), "source-v1"));
            Assert.Equal(2, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact] public void StandaloneProfitableStateCannotBecomeReviewEvidenceByAssertingFeedFlag()
    {
        var state = Empty(); var signal = state.PendingSignals[0];
        var fills = Enumerable.Range(0, 60).Select(i => new PaperFill(new Trade(Spec.Id, "A", signal.Time, 102,
            Open.AddMinutes(i), 110, Open.AddMinutes(i + 1), 120, 1, 10, .09m, "fixture", signal.EvidenceHash), 104.5m, null, null, "unknown", "asserted-feed")).ToArray();
        state = state with { VerifiedFeed = true, Fills = fills,
            Equity = Enumerable.Range(0, 120).Select(i => new EquityPoint(Day.AddDays(i), 10000 + 100 * (i + 1), .01m, 0)).ToArray() };
        var evaluation = PaperEngine.Evaluate(state, 1);
        Assert.Equal("HOLD_OR_DISABLE", evaluation.Decision);
        Assert.Single(evaluation.Reasons); Assert.Contains("UNCOMMITTED_PAPER_EVIDENCE", evaluation.Reasons[0]);
    }
    [Fact] public void PaperJournalEvaluationReplaysTheLatestLedgerAndRejectsEditedOrOlderExports()
    {
        var root = Path.Combine(Path.GetTempPath(), "paper-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new PaperJournal(root);
            var genesis = journal.Start(Empty() with { CodeVersion = "source-v1", ResearchCandidateCount = 4 });
            var opened = journal.Commit(genesis, Event("open", Open, 110), Open, "source-v1");
            var evaluation = journal.Evaluate(opened, "source-v1");
            Assert.Contains(evaluation.Reasons, r => r.Contains("UNVERIFIED_FEED"));
            Assert.DoesNotContain(evaluation.Reasons, r => r.Contains("UNCOMMITTED_PAPER_EVIDENCE"));
            Assert.Equal(4, opened.ResearchCandidateCount);
            Assert.Throws<ArgumentException>(() => journal.Evaluate(opened with { Cash = opened.Cash + 1000 }, "source-v1"));
            Assert.Throws<ArgumentException>(() => journal.Evaluate(opened with { VerifiedFeed = true }, "source-v1"));
            Assert.Throws<ArgumentException>(() => journal.Evaluate(opened with { ResearchCandidateCount = 1 }, "source-v1"));
            Assert.Throws<ArgumentException>(() => journal.Evaluate(opened, "source-v2"));
            var halted = journal.Commit(opened, Event("quote", Open.AddMinutes(1), 70, false), Open.AddMinutes(1), "source-v1");
            Assert.True(halted.Halted);
            Assert.Throws<ArgumentException>(() => journal.Evaluate(opened, "source-v1"));
            Assert.Contains("RISK_OR_REGIME_HALT", journal.Evaluate(halted, "source-v1").Reasons);
            Assert.Equal(3, Directory.GetFiles(root).Length); // Review does not publish a new transition.
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Theory]
    [InlineData("cash")]
    [InlineData("feed")]
    [InlineData("hash")]
    [InlineData("candidate-count")]
    public void ChangedCommittedLedgerIsRejectedByRecomputedAccounting(string field)
    {
        var root = Path.Combine(Path.GetTempPath(), "paper-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new PaperJournal(root);
            var genesis = journal.Start(Empty() with { CodeVersion = "source-v1", ResearchCandidateCount = 4 });
            var opened = journal.Commit(genesis, Event("open", Open, 110), Open, "source-v1");
            var changed = field switch
            {
                "cash" => opened with { Cash = opened.Cash + 1000 },
                "feed" => opened with { VerifiedFeed = true },
                "hash" => opened with { Audit = [opened.Audit[0] with { Hash = "changed" }] },
                _ => opened with { ResearchCandidateCount = 2 }
            };
            File.WriteAllText(Path.Combine(root, "state-test-1.json"), System.Text.Json.JsonSerializer.Serialize(changed));
            Assert.Throws<ArgumentException>(() => journal.Evaluate(changed, "source-v1"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact] public void PaperJournalRejectsGenesisWithFabricatedPerformanceOrUnfrozenCandidateCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "paper-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new PaperJournal(root); var state = Empty() with { CodeVersion = "source-v1", ResearchCandidateCount = 4 };
            Assert.Throws<ArgumentException>(() => journal.Start(state with { Equity = [new(Day, 20000, 1, 0)] }));
            Assert.Throws<ArgumentException>(() => journal.Start(state with { Cash = 20000 }));
            Assert.Throws<ArgumentException>(() => journal.Start(state with { ResearchCandidateCount = 0 }));
            Assert.False(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
