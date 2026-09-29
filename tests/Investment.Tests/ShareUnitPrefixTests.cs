using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class ShareUnitPrefixTests
{
    private static readonly ResearchPlan Plan = new(60, 30, 30, 30, 1, 30);
    private static readonly DateOnly[] Dates = DataFiles.Demo(180).Dates;
    private static readonly StrategySpec[] Candidates = [new("momentum", 2, .5m, 2), new("reversion", 2, .5m, 2)];
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    [Fact] public void TrainingKeepsNecessaryEarlierEffectiveUnitsAndKnownFuturePriceSchedule()
    {
        var change = Change("past-action", Dates[0].AddDays(-3), Dates[65]);
        var credit = new ShareInventoryCredit(change.ActionKey, Clock.Open(Dates[70]), Clock.Open(Dates[70]), "FUTURE-CREDIT");
        var data = Data([change], [credit]);
        var training = AiResearchWorker.TrainingData(data, Plan);
        Assert.Equal(change, Assert.Single(training.ShareUnitChanges!));
        Assert.Null(training.ShareInventoryCredits);
        Assert.Equal(Dates[65], training.ShareUnitChanges![0].PriceUnitDate);
        var view = ShareUnits.SignalHistory(training.Bars, training.Dates[^1], Clock.Open(Dates[60]).AddTicks(-1), training.ShareUnitChanges);
        Assert.Equal(100m, training.Bars[0].Close); Assert.Equal(50m, view[0].Close);
        Assert.Equal(data.Bars.Take(60), training.Bars);
        var restored = JsonSerializer.Deserialize<Dataset>(Json(training))!;
        restored.Validate(); Assert.Equal(training.Hash, restored.Hash);
    }

    [Fact] public async Task FutureEventsAndCreditsCannotEnterTrainingHashAndEmptyListsStayNull()
    {
        var early = Change("known-action", Dates[20], Dates[20]);
        var future = Change("future-action", Dates[160], Dates[160]);
        var lateCredit = new ShareInventoryCredit(early.ActionKey, Clock.Open(Dates[30]), Clock.Open(Dates[150]), "PRIVATE-LATE-CREDIT");
        var futureCredit = new ShareInventoryCredit(future.ActionKey, Clock.Open(Dates[170]), Clock.Open(Dates[170]), "PRIVATE-FUTURE-CREDIT");
        var original = Data([early, future], [lateCredit, futureCredit]);
        var changed = Data([early, future with { NewShares = 3, Evidence = "CHANGED-FUTURE-EVIDENCE" }],
            [lateCredit with { AvailableAt = Clock.Open(Dates[155]), Evidence = "CHANGED-LATE-CREDIT" },
             futureCredit with { AvailableAt = Clock.Open(Dates[175]), Evidence = "CHANGED-FUTURE-CREDIT" }]);
        Assert.NotEqual(original.Hash, changed.Hash);
        var training = AiResearchWorker.TrainingData(original, Plan);
        Assert.Equal(training.Hash, AiResearchWorker.TrainingData(changed, Plan).Hash);
        Assert.Equal(await Prompt(original), await Prompt(changed));
        Assert.Equal(early, Assert.Single(training.ShareUnitChanges!)); Assert.Null(training.ShareInventoryCredits);
        Assert.DoesNotContain("PRIVATE", Json(training)); Assert.DoesNotContain("future-action", Json(training));
        var empty = AiResearchWorker.TrainingData(Data(), Plan);
        var onlyFuture = AiResearchWorker.TrainingData(Data([future], [futureCredit]), Plan);
        Assert.Null(onlyFuture.ShareUnitChanges); Assert.Null(onlyFuture.ShareInventoryCredits);
        Assert.Equal(empty.Hash, onlyFuture.Hash);
    }

    [Fact] public void KnownInventoryCreditIsRetainedButExactValidationOpenIsExcluded()
    {
        var change = Change("known-action", Dates[20], Dates[20]);
        var cutoff = Clock.Open(Dates[60]).AddTicks(-1);
        var credit = new ShareInventoryCredit(change.ActionKey, cutoff.AddHours(-1), cutoff, "known-credit");
        var included = AiResearchWorker.TrainingData(Data([change], [credit]), Plan);
        Assert.Equal(credit, Assert.Single(included.ShareInventoryCredits!));
        var excluded = AiResearchWorker.TrainingData(Data([change], [credit with { AvailableAt = cutoff.AddTicks(1) }]), Plan);
        Assert.Null(excluded.ShareInventoryCredits); Assert.NotEqual(included.Hash, excluded.Hash);
    }

    [Fact] public async Task TrainingAllowsDelayedPublicationBeforeValidationButRejectsLaterPricesBeforeProvider()
    {
        var data = Data(); var cutoff = Clock.Open(Dates[60]).AddTicks(-1);
        var valid = data with { Bars = data.Bars.Select(b => b.Date == Dates[59] ? b with { AvailableAt = cutoff } : b).ToArray() };
        Assert.Equal(cutoff, AiResearchWorker.TrainingData(valid, Plan).Bars[^1].AvailableAt);
        var invalid = valid with { Bars = valid.Bars.Select(b => b.Date == Dates[59] ? b with { AvailableAt = cutoff.AddTicks(1) } : b).ToArray() };
        var directory = Path.Combine(Path.GetTempPath(), "share-prefix-invalid-" + Guid.NewGuid().ToString("N"));
        var calls = 0;
        Task<HypothesisProposal> Unexpected(string input, CancellationToken ct)
        { calls++; throw new InvalidOperationException("Provider must not observe future training prices."); }
        await Assert.ThrowsAsync<ArgumentException>(() => new AiResearchWorker(directory).Run(invalid, Plan,
            new(), new(), new("test-model", true, MaximumRequests: 1), "fixture", Unexpected));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task AiPromptUsesAnonymousConsistentPriceAndFractionalVolumeUnitsWithoutChangingRawData()
    {
        var reverse = Change("PRIVATE-REVERSE-ACTION", Dates[20], Dates[20]) with
        { NewShares = 1, OldShares = 2, Evidence = "PRIVATE-SOURCE-EVIDENCE" };
        var data = Data([reverse]); var rawHash = data.Hash;
        var prompt = await Prompt(data);
        using var json = JsonDocument.Parse(prompt);
        var segment = Assert.Single(json.RootElement.GetProperty("Series").EnumerateArray()).GetProperty("Segments")[0];
        Assert.All(segment.GetProperty("CloseIndex").EnumerateArray(), value => Assert.Equal(1m, value.GetDecimal()));
        Assert.Equal(1m, segment.GetProperty("VolumeIndex")[0].GetDecimal());
        Assert.Equal(5m / 5.5m, segment.GetProperty("VolumeIndex")[20].GetDecimal());
        Assert.DoesNotContain("PRIVATE", prompt); Assert.DoesNotContain("005930", prompt);
        Assert.DoesNotContain(Dates[0].ToString("yyyy-MM-dd"), prompt); Assert.DoesNotContain(data.Source, prompt);
        Assert.Equal(rawHash, data.Hash); Assert.Equal(100m, data.Bars[0].Close); Assert.Equal(11, data.Bars[0].Volume);
        Assert.Equal(200m, data.Bars[20].Close); Assert.Equal(5, data.Bars[20].Volume);
    }

    [Fact] public void HistoricalVolumeRetainsLongPrecisionAndFractionUntilExecutionRounding()
    {
        const long rawVolume = 9_007_199_254_740_993;
        var reverse = Change("reverse", Dates[20], Dates[25]) with { NewShares = 1, OldShares = 2 };
        // Even before raw quotes switch units, the comparison volume uses current economic units.
        var adjusted = ShareUnits.CapacityVolume("005930", Dates[0], Dates[22], rawVolume, Clock.Close(Dates[22]), [reverse]);
        Assert.Equal(4_503_599_627_370_496.5m, adjusted);
        Assert.Equal(rawVolume, ShareUnits.CapacityVolume("005930", Dates[0], Dates[19], rawVolume, Clock.Close(Dates[19]), [reverse]));
    }

    [Fact] public void HoldoutEventsAndDelayedCreditsDoNotChangeFixedCostDiagnostics()
    {
        var known = Change("known-action", Dates[20], Dates[20]);
        var future = Change("future-action", Dates[160], Dates[160]);
        var lateCredit = new ShareInventoryCredit(known.ActionKey, Clock.Open(Dates[30]), Clock.Open(Dates[150]), "late-credit");
        var data = Data([known, future], [lateCredit]);
        var changed = Data([known, future with { NewShares = 3, Evidence = "changed future" }],
            [lateCredit with { AvailableAt = Clock.Open(Dates[155]), Evidence = "changed future credit" }]);
        var plan = Plan with { CostStress = new([new("higher-cost", .001m, .001m)]) };
        var before = new ResearchAgent().Run(data, Candidates, plan, new(), new(), "fixture");
        var after = new ResearchAgent().Run(changed, Candidates, plan, new(), new(), "fixture");
        Assert.NotEqual(before.DataHash, after.DataHash);
        Assert.Equal(Json(before.CostDiagnostics), Json(after.CostDiagnostics));
        Assert.Equal(before.Folds.Select(f => f.SelectedId), after.Folds.Select(f => f.SelectedId));
        Assert.DoesNotContain("future-action", Json(before.CostDiagnostics));
    }

    [Fact] public void CompletedPositionMetricsDoNotSumQuantitiesAcrossShareUnits()
    {
        var entered = Clock.Open(Dates[0]);
        Trade Fill(DateTimeOffset exit, decimal profit, bool completed) => new("strategy", "005930", entered.AddMinutes(-1),
            100, entered, 100, exit, 50, int.MaxValue, profit, .1m, "fixture", "evidence", completed);
        var metrics = Statistics.Measure([], [Fill(Clock.Open(Dates[1]), 10, false), Fill(Clock.Open(Dates[2]), 15, true)], 100, 0, 0);
        Assert.Equal(1, metrics.NumberOfTrades); Assert.Equal(25m, metrics.ExpectedValuePerTrade);
        Assert.Equal(25m, metrics.AverageProfit); Assert.Equal(1m, metrics.WinRate);
        Assert.Equal((Clock.Open(Dates[2]) - entered).TotalDays, metrics.AverageHoldingDays);
    }

    [Fact] public void KrxManifestPassesExplicitUnitsAndCreditsWithoutChangingRawPricesOrCertification()
    {
        const string raw = """
            {"OutBlock_1":[{"BAS_DD":"20240102","ISU_CD":"005930","ISU_NM":"synthetic","MKT_NM":"KOSPI","SECT_TP_NM":"section",
            "TDD_OPNPRC":"100","TDD_HGPRC":"101","TDD_LWPRC":"99","TDD_CLSPRC":"100","ACC_TRDVOL":"11","ACC_TRDVAL":"1100","MKTCAP":"100000","LIST_SHRS":"1000"}]}
            """;
        var date = new DateOnly(2024, 1, 2); var observed = Clock.Close(date).AddDays(1);
        var snapshot = new KrxSnapshot("synthetic", "KOSPI", date, observed, "synthetic official response fixture",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), raw, KrxClient.Parse(raw, date, "KOSPI"));
        var change = Change("fixture-action", date, date);
        var credit = new ShareInventoryCredit(change.ActionKey, Clock.Open(date), Clock.Open(date), "synthetic inventory evidence");
        var manifest = new KrxManifest("fixture", [], [date], [new("005930", date, "sector", true, true, Clock.Close(date), true)],
            ShareUnitChanges: [change], ShareInventoryCredits: [credit]);
        var data = KrxDatasetBuilder.Build([snapshot], manifest);
        Assert.Equal(change, Assert.Single(data.ShareUnitChanges!)); Assert.Equal(credit, Assert.Single(data.ShareInventoryCredits!));
        Assert.False(data.PointInTimeCertified); Assert.Equal(observed, data.Bars[0].AvailableAt);
        Assert.Equal(100m, data.Bars[0].Close); Assert.Equal(11, data.Bars[0].Volume); Assert.True(data.Bars[0].CorporateAction);
        Assert.DoesNotContain("ShareUnitChanges", Json(manifest with { ShareUnitChanges = null, ShareInventoryCredits = null }));
    }

    private static ShareUnitChange Change(string key, DateOnly effective, DateOnly priceUnit) =>
        new(key, "005930", effective, priceUnit, 2, 1, Clock.Open(effective).AddTicks(-1), "synthetic unit evidence");

    private static Dataset Data(ShareUnitChange[]? changes = null, ShareInventoryCredit[]? credits = null)
    {
        var bars = Dates.Select(date =>
        {
            decimal price = 100; decimal volume = 11;
            foreach (var change in (changes ?? []).Where(c => c.PriceUnitDate <= date))
            { price = price * change.OldShares / change.NewShares; volume = volume * change.NewShares / change.OldShares; }
            var pending = (changes ?? []).Any(c => c.EffectiveDate <= date && date < c.PriceUnitDate);
            return new Bar("005930", "sector", date, Clock.Close(date), pending ? 0 : price,
                pending ? 0 : price, pending ? 0 : price, price, pending ? 0 : (long)volume,
                pending ? 0 : price * (long)volume, !pending, true);
        }).ToArray();
        return new("PRIVATE RAW PRICE SOURCE", true, false, bars, ShareUnitChanges: changes, ShareInventoryCredits: credits);
    }

    private static async Task<string> Prompt(Dataset data)
    {
        var directory = Path.Combine(Path.GetTempPath(), "share-prefix-prompt-" + Guid.NewGuid().ToString("N"));
        string? captured = null;
        try
        {
            Task<HypothesisProposal> Capture(string input, CancellationToken ct)
            { captured = input; throw new InvalidOperationException("Synthetic provider stops after capturing the request."); }
            var result = await new AiResearchWorker(directory).Run(data, Plan, new(), new(),
                new("test-model", true, MaximumRequests: 1), "fixture", Capture);
            Assert.Equal("GENERATION_FAILED", result.Status);
            return captured ?? throw new InvalidOperationException("AI prompt was not constructed.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
