using Investment.Core;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Investment.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RESEARCH_TEST_DB"))) Skip = "Set RESEARCH_TEST_DB to a dedicated disposable database."; }
}
public sealed class PostgresTests
{
    [PostgresFact]
    public async Task DatasetAndExperimentRoundTripAgainstRealPostgres()
    {
        var connection = Environment.GetEnvironmentVariable("RESEARCH_TEST_DB")!;
        await using var db = new ResearchDb(new DbContextOptionsBuilder<ResearchDb>().UseNpgsql(connection).Options);
        await db.Database.EnsureCreatedAsync(); await db.InstallEvidenceGuards();
        var data = DataFiles.Demo(35, Random.Shared.Next());
        await db.SaveDataset(data); await db.SaveDataset(data);
        var candidate = new StrategySpec("momentum", 5, .01m, 3);
        var run = new BacktestEngine().Run(data, [candidate], data.Dates[0], data.Dates[^1], new(), new(), codeVersion: "integration-test");
        await db.SaveExperiment(run.Id, data.Hash, "backtest", run.CodeVersion, run);
        Assert.Equal(data.Bars.Length, await db.Prices.CountAsync(p => p.DataHash == data.Hash));
        Assert.Equal(data.Bars[0].AvailableAt, (await db.Prices.FirstAsync(p => p.DataHash == data.Hash)).AvailableAt);
        var stored = await db.Experiments.SingleAsync(e => e.Id == run.Id);
        var restored = System.Text.Json.JsonSerializer.Deserialize<RunResult>(stored.Json)!;
        Assert.Equal(run.Metrics, restored.Metrics); Assert.Equal(run.Trades, restored.Trades);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"Experiments\" SET \"Kind\" = 'mutated' WHERE \"Id\" = {0}", run.Id));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Prices\" WHERE \"DataHash\" = {0}", data.Hash));
    }
}
