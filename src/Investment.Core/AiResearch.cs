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
        var knownAt = Clock.Open(data.Dates[plan.TrainSessions]).AddTicks(-1);
        // Source labels and full-data hashes are excluded from the model prompt.
        // Preserve the listing boundaries that explain gaps inside this prefix, without
        // including future lifecycle notices in its content hash or model input.
        var bars = data.Bars.Where(b => set.Contains(b.Date)).ToArray();
        if (bars.Any(b => b.AvailableAt > knownAt))
            throw new ArgumentException("AI training observations must be available before the next validation session opens.");
        var tickers = bars.Select(b => b.Ticker).ToHashSet(StringComparer.Ordinal);
        // A left-boundary delisting can belong to a ticker whose next listing occurs
        // wholly after training. It describes no security observations in this prefix.
        var events = data.LifecycleEvents?.Where(e => set.Contains(e.Date) && tickers.Contains(e.Ticker)).ToArray();
        var units = ShareUnitPrefix.Select(data, tickers, dates[^1], knownAt);
        var training = new Dataset("TRAINING_ONLY", data.Synthetic, false,
            bars, dates, events is { Length: > 0 } ? events : null, units.Changes, units.Credits);
        // Reject unusable training before a paid provider request or attempt is recorded.
        training.Validate();
        return training;
    }
    public async Task<AiExploration> Run(Dataset data, ResearchPlan plan, Costs costs, Risk risk, AiSettings settings,
        string codeVersion, Func<string, CancellationToken, Task<HypothesisProposal>> generate, CancellationToken ct = default)
    {
        settings.Validate(); costs.Validate(); risk.Validate(); var training = TrainingData(data, plan);
        if (training.Bars.Any(b => b.AvailableAt > DateTimeOffset.UtcNow)) throw new ArgumentException("AI training contains observations not yet available.");
        CostSensitivityRunner.Preflight(plan.CostStress, costs, training.Dates);
        var store = new EvidenceStore(directory); var id = Guid.NewGuid().ToString("N"); var rounds = new List<AiRound>();
        var status = "REQUEST_BUDGET_EXHAUSTED"; string? error = null; var seen = new HashSet<string>();
        var sessionOffsets = training.Dates.Select((date, offset) => (date, offset)).ToDictionary(x => x.date, x => x.offset);
        var trainingKnownAt = Clock.Open(data.Dates[plan.TrainSessions]).AddTicks(-1);
        var economicDate = training.Dates[^1];
        // Cost schedules can contain real dates, source labels and future policy. Expose
        // only the rates applied to this anonymous training calendar.
        var promptCosts = new { costs.Commission, costs.Slippage,
            SellTaxBySession = training.Dates.Select((date, offset) => new { SessionOffset = offset, Rate = costs.SellTaxOn(date) }).ToArray() };
        var listingStarts = (training.LifecycleEvents ?? []).Where(e => e.Kind == "LISTED")
            .Select(e => (e.Ticker, e.Date)).ToHashSet();
        var series = training.Bars.GroupBy(b => b.Ticker).OrderBy(g => g.Key, StringComparer.Ordinal).Select((g, index) =>
        {
            var episodes = new List<List<Bar>>(); var previousOffset = -2;
            foreach (var bar in g.OrderBy(b => b.Date))
            {
                var offset = sessionOffsets[bar.Date];
                if (offset != previousOffset + 1 || listingStarts.Contains((bar.Ticker, bar.Date))) episodes.Add([]);
                episodes[^1].Add(bar); previousOffset = offset;
            }
            return new { Symbol = "S" + index, Segments = episodes.Select(bars =>
            {
                // A reused ticker is not a continuous security price history. Reset both
                // normalizations and preserve the shared anonymous calendar in each episode.
                // A later reuse of the same ticker must not change an earlier listing's units.
                var episodeChanges = training.ShareUnitChanges?.Where(c => c.Ticker == bars[0].Ticker &&
                    c.EffectiveDate <= bars[^1].Date).ToArray();
                var comparison = ShareUnits.SignalHistory(bars.ToArray(), economicDate, trainingKnownAt, episodeChanges);
                var volumes = bars.Select(b => ShareUnits.CapacityVolume(b.Ticker, b.Date, economicDate,
                    b.Volume, trainingKnownAt, episodeChanges)).ToArray();
                var basePrice = comparison[0].Close; var baseVolume = volumes[0] > 0 ? volumes[0] : 1m;
                return new { SessionOffsets = bars.Select(b => sessionOffsets[b.Date]).ToArray(),
                    CloseIndex = comparison.Select(b => b.Close / basePrice).ToArray(),
                    VolumeIndex = volumes.Select(volume => volume / baseVolume).ToArray() };
            }).ToArray() };
        }).ToArray();
        for (var index = 0; index < settings.MaximumRequests; index++)
        {
            if (ct.IsCancellationRequested) { status = "CANCELLED"; break; }
            var input = JsonSerializer.Serialize(new { TrainingOnly = true, TrainingSessions = sessionOffsets.Count,
                SeriesSemantics = "SessionOffsets share a zero-based training exchange-session calendar. Each segment is a separate listing episode with independently normalized prices and volume, expressed in the final training session's economic share units using only terms known before validation opens. Raw observations are preserved separately. Never compute returns across segments or treat omitted sessions as consecutive observations.",
                Series = series, Costs = promptCosts, Risk = risk,
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
