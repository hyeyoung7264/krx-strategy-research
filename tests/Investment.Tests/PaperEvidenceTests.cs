using Investment.Core;
using Xunit;

namespace Investment.Tests;

public sealed class PaperEvidenceTests
{
    private static (ExperimentArchive Archive, SourceSnapshot Source) Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        var source = SourceSnapshot.Capture(root?.FullName ?? throw new InvalidOperationException("Repository root missing."), typeof(ResearchAgent).Assembly);
        // Mock certification only to reach archive checks. Policy remains unreviewed; this is not market evidence.
        var seed = DataFiles.Demo(180);
        var data = seed with { Source = "TEST_FIXTURE_NOT_MARKET_EVIDENCE", Synthetic = false, PointInTimeCertified = true, Sessions = seed.Dates,
            SessionHours = seed.Dates.Select(d => new SessionHours(d, Clock.Open(d), Clock.Close(d),
                Clock.Open(d).AddDays(-1), "SYNTHETIC certified-branch session fixture")).ToArray() };
        var research = new ResearchAgent().Run(data, new BaselineHypotheses().Generate(), new(60, 30, 30, 30, 1, 30), new(), new(), source.Hash);
        return (new(1, data, source, Research: research), source);
    }
    [Theory]
    [InlineData("evaluation")]
    [InlineData("holdout")]
    [InlineData("folds")]
    [InlineData("metadata")]
    public void PaperStartRejectsModifiedResearchBeforeTrustingItsEligibilityLabel(string field)
    {
        var (archive, source) = Fixture(); var result = archive.Research!;
        Assert.True(archive.Reproduce(source).Matches); Assert.NotEqual("PAPER_ELIGIBLE", result.Evaluation.Decision);
        var changed = field switch
        {
            "evaluation" => result with { Evaluation = new("PAPER_ELIGIBLE", [], .01m) },
            "holdout" => result with { Holdout = result.Holdout with { Metrics = result.Holdout.Metrics with { TotalReturn = 1 } } },
            "folds" => result with { Folds = result.Folds.Select((f, i) => i == 0 ? f with { ValidationPassed = !f.ValidationPassed } : f).ToArray() },
            _ => result with { Synthetic = true }
        };
        var edited = archive with { Research = changed };
        var error = Assert.Throws<ArgumentException>(() => PaperEngine.Start([edited, edited], archive.Data, new(), new(), DateTimeOffset.UtcNow, source));
        Assert.Contains("reproduction failed", error.Message);
    }
    [Fact] public void FaithfullyReproducedRejectedResearchStillCannotStartPaper()
    {
        var (archive, source) = Fixture();
        var second = archive with { Research = archive.Research! with { Id = "second-fixture" } };
        var error = Assert.Throws<ArgumentException>(() => PaperEngine.Start([archive, second], archive.Data, new(), new(), DateTimeOffset.UtcNow, source));
        Assert.Contains("Ineligible", error.Message);
    }
    [Fact] public void PaperStartRequiresFullResearchInputsAndSourceBoundBinaryEvidence()
    {
        var (archive, source) = Fixture();
        var legacy = archive with { Source = source with { Binaries = [] } };
        var error = Assert.Throws<ArgumentException>(() => PaperEngine.Start([legacy, legacy], archive.Data, new(), new(), DateTimeOffset.UtcNow, source));
        Assert.Contains("source-bound binary evidence", error.Message);
        Assert.Throws<ArgumentException>(() => PaperEngine.Start([archive with { Research = null }, archive], archive.Data, new(), new(), DateTimeOffset.UtcNow, source));
        Assert.Throws<ArgumentException>(() => PaperEngine.Start([archive with { Data = archive.Data with { Source = "different" } }, archive], archive.Data, new(), new(), DateTimeOffset.UtcNow, source));
    }
}
