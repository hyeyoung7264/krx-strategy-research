using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Investment.Core;

public sealed record AiSettings(string Model, bool Enabled = false, int MaximumRequests = 1, int MaximumOutputTokens = 2048,
    int MaximumInputCharacters = 64000, int CandidateLimit = 8)
{
    public void Validate()
    {
        if (!Enabled) throw new ArgumentException("AI calls disabled. Explicitly configure model and bounded request/token limits before enabling.");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 100 || Model.Any(char.IsControl) || MaximumRequests is < 1 or > 10 ||
            MaximumOutputTokens is < 128 or > 4096 || MaximumInputCharacters is < 1000 or > 64000 || CandidateLimit is < 4 or > 20)
            throw new ArgumentException("Invalid model or bounded AI research budget.");
    }
}
public sealed record HypothesisOutput(string Rationale, StrategySpec[] Candidates);
public sealed record HypothesisProposal(string Id, string Model, DateTimeOffset ReceivedAt, string RequestHash,
    string RawHash, string RawJson, HypothesisOutput Output, int InputTokens, int OutputTokens);
public sealed record TrainingFeedback(StrategySpec Strategy, Metrics Metrics, string[] RiskEvents);
public sealed record AiRound(int Index, HypothesisProposal Proposal, TrainingFeedback[] Feedback);
public sealed record AiExploration(string Id, string CodeVersion, string TrainingHash, DateOnly TrainingStart, DateOnly TrainingEnd,
    AiSettings Settings, AiRound[] Rounds, string Status, string? ErrorKind, DateTimeOffset CreatedAt,
    string Note = "Training-only hypothesis exploration; not independent validation, paper eligibility or proof of market profitability.");

public sealed class OpenAiHypotheses(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    private const string Schema = """
        {"type":"object","additionalProperties":false,"required":["Rationale","Candidates"],"properties":{
        "Rationale":{"type":"string"},"Candidates":{"type":"array","items":{"type":"object","additionalProperties":false,
        "required":["Family","Lookback","Threshold","HoldDays","Version"],"properties":{
        "Family":{"type":"string","enum":["momentum","reversion"]},"Lookback":{"type":"integer"},
        "Threshold":{"type":"number"},"HoldDays":{"type":"integer"},"Version":{"type":"integer"}}}}}}
        """;
    public async Task<HypothesisProposal> Generate(string key, AiSettings settings, string trainingInput, CancellationToken ct = default)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new ArgumentException("Set OPENAI_API_KEY locally; never pass it as a CLI argument.");
        if (trainingInput.Length > settings.MaximumInputCharacters) throw new ArgumentException("AI input exceeds configured character budget.");
        using var schema = JsonDocument.Parse(Schema);
        var body = JsonSerializer.Serialize(new
        {
            model = settings.Model, store = false, max_output_tokens = settings.MaximumOutputTokens,
            instructions = $"Propose only JSON parameter hypotheses for the existing momentum and reversion algorithms. Return 4..{settings.CandidateLimit} unique versions, at least two per family. Use only supplied anonymized training data and feedback; no external knowledge, named securities, tools, code, orders or claims of future profits. Lookback 2..60, hold days 1..20, threshold 0..0.3, version positive. Avoid previously evaluated parameter IDs. Costs and risk cannot be changed; daily 1% is not an optimization target. Rationale is a falsifiable training hypothesis, not an investment recommendation.",
            input = trainingInput,
            text = new { format = new { type = "json_schema", name = "research_hypotheses", strict = true, schema = schema.RootElement.Clone() } }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenAI HTTP {(int)response.StatusCode}; no automatic retry or provider error-body logging.");
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (raw.Length > 1_000_000) throw new InvalidOperationException("OpenAI response too large.");
            using var document = JsonDocument.Parse(raw); var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "completed") throw new InvalidOperationException("AI response incomplete or failed; no candidate accepted.");
            var text = new StringBuilder();
            foreach (var item in root.GetProperty("output").EnumerateArray().Where(i => i.GetProperty("type").GetString() == "message"))
                foreach (var content in item.GetProperty("content").EnumerateArray())
                {
                    if (content.GetProperty("type").GetString() != "output_text") throw new InvalidOperationException("AI refused or returned unexpected content.");
                    text.Append(content.GetProperty("text").GetString());
                }
            var output = JsonSerializer.Deserialize<HypothesisOutput>(text.ToString()) ?? throw new InvalidOperationException("Empty AI hypotheses.");
            if (string.IsNullOrWhiteSpace(output.Rationale) || output.Rationale.Length > 8000 || output.Candidates == null || output.Candidates.Length < 4 || output.Candidates.Length > settings.CandidateLimit ||
                output.Candidates.Select(s => (s with { Version = 1 }).Id).Distinct().Count() != output.Candidates.Length || output.Candidates.GroupBy(s => s.Family).Count() < 2 || output.Candidates.GroupBy(s => s.Family).Any(g => g.Count() < 2))
                throw new InvalidOperationException("AI output violates registered candidate limits or family diversification.");
            foreach (var spec in output.Candidates)
            { spec.Validate(); if (spec.Lookback > 60 || spec.HoldDays > 20 || spec.Threshold > .3m) throw new InvalidOperationException("AI parameters outside bounded search space."); }
            var usage = root.GetProperty("usage"); var inputTokens = usage.GetProperty("input_tokens").GetInt32(); var outputTokens = usage.GetProperty("output_tokens").GetInt32();
            if (inputTokens < 0 || outputTokens < 0 || outputTokens > settings.MaximumOutputTokens) throw new InvalidOperationException("AI usage missing or exceeds output budget.");
            return new(root.GetProperty("id").GetString() ?? throw new InvalidOperationException("AI response ID missing."), settings.Model,
                clock?.Invoke() ?? DateTimeOffset.UtcNow, Hash(body), Hash(raw), raw, output, inputTokens, outputTokens);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("OpenAI network failure; key and provider exception redacted. No automatic retry."); }
        catch (TaskCanceledException) { throw new InvalidOperationException("OpenAI cancelled or timed out; no automatic retry."); }
    }
    internal static void ValidateProposal(HypothesisProposal proposal, AiSettings settings)
    {
        settings.Validate();
        if (proposal.Model != settings.Model || proposal.RawJson.Length > 1_000_000 || proposal.RawHash != Hash(proposal.RawJson) || proposal.InputTokens < 0 || proposal.OutputTokens < 0)
            throw new InvalidOperationException("AI raw response/model evidence differs.");
        using var document = JsonDocument.Parse(proposal.RawJson); var root = document.RootElement;
        if (root.GetProperty("status").GetString() != "completed" || root.GetProperty("id").GetString() != proposal.Id ||
            root.GetProperty("usage").GetProperty("input_tokens").GetInt32() != proposal.InputTokens ||
            root.GetProperty("usage").GetProperty("output_tokens").GetInt32() != proposal.OutputTokens || proposal.OutputTokens > settings.MaximumOutputTokens)
            throw new InvalidOperationException("AI response status/identity/usage differs.");
        var text = string.Concat(root.GetProperty("output").EnumerateArray().Where(i => i.GetProperty("type").GetString() == "message")
            .SelectMany(i => i.GetProperty("content").EnumerateArray()).Select(c => c.GetProperty("type").GetString() == "output_text" ?
                c.GetProperty("text").GetString() : throw new InvalidOperationException("Unexpected AI content.")));
        var parsed = JsonSerializer.Deserialize<HypothesisOutput>(text) ?? throw new InvalidOperationException("AI output missing.");
        if (parsed.Rationale != proposal.Output.Rationale || !parsed.Candidates.SequenceEqual(proposal.Output.Candidates))
            throw new InvalidOperationException("AI raw and normalized hypotheses differ.");
        if (parsed.Candidates.Length < 4 || parsed.Candidates.Length > settings.CandidateLimit ||
            parsed.Candidates.Select(s => (s with { Version = 1 }).Id).Distinct().Count() != parsed.Candidates.Length ||
            parsed.Candidates.GroupBy(s => s.Family).Count() < 2 || parsed.Candidates.GroupBy(s => s.Family).Any(g => g.Count() < 2))
            throw new InvalidOperationException("AI candidate registration differs.");
        foreach (var spec in parsed.Candidates)
        { spec.Validate(); if (spec.Lookback > 60 || spec.HoldDays > 20 || spec.Threshold > .3m) throw new InvalidOperationException("AI parameter bounds exceeded."); }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed class AiResearchWorker(string directory)
{
    public static Dataset TrainingData(Dataset data, ResearchPlan plan)
    {
        data.Validate(); plan.Validate();
        if (data.Dates.Length < plan.TrainSessions + plan.ValidationSessions + 2 * plan.TestSessions + plan.HoldoutSessions)
            throw new ArgumentException("AI exploration requires the research calendar; it never consumes reserved holdout.");
        var dates = data.Dates.Take(plan.TrainSessions).ToArray(); var set = dates.ToHashSet();
        // Source labels and full-data hashes are excluded from the model prompt.
        return new("TRAINING_ONLY", data.Synthetic, false, data.Bars.Where(b => set.Contains(b.Date)).ToArray(), dates);
    }
    public async Task<AiExploration> Run(Dataset data, ResearchPlan plan, Costs costs, Risk risk, AiSettings settings,
        string codeVersion, Func<string, CancellationToken, Task<HypothesisProposal>> generate, CancellationToken ct = default)
    {
        settings.Validate(); costs.Validate(); risk.Validate(); var training = TrainingData(data, plan);
        if (training.Bars.Any(b => b.AvailableAt > DateTimeOffset.UtcNow)) throw new ArgumentException("AI training contains observations not yet available.");
        var store = new EvidenceStore(directory); var id = Guid.NewGuid().ToString("N"); var rounds = new List<AiRound>();
        var status = "REQUEST_BUDGET_EXHAUSTED"; string? error = null; var seen = new HashSet<string>();
        var series = training.Bars.GroupBy(b => b.Ticker).OrderBy(g => g.Key, StringComparer.Ordinal).Select((g, index) =>
        {
            var bars = g.OrderBy(b => b.Date).ToArray(); var basePrice = bars[0].Close; var baseVolume = Math.Max(1, bars[0].Volume);
            return new { Symbol = "S" + index, CloseIndex = bars.Select(b => b.Close / basePrice).ToArray(), VolumeIndex = bars.Select(b => (decimal)b.Volume / baseVolume).ToArray() };
        }).ToArray();
        for (var index = 0; index < settings.MaximumRequests; index++)
        {
            if (ct.IsCancellationRequested) { status = "CANCELLED"; break; }
            var input = JsonSerializer.Serialize(new { TrainingOnly = true, Series = series, Costs = costs, Risk = risk,
                Feedback = rounds.SelectMany(r => r.Feedback).ToArray() });
            if (input.Length > settings.MaximumInputCharacters) { status = "INPUT_BUDGET_EXCEEDED"; break; }
            // A request attempt is immutable before network activity; an interrupted worker is never silently restarted.
            store.Save("ai-attempt", id + "-" + index, new { TrainingHash = training.Hash, settings, codeVersion, Index = index, Input = input, Time = DateTimeOffset.UtcNow });
            HypothesisProposal proposal;
            try { proposal = await generate(input, ct); OpenAiHypotheses.ValidateProposal(proposal, settings); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException or OperationCanceledException)
            { status = "GENERATION_FAILED"; error = ex.GetType().Name; break; }
            store.Save("ai-proposal", id + "-" + index, proposal);
            if (proposal.Output.Candidates.Any(s => !seen.Add((s with { Version = 1 }).Id))) { status = "REPEATED_CANDIDATE_REQUIRES_REVIEW"; break; }
            var feedback = proposal.Output.Candidates.Select(spec =>
            {
                var run = new BacktestEngine().Run(training, [spec], training.Dates[0], training.Dates[^1], costs, risk, codeVersion: codeVersion);
                var events = run.Events.Select(e => e.Contains("REGIME_CHANGE", StringComparison.Ordinal) ? "REGIME_CHANGE" :
                    e.Contains("RISK", StringComparison.Ordinal) ? "RISK_HALT" : "EXECUTION_EVENT").Distinct().ToArray();
                return new TrainingFeedback(spec, run.Metrics, events);
            }).ToArray();
            var round = new AiRound(index, proposal, feedback); store.Save("ai-round", id + "-" + index, round); rounds.Add(round);
        }
        var result = new AiExploration(id, codeVersion, training.Hash, training.Dates[0], training.Dates[^1], settings, rounds.ToArray(), status, error, DateTimeOffset.UtcNow);
        store.Save("ai-exploration", id, result); return result;
    }
}
