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
