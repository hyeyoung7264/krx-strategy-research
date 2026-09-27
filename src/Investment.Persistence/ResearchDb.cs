using Investment.Core;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Investment.Persistence;

public sealed class DataRevision
{
    public required string Hash { get; set; }
    public required string Source { get; set; }
    public bool Synthetic { get; set; }
    public bool PointInTimeCertified { get; set; }
    public required string Json { get; set; }
}
public sealed class ExperimentRecord
{
    public required string Id { get; set; }
    public required string DataHash { get; set; }
    public required string Kind { get; set; }
    public required string CodeVersion { get; set; }
    public required string Json { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class PriceRecord
{
    public required string DataHash { get; set; }
    public required string Ticker { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public decimal TradingValue { get; set; }
    public required string Sector { get; set; }
    public bool Tradable { get; set; }
    public bool Member { get; set; }
}
public sealed class ResearchDb(DbContextOptions<ResearchDb> options) : DbContext(options)
{
    public DbSet<DataRevision> DataRevisions => Set<DataRevision>();
    public DbSet<PriceRecord> Prices => Set<PriceRecord>();
    public DbSet<ExperimentRecord> Experiments => Set<ExperimentRecord>();
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<DataRevision>().HasKey(e => e.Hash);
        m.Entity<DataRevision>().Property(e => e.Json).HasColumnType("jsonb");
        m.Entity<PriceRecord>().HasKey(e => new { e.DataHash, e.Ticker, e.Date });
        m.Entity<PriceRecord>().HasOne<DataRevision>().WithMany().HasForeignKey(e => e.DataHash).OnDelete(DeleteBehavior.Restrict);
        foreach (var p in new[] { "Open", "High", "Low", "Close", "TradingValue" }) m.Entity<PriceRecord>().Property<decimal>(p).HasPrecision(28, 8);
        m.Entity<ExperimentRecord>().HasKey(e => e.Id);
        m.Entity<ExperimentRecord>().Property(e => e.Json).HasColumnType("jsonb");
        m.Entity<ExperimentRecord>().HasOne<DataRevision>().WithMany().HasForeignKey(e => e.DataHash).OnDelete(DeleteBehavior.Restrict);
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { RejectMutation(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { RejectMutation(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
    private void RejectMutation()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries().Any(e => e.State is EntityState.Modified or EntityState.Deleted)) throw new InvalidOperationException("Evidence is append-only; create a new revision.");
    }
    public async Task SaveDataset(Dataset data)
    {
        data.Validate();
        if (await DataRevisions.AnyAsync(e => e.Hash == data.Hash)) return;
        DataRevisions.Add(new() { Hash = data.Hash, Source = data.Source, Synthetic = data.Synthetic, PointInTimeCertified = data.PointInTimeCertified, Json = JsonSerializer.Serialize(data) });
        Prices.AddRange(data.Bars.Select(b => new PriceRecord { DataHash = data.Hash, Ticker = b.Ticker, Date = b.Date,
            AvailableAt = b.AvailableAt.ToUniversalTime(), Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
            Volume = b.Volume, TradingValue = b.TradingValue, Sector = b.Sector, Tradable = b.Tradable, Member = b.Member }));
        await SaveChangesAsync();
    }
    public async Task SaveExperiment<T>(string id, string hash, string kind, string code, T value)
    {
        Experiments.Add(new() { Id = id, DataHash = hash, Kind = kind, CodeVersion = code, CreatedAt = DateTimeOffset.UtcNow, Json = JsonSerializer.Serialize(value) });
        await SaveChangesAsync();
    }

    public Task InstallEvidenceGuards() => Database.ExecuteSqlRawAsync("""
        CREATE OR REPLACE FUNCTION reject_evidence_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'Research evidence is append-only'; END; $$;
        DO $$ BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'immutable_data_revisions') THEN
            CREATE TRIGGER immutable_data_revisions BEFORE UPDATE OR DELETE OR TRUNCATE ON "DataRevisions" FOR EACH STATEMENT EXECUTE FUNCTION reject_evidence_mutation();
          END IF;
          IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'immutable_prices') THEN
            CREATE TRIGGER immutable_prices BEFORE UPDATE OR DELETE OR TRUNCATE ON "Prices" FOR EACH STATEMENT EXECUTE FUNCTION reject_evidence_mutation();
          END IF;
          IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'immutable_experiments') THEN
            CREATE TRIGGER immutable_experiments BEFORE UPDATE OR DELETE OR TRUNCATE ON "Experiments" FOR EACH STATEMENT EXECUTE FUNCTION reject_evidence_mutation();
          END IF;
        END $$;
        """);
}
