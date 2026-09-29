using Investment.Core;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Investment.Tests;

public sealed class AiResearchTests
{
    private static readonly AiSettings Settings = new("test-model", true, MaximumRequests: 2);
    private static readonly ResearchPlan Plan = new(60, 30, 30, 30, 1, 30);
    private static string Response(string status = "completed", StrategySpec[]? candidates = null) => JsonSerializer.Serialize(new
    {
        id = "response-fixture", status,
        output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(new HypothesisOutput("Falsifiable training-only fixture", candidates ?? new BaselineHypotheses().Generate())) } } } },
        usage = new { input_tokens = 100, output_tokens = 200 }
    });
    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body { get; private set; } public Uri? Uri { get; private set; } public bool HasKey { get; private set; } public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(ct); Uri = request.RequestUri;
            HasKey = request.Headers.Authorization?.Scheme == "Bearer";
            return new(status) { Content = new StringContent(response) };
        }
    }
    [Fact] public async Task OfficialRequestIsStructuredBoundedAndDoesNotPutKeyInBody()
    {
        var handler = new Handler(Response()); using var http = new HttpClient(handler);
        var result = await new OpenAiHypotheses(http).Generate("fake-test-secret", Settings, "training fixture");
        Assert.Equal("https://api.openai.com/v1/responses", handler.Uri!.AbsoluteUri); Assert.True(handler.HasKey);
        Assert.DoesNotContain("fake-test-secret", handler.Body!);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(2048, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.True(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.Equal(4, result.Output.Candidates.Length); Assert.Equal(200, result.OutputTokens); Assert.Equal(64, result.RawHash.Length);
    }
    [Fact] public async Task IncompleteUnsupportedAndQuotaResponsesNeverProduceCandidatesOrRetry()
    {
        foreach (var response in new[] { Response("incomplete"), Response(candidates: [new("unknown-code", 2, 0, 1)]), Response(candidates: Enumerable.Repeat(new StrategySpec("momentum", 2, 0, 1), 4).ToArray()) })
        {
            var handler = new Handler(response); using var http = new HttpClient(handler);
            await Assert.ThrowsAnyAsync<Exception>(() => new OpenAiHypotheses(http).Generate("fake", Settings, "training"));
            Assert.Equal(1, handler.Calls);
        }
        var quota = new Handler("secret-provider-error", HttpStatusCode.TooManyRequests); using var quotaHttp = new HttpClient(quota);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new OpenAiHypotheses(quotaHttp).Generate("fake", Settings, "training"));
        Assert.DoesNotContain("secret-provider-error", error.Message); Assert.Equal(1, quota.Calls);
    }
    [Fact] public async Task DisabledMissingKeyAndOversizedInputNeverReachNetwork()
    {
        var handler = new Handler(Response()); using var http = new HttpClient(handler); var client = new OpenAiHypotheses(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.Generate("fake", Settings with { Enabled = false }, "training"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Generate("", Settings, "training"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Generate("fake", Settings, new string('x', 64001)));
        Assert.Equal(0, handler.Calls);
    }
    [Fact] public async Task WorkerLearnsOnlyFromAnonymizedTrainingAndStopsAtBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = DataFiles.Demo(180); var firstHoldout = data.Dates[^30];
            var changed = data with { Bars = data.Bars.Select(b => b.Date >= firstHoldout ? b with { Close = b.Close * 2, High = b.High * 2, Open = b.Open * 2, Low = b.Low * 2 } : b).ToArray() };
            Assert.Equal(AiResearchWorker.TrainingData(data, Plan).Hash, AiResearchWorker.TrainingData(changed, Plan).Hash);
            var inputs = new List<string>();
            async Task<HypothesisProposal> Generate(string input, CancellationToken ct)
            {
                inputs.Add(input);
                var candidates = new BaselineHypotheses().Generate().Select(s => s with { Lookback = s.Lookback + inputs.Count }).ToArray();
                using var roundHttp = new HttpClient(new Handler(Response(candidates: candidates)));
                return await new OpenAiHypotheses(roundHttp).Generate("fake", Settings, input, ct);
            }
            var result = await new AiResearchWorker(root).Run(data, Plan, new(), new(), Settings, "fixture", Generate);
            Assert.Equal(2, result.Rounds.Length); Assert.Equal("REQUEST_BUDGET_EXHAUSTED", result.Status);
            Assert.True(result.TrainingEnd < firstHoldout); Assert.Contains("not independent validation", result.Note);
            Assert.All(inputs, input => { Assert.DoesNotContain("DEMO", input); Assert.DoesNotContain(data.Source, input); });
            using var second = JsonDocument.Parse(inputs[1]); Assert.Equal(4, second.RootElement.GetProperty("Feedback").GetArrayLength());
            Assert.Equal(2, Directory.GetFiles(root, "ai-attempt-*.json").Length);
            Assert.Single(Directory.GetFiles(root, "ai-exploration-*.json"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task WorkerPreservesFailureWithoutProviderSecretsOrHiddenRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N")); var calls = 0;
        try
        {
            Task<HypothesisProposal> Fail(string input, CancellationToken ct) { calls++; throw new InvalidOperationException("secret-test-provider-message"); }
            var result = await new AiResearchWorker(root).Run(DataFiles.Demo(180), Plan, new(), new(), Settings, "fixture", Fail);
            Assert.Equal(1, calls); Assert.Equal("GENERATION_FAILED", result.Status); Assert.Empty(result.Rounds);
            Assert.All(Directory.GetFiles(root), path => Assert.DoesNotContain("secret-test-provider-message", File.ReadAllText(path)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static Dataset ChangingUniverse()
    {
        var data = DataFiles.Demo(180); var dates = data.Dates;
        var bars = data.Bars.Where(b => b.Ticker switch
        {
            "DEMO00" => b.Date >= dates[5],
            "DEMO01" => b.Date < dates[15] || b.Date >= dates[25],
            "DEMO02" => b.Date < dates[35],
            "DEMO03" => b.Date >= dates[60],
            "DEMO04" => b.Date >= dates[59],
            _ => true
        }).Select(b => b with { Member = false }).ToArray();
        SecurityLifecycleEvent Event(string ticker, int session, string kind) =>
            new(ticker, dates[session], kind, Clock.Open(dates[session]), "fixture-notice");
        return data with { Bars = bars, LifecycleEvents = [Event("DEMO00", 5, "LISTED"),
            Event("DEMO01", 15, "DELISTED"), Event("DEMO01", 25, "LISTED"),
            Event("DEMO02", 35, "DELISTED"), Event("DEMO03", 60, "LISTED"), Event("DEMO04", 59, "LISTED")] };
    }
    [Fact] public void TrainingPrefixPreservesLifecycleBoundariesAndExcludesFutureEvidence()
    {
        var data = ChangingUniverse(); var training = AiResearchWorker.TrainingData(data, Plan);
        training.Validate();
        Assert.Equal(5, training.LifecycleEvents!.Length);
        Assert.Contains(training.LifecycleEvents, e => e.Kind == "DELISTED");
        Assert.Contains(training.LifecycleEvents, e => e.Ticker == "DEMO01" && e.Kind == "LISTED");
        Assert.Contains(training.LifecycleEvents, e => e.Date == training.Dates[^1]);
        Assert.DoesNotContain(training.LifecycleEvents, e => e.Ticker == "DEMO03");
        Assert.DoesNotContain(training.Bars, b => b.Ticker == "DEMO03");
        var changedFuture = data with { LifecycleEvents = data.LifecycleEvents!.Select(e => e.Ticker == "DEMO03"
            ? e with { Evidence = "future-revision-not-available-to-training" } : e).ToArray() };
        Assert.Equal(training.Hash, AiResearchWorker.TrainingData(changedFuture, Plan).Hash);
    }
    [Fact] public async Task WorkerCanEvaluateSparseTrainingWithoutLosingListingEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = ChangingUniverse(); var settings = Settings with { MaximumRequests = 1 };
            var handler = new Handler(Response()); using var http = new HttpClient(handler);
            var client = new OpenAiHypotheses(http);
            var result = await new AiResearchWorker(root).Run(data, Plan, new(), new(), settings, "fixture",
                (input, ct) => client.Generate("fake", settings, input, ct));
            Assert.Equal(1, handler.Calls); Assert.Single(result.Rounds);
            Assert.Equal(4, result.Rounds[0].Feedback.Length);
            Assert.Single(Directory.GetFiles(root, "ai-exploration-*.json"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public void TrainingRetainsAnExplicitListingAtTheFirstTrainingSession()
    {
        var data = ChangingUniverse(); var first = data.Dates[0];
        var notice = new SecurityLifecycleEvent("DEMO05", first, "LISTED", Clock.Open(first), "first-session-notice");
        data = data with { LifecycleEvents = data.LifecycleEvents!.Append(notice).ToArray() };
        var training = AiResearchWorker.TrainingData(data, Plan);
        training.Validate(); Assert.Contains(notice, training.LifecycleEvents!);
    }
    [Fact] public void TrainingOmitsBoundaryEventsForATickerWithOnlyFutureRows()
    {
        var data = ChangingUniverse(); var dates = data.Dates;
        var initialDelisting = new SecurityLifecycleEvent("DEMO00", dates[0], "DELISTED", Clock.Open(dates[0]), "left-boundary-notice");
        var futureListing = new SecurityLifecycleEvent("DEMO00", dates[70], "LISTED", Clock.Open(dates[70]), "future-listing-notice");
        data = data with
        {
            Bars = data.Bars.Where(b => b.Ticker != "DEMO00" || b.Date >= dates[70]).ToArray(),
            LifecycleEvents = data.LifecycleEvents!.Where(e => e.Ticker != "DEMO00").Concat([initialDelisting, futureListing]).ToArray()
        };
        data.Validate();
        var training = AiResearchWorker.TrainingData(data, Plan); training.Validate();
        Assert.DoesNotContain(training.Bars, b => b.Ticker == "DEMO00");
        Assert.DoesNotContain(training.LifecycleEvents!, e => e.Ticker == "DEMO00");
        Assert.Equal(data.LifecycleEvents.Where(e => e.Ticker != "DEMO00" && e.Date <= dates[59]), training.LifecycleEvents!);
        var movedFuture = data with
        {
            Bars = data.Bars.Where(b => b.Ticker != "DEMO00" || b.Date >= dates[80]).ToArray(),
            LifecycleEvents = data.LifecycleEvents.Select(e => e == futureListing
                ? e with { Date = dates[80], AvailableAt = Clock.Open(dates[80]) } : e).ToArray()
        };
        Assert.Equal(training.Hash, AiResearchWorker.TrainingData(movedFuture, Plan).Hash);
    }
    [Fact] public async Task WorkerPromptPreservesAnonymousSessionGapsAndResetsRelistedPrices()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = ChangingUniverse(); var dates = data.Dates;
            data = data with { Bars = data.Bars.Select(b => b.Ticker == "DEMO01" && b.Date >= dates[25]
                ? b with { Open = b.Open * 4, High = b.High * 4, Low = b.Low * 4, Close = b.Close * 4, Volume = b.Volume * 3 } : b).ToArray() };
            var future = data with
            {
                LifecycleEvents = data.LifecycleEvents!.Select(e => e.Date >= dates[60] ? e with { Evidence = "future-notice" } : e).ToArray(),
                Bars = data.Bars.Select(b => b.Date >= dates[60]
                    ? b with { Open = b.Open * 2, High = b.High * 2, Low = b.Low * 2, Close = b.Close * 2 } : b).ToArray()
            };
            async Task<string> Prompt(Dataset input, string suffix)
            {
                var settings = Settings with { MaximumRequests = 1 };
                var handler = new Handler(Response()); using var http = new HttpClient(handler);
                await new AiResearchWorker(Path.Combine(root, suffix)).Run(input, Plan, new(), new(), settings, "fixture",
                    (text, ct) => new OpenAiHypotheses(http).Generate("fake", settings, text, ct));
                Assert.Equal(1, handler.Calls);
                using var request = JsonDocument.Parse(handler.Body!);
                return request.RootElement.GetProperty("input").GetString()!;
            }
            var prompt = await Prompt(data, "original");
            Assert.Equal(prompt, await Prompt(future, "future-mutated"));
            Assert.DoesNotContain("DEMO", prompt); Assert.DoesNotContain(data.Source, prompt);
            Assert.DoesNotContain("fixture-notice", prompt); Assert.DoesNotContain("future-notice", prompt);
            Assert.DoesNotContain(dates[0].ToString("yyyy-MM-dd"), prompt);
            using var document = JsonDocument.Parse(prompt); var body = document.RootElement;
            Assert.Equal(60, body.GetProperty("TrainingSessions").GetInt32());
            var series = body.GetProperty("Series").EnumerateArray().ToArray();
            Assert.Equal(5, series.Length); // The future listing has no symbol in the training prompt.
            var initialListing = series.Single(s => s.GetProperty("Symbol").GetString() == "S0").GetProperty("Segments");
            Assert.Equal(5, initialListing[0].GetProperty("SessionOffsets")[0].GetInt32());
            var relisted = series.Single(s => s.GetProperty("Symbol").GetString() == "S1").GetProperty("Segments");
            Assert.Equal(2, relisted.GetArrayLength());
            Assert.Equal(Enumerable.Range(0, 15), relisted[0].GetProperty("SessionOffsets").EnumerateArray().Select(e => e.GetInt32()));
            Assert.Equal(Enumerable.Range(25, 35), relisted[1].GetProperty("SessionOffsets").EnumerateArray().Select(e => e.GetInt32()));
            Assert.All(relisted.EnumerateArray(), segment =>
            {
                Assert.Equal(1m, segment.GetProperty("CloseIndex")[0].GetDecimal());
                Assert.Equal(1m, segment.GetProperty("VolumeIndex")[0].GetDecimal());
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task InvalidLifecycleTrainingFailsBeforeProviderOrAttempt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N")); var calls = 0;
        Task<HypothesisProposal> Unexpected(string input, CancellationToken ct)
        { calls++; throw new InvalidOperationException("Provider must not be called for invalid data."); }
        var data = ChangingUniverse() with { LifecycleEvents = null };
        await Assert.ThrowsAsync<ArgumentException>(() => new AiResearchWorker(root).Run(data, Plan, new(), new(), Settings, "fixture", Unexpected));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(root));
    }
    [Fact] public async Task WorkerCostPromptContainsOnlyAnonymousTrainingRates()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = ChangingUniverse(); var dates = data.Dates;
            var rows = dates.Select((date, offset) => new SellTaxSession(date, date.AddDays(3),
                offset < 30 ? .01m : offset < 60 ? .02m : .099m, "PRIVATE-TAX-EVIDENCE")).ToArray();
            var costs = new Costs(.00015m, .777m, .001m, new("PRIVATE-SCHEDULE-SOURCE", rows));
            async Task<string> Prompt(Costs input, string suffix)
            {
                var settings = Settings with { MaximumRequests = 1 };
                var handler = new Handler(Response()); using var http = new HttpClient(handler);
                await new AiResearchWorker(Path.Combine(root, suffix)).Run(data, Plan, input, new(), settings, "fixture",
                    (text, ct) => new OpenAiHypotheses(http).Generate("fake", settings, text, ct));
                Assert.Equal(1, handler.Calls);
                using var request = JsonDocument.Parse(handler.Body!);
                return request.RootElement.GetProperty("input").GetString()!;
            }
            var prompt = await Prompt(costs, "original");
            var futureChanged = costs with { TaxSchedule = new("OTHER-PRIVATE-SOURCE", rows.Select((row, offset) =>
                offset >= 60 ? row with { Rate = .8m, Evidence = "PRIVATE-FUTURE-TAX" } : row).ToArray()) };
            Assert.Equal(prompt, await Prompt(futureChanged, "future-changed"));
            Assert.Equal(prompt, await Prompt(costs with { TaxSchedule = new("training-only-source", rows.Take(60).ToArray()) }, "training-only"));
            Assert.DoesNotContain("PRIVATE-", prompt);
            Assert.DoesNotContain(dates[0].ToString("yyyy-MM-dd"), prompt);
            Assert.DoesNotContain(rows[0].SettlementDate.ToString("yyyy-MM-dd"), prompt);
            using var document = JsonDocument.Parse(prompt); var projected = document.RootElement.GetProperty("Costs");
            Assert.Equal(costs.Commission, projected.GetProperty("Commission").GetDecimal());
            Assert.Equal(costs.Slippage, projected.GetProperty("Slippage").GetDecimal());
            Assert.False(projected.TryGetProperty("TaxSchedule", out _));
            Assert.False(projected.TryGetProperty("SellTax", out _));
            var rates = projected.GetProperty("SellTaxBySession").EnumerateArray().ToArray();
            Assert.Equal(60, rates.Length);
            Assert.Equal(Enumerable.Range(0, 60), rates.Select(row => row.GetProperty("SessionOffset").GetInt32()));
            Assert.All(rates.Take(30), row => Assert.Equal(.01m, row.GetProperty("Rate").GetDecimal()));
            Assert.All(rates.Skip(30), row => Assert.Equal(.02m, row.GetProperty("Rate").GetDecimal()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task MissingTrainingCostMappingFailsBeforeProviderOrAttempt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N")); var calls = 0;
        Task<HypothesisProposal> Unexpected(string input, CancellationToken ct)
        { calls++; throw new InvalidOperationException("Provider must not be called for incomplete costs."); }
        var data = ChangingUniverse();
        var rows = data.Dates.Take(60).Where((_, offset) => offset != 30)
            .Select(date => new SellTaxSession(date, date.AddDays(3), .002m, "fixture")).ToArray();
        var costs = new Costs(TaxSchedule: new("fixture", rows));
        await Assert.ThrowsAsync<ArgumentException>(() => new AiResearchWorker(root).Run(data, Plan, costs, new(), Settings, "fixture", Unexpected));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(root));
    }
    [Fact] public async Task ImpossibleStressCostsFailBeforeProviderOrAttempt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N")); var calls = 0;
        Task<HypothesisProposal> Unexpected(string input, CancellationToken ct)
        { calls++; throw new InvalidOperationException("Provider must not be called for invalid stress costs."); }
        var plan = Plan with { CostStress = new([new("invalid-total", .6m, 0)]) };
        await Assert.ThrowsAsync<ArgumentException>(() => new AiResearchWorker(root).Run(DataFiles.Demo(180), plan,
            new Costs(.5m, 0, 0), new(), Settings, "fixture", Unexpected));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(root));
    }
    [Fact] public async Task WorkerRejectsNormalizedCandidatesThatDifferFromRawProviderEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new Handler(Response()));
            async Task<HypothesisProposal> Edited(string input, CancellationToken ct)
            {
                var result = await new OpenAiHypotheses(http).Generate("fake", Settings, input, ct);
                return result with { Output = result.Output with { Candidates = result.Output.Candidates.Select(s => s with { Threshold = .2m }).ToArray() } };
            }
            var result = await new AiResearchWorker(root).Run(DataFiles.Demo(180), Plan, new(), new(), Settings, "fixture", Edited);
            Assert.Equal("GENERATION_FAILED", result.Status); Assert.Empty(result.Rounds);
            Assert.Empty(Directory.GetFiles(root, "ai-round-*.json"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task AiCohortCarriesGenerationTimeAndCannotPromoteHistoricalForwardWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-research-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = new DirectoryInfo(AppContext.BaseDirectory);
            while (project != null && !File.Exists(Path.Combine(project.FullName, "Directory.Build.props"))) project = project.Parent;
            var source = SourceSnapshot.Capture(project!.FullName, typeof(CohortAgent).Assembly);
            var data = DataFiles.Demo(180); var settings = Settings with { MaximumRequests = 1 };
            using var http = new HttpClient(new Handler(Response())); var client = new OpenAiHypotheses(http);
            var exploration = await new AiResearchWorker(root).Run(data, Plan, new(), new(), settings, source.Hash,
                (input, ct) => client.Generate("fake", settings, input, ct));
            var candidates = exploration.Rounds.SelectMany(r => r.Proposal.Output.Candidates).ToArray();
            var time = exploration.Rounds.Max(r => r.Proposal.ReceivedAt);
            var cohort = new CohortAgent().Run(data, candidates, Plan, new(), new(), source.Hash, time);
            Assert.Contains(cohort.Evaluation.Reasons, r => r.StartsWith("AI_HYPOTHESIS_POSTDATES_FORWARD_WINDOWS", StringComparison.Ordinal));
            var archive = new ExperimentArchive(2, data, source, Cohort: cohort, Ai: exploration);
            Assert.True(archive.Reproduce(source).Matches);
            Assert.Throws<ArgumentException>(() => (archive with { Ai = null }).Reproduce(source));
            Assert.Throws<ArgumentException>(() => (archive with { Cohort = cohort with { HypothesesCreatedAt = time.AddYears(-1) } }).Reproduce(source));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
