using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class SessionHoursTests
{
    private static readonly DateOnly[] Days = [new(2024, 1, 2), new(2024, 1, 3), new(2024, 1, 4),
        new(2024, 1, 5), new(2024, 1, 8), new(2024, 1, 9), new(2024, 1, 10)];
    private static readonly DateOnly PaperDay = new(2024, 1, 8);
    private static readonly StrategySpec Momentum = new("momentum", 2, 0, 2);
    private static readonly Costs Zero = new(0, 0, 0);
    private static readonly Risk Loose = new(.1m, .4m, .8m, .3m, .5m, .5m, .5m, .01m);
    private static readonly ResearchPlan Plan = new(60, 30, 30, 30, 1, 30);
    private static DateTimeOffset At(DateOnly date, int hour, int minute = 0) =>
        new(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(9));
    private static SessionHours Hours(DateOnly date, bool delayed = false) => new(date,
        At(date, delayed ? 10 : 9), At(date, delayed ? 16 : 15, 30), At(date.AddDays(-1), 8),
        "SYNTHETIC SESSION FIXTURE, NOT EXCHANGE EVIDENCE");
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static Dataset Prices()
    {
        decimal[] prices = [100, 101, 102, 110, 111, 112, 113];
        var hours = Days.Select(d => Hours(d, d == Days[3])).ToArray();
        var bars = prices.Select((p, i) => new Bar("A", "sector", Days[i], hours[i].CloseAt,
            p, p * 1.01m, p * .99m, p, 100_000, p * 100_000, true, true)).ToArray();
        return new("SYNTHETIC SESSION PRICES", true, false, bars, Days, SessionHours: hours);
    }
    private static RunResult Run(Dataset data, StrategySpec? spec = null) => new BacktestEngine().Run(data,
        [spec ?? Momentum], data.Dates[0], data.Dates[^1], Zero, Loose, initial: 10_000);

    [Fact]
    public void NullSchedulesKeepLegacyJsonAndFallbackOnlyWhenExplicitEvidenceIsNotRequired()
    {
        var data = Prices() with { SessionHours = null };
        Assert.DoesNotContain("\"SessionHours\"", Json(data));
        Assert.DoesNotContain("\"SessionHours\"", Json(EmptyPaper() with { SessionHours = null }));
        Assert.DoesNotContain("\"SessionHours\"", Json(Event("open", At(PaperDay, 9), 110)));
        Assert.Equal(Clock.Open(Days[0]), MarketSessions.OpeningTime(Days[0], null));
        Assert.Equal(Clock.Close(Days[0]), MarketSessions.ClosingTime(Days[0], null));
        Assert.Throws<ArgumentException>(() => MarketSessions.OpeningTime(Days[0], null, requireExplicit: true));
        Assert.Throws<ArgumentException>(() => MarketSessions.ClosingTime(Days[0], null, requireExplicit: true));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unordered")]
    [InlineData("same-open-close")]
    [InlineData("wrong-local-date")]
    [InlineData("late-evidence")]
    [InlineData("missing-availability")]
    [InlineData("missing-evidence")]
    public void InvalidExplicitSchedulesFailBeforeUse(string problem)
    {
        var first = Hours(Days[0]); var second = Hours(Days[1]);
        SessionHours[] hours = problem switch
        {
            "duplicate" => [first, first],
            "unordered" => [second, first],
            "same-open-close" => [first with { CloseAt = first.OpenAt }],
            "wrong-local-date" => [first with { OpenAt = first.OpenAt.AddDays(-1) }],
            "late-evidence" => [first with { AvailableAt = first.OpenAt.AddTicks(1) }],
            "missing-availability" => [first with { AvailableAt = default }],
            _ => [first with { Evidence = " " }]
        };
        Assert.Throws<ArgumentException>(() => MarketSessions.Validate(hours, [Days[0]]));
    }

    [Fact]
    public void UtcOffsetsAreComparedAsInstantsWithinTheKoreanSessionDate()
    {
        var hours = Hours(Days[0], delayed: true);
        var utc = hours with { OpenAt = hours.OpenAt.ToUniversalTime(), CloseAt = hours.CloseAt.ToUniversalTime() };
        MarketSessions.Validate([utc], [Days[0]], requireExplicit: true);
        Assert.Equal(hours.OpenAt, MarketSessions.OpeningTime(Days[0], [utc]));
        Assert.Equal(hours.CloseAt, MarketSessions.ClosingTime(Days[0], [utc]));
    }

    [Fact]
    public void ExplicitMissingDatesNeverUseTheLegacyClockEvenForUnreviewedData()
    {
        Assert.Throws<ArgumentException>(() => MarketSessions.OpeningTime(Days[1], [Hours(Days[0])]));
        Assert.Throws<ArgumentException>(() => MarketSessions.ClosingTime(Days[1], []));
        Assert.Throws<ArgumentException>(() => (Prices() with { SessionHours = [Hours(Days[0])] }).Validate());
        var data = Prices() with { Synthetic = false, PointInTimeCertified = true };
        data.Validate();
        Assert.Throws<ArgumentException>(() => (data with { SessionHours = null }).Validate());
        Assert.Throws<ArgumentException>(() => (data with { SessionHours = data.SessionHours![..^1] }).Validate());
    }

    [Fact]
    public void DelayedCloseRejectsTheOldDailyBarPublicationBoundaryAndPrematureSignals()
    {
        var data = Prices(); var delayed = Days[3];
        var premature = data with { Bars = data.Bars.Select(b => b.Date == delayed
            ? b with { AvailableAt = Clock.Close(delayed) } : b).ToArray() };
        Assert.Throws<ArgumentException>(() => premature.Validate());
        var history = premature.Bars.Take(4).ToArray();
        Assert.Throws<ArgumentException>(() => new PriceStrategy(Momentum).Generate(history,
            At(delayed, 16), data.SessionHours));
        Assert.NotNull(new PriceStrategy(Momentum).Generate(data.Bars.Take(4).ToArray(),
            At(delayed, 16, 30), data.SessionHours));
    }

    [Fact]
    public void TenOClockExecutionCanUsePriorDailyPricesPublishedAtNineThirty()
    {
        var data = Prices(); var delayedOpen = At(Days[3], 10);
        data = data with { Bars = data.Bars.Select((b, i) => i == 2
            ? b with { AvailableAt = delayedOpen.AddMinutes(-30) } : b).ToArray() };
        var trade = Assert.Single(Run(data).Trades);
        Assert.Equal(delayedOpen, trade.EntryTime);
        Assert.True(trade.SignalTime < delayedOpen);
        Assert.Equal(102m, trade.SignalPrice);
        var unavailableAtDecision = data with { Bars = data.Bars.Select((b, i) => i == 2
            ? b with { AvailableAt = delayedOpen } : b).ToArray() };
        Assert.DoesNotContain(Run(unavailableAtDecision).Trades, t => t.EntryTime == delayedOpen);
    }

    [Fact]
    public void BacktestCannotUseOpeningScheduleFirstPublishedAtTheExecutionInstant()
    {
        var data = Prices();
        data = data with { SessionHours = data.SessionHours!.Select(h => h.Date == Days[3]
            ? h with { AvailableAt = h.OpenAt } : h).ToArray() };
        data.Validate(); // Structurally valid, but unavailable at the pre-open decision cutoff.
        Assert.Throws<ArgumentException>(() => Run(data));
    }

    [Fact]
    public void SplitAndInventoryCreditUseActualOpeningTimeWithoutBorrowingTheLaterCredit()
    {
        var data = Prices(); var effective = Days[4]; var opening = At(effective, 10);
        var change = new ShareUnitChange("session-split", "A", effective, effective, 2, 1,
            opening.AddMinutes(-30), "synthetic terms observed at 09:30");
        var credit = new ShareInventoryCredit(change.ActionKey, opening.AddMinutes(1), opening.AddMinutes(1),
            "synthetic inventory observed after the auction");
        data = data with
        {
            SessionHours = data.SessionHours!.Select(h => h.Date == effective ? Hours(effective, true) : h).ToArray(),
            Bars = data.Bars.Select(b => b.Date < effective ? b : b with { Open = b.Open / 2, High = b.High / 2,
                Low = b.Low / 2, Close = b.Close / 2, Volume = b.Volume * 2,
                AvailableAt = b.Date == effective ? At(effective, 16, 30) : b.AvailableAt }).ToArray(),
            ShareUnitChanges = [change], ShareInventoryCredits = [credit]
        };
        var run = Run(data, Momentum with { HoldDays = 1 });
        Assert.Equal(opening, Assert.Single(run.ShareUnitAdjustments!).AppliedAt);
        var trade = Assert.Single(run.Trades, t => t.ShareUnitActionKeys?.Contains(change.ActionKey) == true);
        Assert.Equal(Clock.Open(Days[5]), trade.ExitTime);
        Assert.Equal(18, trade.Quantity);
        Assert.Contains(run.Events, e => e.Contains("inventory credit unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void PrefixRejectsUnobservedRequiredHoursAndDoesNotIncludeFutureEvidence()
    {
        var first = Hours(Days[0]); var future = Hours(Days[1]) with { Evidence = "PRIVATE FUTURE HOURS" };
        var cutoff = first.OpenAt.AddTicks(-1);
        var prefix = MarketSessions.Prefix([first, future], [first.Date], cutoff);
        Assert.Equal(first, Assert.Single(prefix!));
        Assert.DoesNotContain("PRIVATE", Json(prefix));
        Assert.Throws<ArgumentException>(() => MarketSessions.Prefix([first], [Days[1]], cutoff));
        Assert.Throws<ArgumentException>(() => MarketSessions.Prefix([first with { AvailableAt = first.OpenAt }],
            [first.Date], cutoff));
    }

    [Fact]
    public void TrainingUsesActualValidationOpeningBoundaryButExcludesThatAndLaterScheduleRows()
    {
        var seed = DataFiles.Demo(180); var dates = seed.Dates; var boundary = dates[60];
        var data = seed with
        {
            SessionHours = dates.Select(d => Hours(d, d == boundary)).ToArray(),
            Bars = seed.Bars.Select(b => b.Date == dates[59] ? b with { AvailableAt = At(boundary, 9, 30) }
                : b.Date == boundary ? b with { AvailableAt = At(boundary, 16, 30) } : b).ToArray()
        };
        var training = AiResearchWorker.TrainingData(data, Plan);
        Assert.Equal(dates.Take(60), training.SessionHours!.Select(h => h.Date));
        Assert.Equal(At(boundary, 9, 30), training.Bars.Max(b => b.AvailableAt));
        var future = data with { SessionHours = data.SessionHours!.Select(h => h.Date > boundary
            ? h with { OpenAt = At(h.Date, 10), Evidence = "PRIVATE HOLDOUT HOURS" } : h).ToArray() };
        Assert.NotEqual(data.Hash, future.Hash);
        Assert.Equal(training.Hash, AiResearchWorker.TrainingData(future, Plan).Hash);
        Assert.DoesNotContain("PRIVATE HOLDOUT", Json(training));
        var tooLate = data with { Bars = data.Bars.Select(b => b.Date == dates[59]
            ? b with { AvailableAt = At(boundary, 10) } : b).ToArray() };
        Assert.Throws<ArgumentException>(() => AiResearchWorker.TrainingData(tooLate, Plan));
    }

    [Theory]
    [InlineData("AI")]
    [InlineData("COST")]
    public void PrefixBoundaryCannotDependOnSchedulePublishedOnlyAtThatOpening(string consumer)
    {
        var seed = DataFiles.Demo(180); var dates = seed.Dates;
        var boundary = dates[consumer == "AI" ? 60 : 150];
        var data = seed with { SessionHours = dates.Select(d => d == boundary
            ? Hours(d) with { AvailableAt = Clock.Open(d) } : Hours(d)).ToArray() };
        data.Validate();
        if (consumer == "AI")
        {
            var error = Assert.Throws<ArgumentException>(() => AiResearchWorker.TrainingData(data, Plan));
            Assert.Contains("training cutoff", error.Message, StringComparison.Ordinal);
        }
        else
        {
            StrategySpec[] candidates = [new("momentum", 2, .5m, 2), new("reversion", 2, .5m, 2)];
            var plan = Plan with { CostStress = new([new("higher-cost", .001m, .001m)]) };
            var error = Assert.Throws<ArgumentException>(() => new ResearchAgent().Run(data, candidates, plan, Zero, new(), "fixture"));
            Assert.Contains("diagnostic cutoff", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TrainingRetainsPastEffectiveClockButNotKnownFuturePriceTransitionClock()
    {
        var seed = DataFiles.Demo(180); var dates = seed.Dates; var past = dates[0].AddDays(-3);
        var change = new ShareUnitChange("past-effective", "DEMO00", past, dates[65], 2, 1,
            At(past, 9, 30), "known future quote transition terms");
        var credit = new ShareInventoryCredit(change.ActionKey, At(past, 10), At(past, 10), "past credit");
        var hours = dates.Select(d => Hours(d)).Prepend(Hours(past, true)).ToArray();
        var data = seed with
        {
            SessionHours = hours, ShareUnitChanges = [change], ShareInventoryCredits = [credit],
            Bars = seed.Bars.Select(b => b.Ticker == "DEMO00" && b.Date < dates[65]
                ? b with { Open = 0, High = 0, Low = 0, Volume = 0, TradingValue = 0, Tradable = false } : b).ToArray()
        };
        var training = AiResearchWorker.TrainingData(data, Plan);
        Assert.Equal(change, Assert.Single(training.ShareUnitChanges!));
        Assert.Equal(credit, Assert.Single(training.ShareInventoryCredits!));
        Assert.Contains(training.SessionHours!, h => h.Date == past && h.OpenAt == At(past, 10));
        Assert.DoesNotContain(training.SessionHours!, h => h.Date == dates[65]);
        var changed = data with { SessionHours = hours.Select(h => h.Date == dates[65]
            ? h with { OpenAt = At(h.Date, 10), Evidence = "PRIVATE FUTURE TRANSITION HOURS" } : h).ToArray() };
        Assert.Equal(training.Hash, AiResearchWorker.TrainingData(changed, Plan).Hash);
        var replay = JsonSerializer.Deserialize<Dataset>(Json(training))!;
        replay.Validate(); Assert.Equal(training.Hash, replay.Hash);
    }

    [Fact]
    public void FutureHoursDoNotChangeFixedPreHoldoutCostDiagnostics()
    {
        var seed = DataFiles.Demo(180); var dates = seed.Dates;
        var data = seed with { SessionHours = dates.Select(d => Hours(d)).ToArray() };
        var changed = data with { SessionHours = data.SessionHours!.Select(h => h.Date >= dates[150]
            ? h with { OpenAt = At(h.Date, 10), Evidence = "PRIVATE HOLDOUT SESSION EVIDENCE" } : h).ToArray() };
        StrategySpec[] candidates = [new("momentum", 2, .5m, 2), new("reversion", 2, .5m, 2)];
        var plan = Plan with { CostStress = new([new("higher-cost", .001m, .001m)]) };
        var first = new ResearchAgent().Run(data, candidates, plan, Zero, new(), "fixture");
        var second = new ResearchAgent().Run(changed, candidates, plan, Zero, new(), "fixture");
        Assert.NotEqual(first.DataHash, second.DataHash);
        Assert.NotNull(first.CostDiagnostics);
        Assert.Equal(Json(first.CostDiagnostics), Json(second.CostDiagnostics));
        Assert.DoesNotContain("PRIVATE HOLDOUT", Json(first.CostDiagnostics));
    }

    [Fact]
    public void CertifiedCostPrefixKeepsKnownFuturePriceTermsWithoutFutureHours()
    {
        var dates = DataFiles.Demo(180).Dates;
        var change = new ShareUnitChange("known-pending-split", "A", dates[20], dates[160], 2, 1,
            Clock.Open(dates[20]).AddDays(-1), "synthetic preannounced transition");
        var bars = dates.Select((d, i) =>
        {
            var pending = i is >= 20 and < 160; var price = i < 160 ? 100m : 50m;
            return new Bar("A", "sector", d, Clock.Close(d), pending ? 0 : price, pending ? 0 : price,
                pending ? 0 : price, price, pending ? 0 : 100_000, pending ? 0 : price * 100_000, !pending, true);
        }).ToArray();
        var data = new Dataset("FABRICATED CERTIFIED BRANCH, NOT MARKET EVIDENCE", false, true, bars, dates,
            ShareUnitChanges: [change], SessionHours: dates.Select(d => Hours(d)).ToArray());
        var changed = data with { SessionHours = data.SessionHours!.Select(h => h.Date >= dates[150]
            ? h with { OpenAt = At(h.Date, 10), Evidence = "PRIVATE FUTURE PRICE-TRANSITION CLOCK" } : h).ToArray() };
        StrategySpec[] candidates = [new("momentum", 2, .5m, 2), new("reversion", 2, .5m, 2)];
        var plan = Plan with { CostStress = new([new("higher-cost", .001m, .001m)]) };
        var first = new ResearchAgent().Run(data, candidates, plan, Zero, new(), "fixture");
        var second = new ResearchAgent().Run(changed, candidates, plan, Zero, new(), "fixture");
        Assert.NotNull(first.CostDiagnostics);
        Assert.Equal(Json(first.CostDiagnostics), Json(second.CostDiagnostics));
        Assert.DoesNotContain("PRIVATE FUTURE", Json(first.CostDiagnostics));
    }

    [Fact]
    public void PaperAppendsKnownCurrentHoursWithoutMutatingPriorStateOrRewritingHistory()
    {
        var before = EmptyPaper(); var original = Json(before); var hours = Hours(PaperDay, true);
        var opening = Event("open", hours.OpenAt, 110) with { SessionHours = [hours] };
        var opened = Step(before, opening);
        Assert.Equal(original, Json(before));
        Assert.Equal(4, opened.SessionHours!.Length);
        Assert.Equal(hours.OpenAt, Assert.Single(opened.Positions).EntryTime);
        Assert.Equal(hours, Assert.Single(opened.Audit[0].Observation.SessionHours!));
        Assert.Throws<ArgumentException>(() => Step(before, opening with
        { SessionHours = [before.SessionHours![0] with { Evidence = "rewritten history" }, hours] }));
        Assert.Throws<ArgumentException>(() => Step(opened, Event("quote", hours.OpenAt.AddMinutes(1), 110) with
        { SessionHours = [hours with { CloseAt = hours.CloseAt.AddMinutes(1) }] }));
        Assert.Equal(Json(opened), Json(JsonSerializer.Deserialize<PaperState>(Json(opened))!));
    }

    [Fact]
    public void PaperRejectsOldOpeningClockMissingScheduleAndLateAvailability()
    {
        var before = EmptyPaper(); var hours = Hours(PaperDay, true);
        Assert.Throws<ArgumentException>(() => Step(before, Event("open", Clock.Open(PaperDay), 110) with { SessionHours = [hours] }));
        Assert.Throws<ArgumentException>(() => Step(before, Event("open", hours.OpenAt, 110)));
        Assert.Throws<ArgumentException>(() => Step(before, Event("open", hours.OpenAt, 110) with
        { SessionHours = [hours with { AvailableAt = hours.OpenAt.AddTicks(1) }] }));
        var knownAtNineThirty = hours with { AvailableAt = At(PaperDay, 9, 30) };
        Assert.Single(Step(before, Event("open", hours.OpenAt, 110) with { SessionHours = [knownAtNineThirty] }).Positions);
    }

    [Fact]
    public void PaperRejectsPrematureCloseAndQuotesAfterActualClose()
    {
        var hours = Hours(PaperDay, true); var opened = OpenPaper(hours);
        Assert.Throws<ArgumentException>(() => Step(opened, Closing(Clock.Close(PaperDay), 111)));
        Assert.Throws<ArgumentException>(() => Step(opened, Event("quote", hours.CloseAt.AddTicks(1), 111)));
        Assert.Throws<ArgumentException>(() => Step(opened, Closing(hours.CloseAt.AddMinutes(10).AddTicks(1), 111)));
    }

    [Fact]
    public void LateCloseRequiresTheActualClosingQuoteInsteadOfAnAfterHoursQuote()
    {
        var hours = Hours(PaperDay, true); var opened = OpenPaper(hours);
        var late = Closing(hours.CloseAt.AddMinutes(5), 111);
        Assert.Single(Step(opened, late).Equity);
        Assert.Throws<ArgumentException>(() => Step(opened, late with
        { Quotes = [late.Quotes[0] with { Time = late.ObservedAt }] }));
        Assert.Throws<ArgumentException>(() => Step(opened, late with
        { Quotes = [late.Quotes[0] with { Time = hours.CloseAt.AddSeconds(-10).AddTicks(-1) }] }));
        Assert.Single(Step(opened, late with
        { Quotes = [late.Quotes[0] with { Time = hours.CloseAt.AddSeconds(-10) }] }).Equity);
    }

    [Fact]
    public void OpeningCannotExecuteAQuoteTimestampedBeforeTheActualAuction()
    {
        var hours = Hours(PaperDay, true);
        var opening = Event("open", hours.OpenAt, 110) with { SessionHours = [hours] };
        Assert.Throws<ArgumentException>(() => Step(EmptyPaper(), opening with
        { Quotes = [opening.Quotes[0] with { Time = hours.OpenAt.AddSeconds(-1) }] }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void LateCloseMarksLossAndHaltsButCannotManufactureAnAfterHoursFill(int minutes)
    {
        var hours = Hours(PaperDay, true); var opened = OpenPaper(hours);
        var after = Step(opened, Closing(hours.CloseAt.AddMinutes(minutes), 70));
        Assert.True(after.Halted);
        Assert.Empty(after.Fills); Assert.Single(after.Positions);
        Assert.Equal(opened.Cash, after.Cash); Assert.Equal(opened.Turnover, after.Turnover);
        Assert.Equal(9_640m, Assert.Single(after.Equity).Equity);
        Assert.Equal(hours.CloseAt.AddMinutes(minutes), after.LastObservation);
    }

    [Fact]
    public void ExactlyAtActualCloseRetainsTheExistingExecutableStopBoundary()
    {
        var hours = Hours(PaperDay, true); var opened = OpenPaper(hours);
        var closed = Step(opened, Closing(hours.CloseAt, 70));
        Assert.Empty(closed.Positions);
        Assert.Equal(hours.CloseAt, Assert.Single(closed.Fills).Trade.ExitTime);
        Assert.Equal(-360m, closed.Fills[0].Trade.NetProfit);
    }

    [Fact]
    public void PaperJournalReplaysAppendedHoursAndRejectsScheduleTampering()
    {
        var directory = Path.Combine(Path.GetTempPath(), "session-hours-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new PaperJournal(directory); var genesis = journal.Start(EmptyPaper());
            var hours = Hours(PaperDay, true);
            var opened = journal.Commit(genesis, Event("open", hours.OpenAt, 110) with { SessionHours = [hours] },
                hours.OpenAt, "session-fixture");
            Assert.Equal(Json(opened), Json(journal.Recover(genesis)));
            var close = Closing(hours.CloseAt.AddMinutes(1), 70);
            var closed = journal.Commit(opened, close, close.ObservedAt, "session-fixture");
            Assert.Contains("RISK_OR_REGIME_HALT", journal.Evaluate(closed, "session-fixture").Reasons);
            var changed = closed with { SessionHours = closed.SessionHours!.Select((h, i) => i == 0
                ? h with { Evidence = "changed committed session source" } : h).ToArray() };
            Assert.Throws<ArgumentException>(() => journal.Evaluate(changed, "session-fixture"));
            File.WriteAllText(Path.Combine(directory, "state-session-hours-2.json"), Json(changed));
            Assert.Throws<ArgumentException>(() => journal.Evaluate(changed, "session-fixture"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void KrxManifestCarriesExplicitHoursWithoutInventingCertification()
    {
        const string raw = """
            {"OutBlock_1":[{"BAS_DD":"20240102","ISU_CD":"005930","ISU_NM":"synthetic","MKT_NM":"KOSPI","SECT_TP_NM":"section",
            "TDD_OPNPRC":"100","TDD_HGPRC":"101","TDD_LWPRC":"99","TDD_CLSPRC":"100","ACC_TRDVOL":"11","ACC_TRDVAL":"1100","MKTCAP":"100000","LIST_SHRS":"1000"}]}
            """;
        var date = Days[0]; var hours = Hours(date, true); var observed = hours.CloseAt.AddDays(1);
        var snapshot = new KrxSnapshot("synthetic", "KOSPI", date, observed, "synthetic official response fixture",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), raw, KrxClient.Parse(raw, date, "KOSPI"));
        var manifest = new KrxManifest("fixture", [], [date],
            [new("005930", date, "sector", true, true, hours.CloseAt)], SessionHours: [hours]);
        var data = KrxDatasetBuilder.Build([snapshot], manifest);
        Assert.Equal(hours, Assert.Single(data.SessionHours!));
        Assert.False(data.PointInTimeCertified); Assert.Equal(observed, data.Bars[0].AvailableAt);
        Assert.Equal(100m, data.Bars[0].Close); Assert.Equal(11, data.Bars[0].Volume);
        Assert.DoesNotContain("\"SessionHours\"", Json(manifest with { SessionHours = null }));
    }

    private static PaperState EmptyPaper()
    {
        DateOnly[] dates = [new(2024, 1, 3), new(2024, 1, 4), new(2024, 1, 5)];
        var history = dates.Select((d, i) => new Bar("A", "sector", d, Clock.Close(d),
            100 + i, 103 + i, 99 + i, 100 + i, 100_000, (100 + i) * 100_000, true, true)).ToArray();
        var signal = new PriceStrategy(Momentum).Generate(history, history[^1].AvailableAt)!;
        return new("session-hours", ["synthetic-research"], [Momentum], Zero, new(),
            10_000, 10_000, 10_000, 10_000, null, At(PaperDay, 8, 59), "unknown", false, false,
            history, [signal], [], [], [], [], 0, CodeVersion: "session-fixture", ResearchCandidateCount: 4,
            SessionHours: dates.Select(d => Hours(d)).ToArray());
    }
    private static Observation Event(string kind, DateTimeOffset time, decimal price) => new("manual-session-fixture",
        kind, time, [new("A", "sector", time, price, price, true)], []);
    private static Observation Closing(DateTimeOffset time, decimal price) => Event("close", time, price) with
    { Quotes = [new("A", "sector", time > At(PaperDay, 16, 30) ? At(PaperDay, 16, 30) : time, price, price, true)],
      ClosedBars = [new("A", "sector", PaperDay, time, 110, Math.Max(112, price), Math.Min(69, price), price,
        100_000, price * 100_000, true, true)] };
    private static PaperState Step(PaperState before, Observation observation) => PaperEngine.Step(before, observation, observation.ObservedAt);
    private static PaperState OpenPaper(SessionHours hours) => Step(EmptyPaper(),
        Event("open", hours.OpenAt, 110) with { SessionHours = [hours] });
}
