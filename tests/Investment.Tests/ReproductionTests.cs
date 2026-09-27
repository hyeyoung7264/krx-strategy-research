using Investment.Core;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Investment.Tests;

public sealed class ReproductionTests
{
    private static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }
    private static SourceSnapshot Source() => SourceSnapshot.Capture(Root(), typeof(ResearchAgent).Assembly);
    [Fact] public void FreshBinaryIsBoundToCurrentSourceAndArchiveDetectsMutation()
    {
        var source = Source(); source.ValidateArchive(); Assert.NotEmpty(source.Binaries!);
        var changed = source with { Files = source.Files.Select((f, i) => i == 0 ? f with { Content = f.Content + "changed" } : f).ToArray() };
        Assert.Throws<ArgumentException>(changed.ValidateArchive);
    }
    [Fact] public void StaleBuildManifestCannotCertifyChangedOrAddedSources()
    {
        var root = Path.Combine(Path.GetTempPath(), "build-proof-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            var paths = new[] { "src/A.cs", "Directory.Build.props", "Directory.Build.targets", "Investment.sln", "NuGet.Config" }.Select(p => Path.Combine(root, p)).ToArray();
            foreach (var path in paths) File.WriteAllText(path, "initial");
            var manifest = string.Join('\n', paths.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) + "|" + p));
            SourceSnapshot.ValidateBuildManifest(manifest, root);
            File.WriteAllText(paths[0], "changed");
            Assert.Throws<InvalidOperationException>(() => SourceSnapshot.ValidateBuildManifest(manifest, root));
            File.WriteAllText(paths[0], "initial"); File.WriteAllText(Path.Combine(root, "src", "B.cs"), "added");
            Assert.Throws<InvalidOperationException>(() => SourceSnapshot.ValidateBuildManifest(manifest, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact] public void BacktestReproductionChecksTradesCurveMetricsAndGross()
    {
        var source = Source(); var data = DataFiles.Demo(60); var spec = new StrategySpec("momentum", 5, .01m, 3);
        var run = new BacktestEngine().Run(data, [spec], data.Dates[0], data.Dates[^1], new(), new(), codeVersion: source.Hash);
        Assert.True(EvidenceReplay.Backtest(data, run, source, source).Matches);
        var changed = run with { Equity = run.Equity.Select((p, i) => i == 0 ? p with { Equity = p.Equity + 1 } : p).ToArray() };
        Assert.False(EvidenceReplay.Backtest(data, changed, source, source).Matches);
        Assert.Throws<ArgumentException>(() => EvidenceReplay.Backtest(data with { Source = "changed" }, run, source, source));
    }
    [Fact] public void LocaleDoesNotChangeSignalsOrExperimentEvents()
    {
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var data = DataFiles.Demo(60); var spec = new StrategySpec("momentum", 5, .01m, 3);
            RunResult Run() => new BacktestEngine().Run(data, [spec], data.Dates[0], data.Dates[^1], new(), new());
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US"); var english = Run();
            var history = data.Bars.Where(b => b.Ticker == "DEMO00").Take(20).ToArray(); var time = history[^1].AvailableAt;
            var englishSignal = new PriceStrategy(new("momentum", 2, 0, 2)).Generate(history, time);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR"); var french = Run();
            var frenchSignal = new PriceStrategy(new("momentum", 2, 0, 2)).Generate(history, time);
            Assert.Equal(english.Metrics, french.Metrics); Assert.Equal(english.Events, french.Events); Assert.Equal(englishSignal, frenchSignal);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
    }
    [Fact] public void ResearchArchiveRoundTripReproducesEveryFoldAndDecision()
    {
        var source = Source(); var data = DataFiles.Demo(180); var plan = new ResearchPlan(60, 30, 30, 30, 1, 30);
        var research = new ResearchAgent().Run(data, new BaselineHypotheses().Generate(), plan, new(), new(), source.Hash);
        var archive = new ExperimentArchive(1, data, source, Research: research);
        var decoded = JsonSerializer.Deserialize<ExperimentArchive>(JsonSerializer.Serialize(archive))!;
        Assert.True(decoded.Reproduce(source).Matches);
        var modified = research with { Evaluation = research.Evaluation with { Decision = "PAPER_ELIGIBLE" } };
        Assert.Contains("evaluation", EvidenceReplay.Research(data, modified, source, source).Differences);
        Assert.Throws<ArgumentException>(() => (archive with { SchemaVersion = 2 }).Reproduce(source));
    }
    [Fact] public async Task ConcurrentEvidenceWritesPublishExactlyOneCompleteDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), "atomic-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EvidenceStore(root);
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            { try { store.Save("race", "same", new { Sequence = i, Payload = new string('x', 10000) }); return true; } catch (IOException) { return false; } })));
            Assert.Equal(1, outcomes.Count(success => success));
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "race-same.json")));
            Assert.Equal(10000, result.RootElement.GetProperty("Payload").GetString()!.Length);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact] public void SerializationFailurePublishesNoBrokenEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "failed-evidence-" + Guid.NewGuid().ToString("N"));
        var value = new Dictionary<string, object>(); value["cycle"] = value;
        Assert.Throws<JsonException>(() => new EvidenceStore(root).Save("test", "cycle", value));
        Assert.False(Directory.Exists(root));
    }
}
