using Investment.Core;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

try
{
    string Option(string name, string fallback) { var i = Array.IndexOf(args, name); return i < 0 ? fallback : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : throw new ArgumentException($"Missing {name} value."); }
    T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new ArgumentException("Empty JSON.");
    void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, DataFiles.Json));
    var command = args.FirstOrDefault() ?? "help";
    var allowedOptions = new HashSet<string> { "--dataset", "--output", "--config", "--csv", "--source", "--start", "--end", "--corp-code", "--evidence", "--state", "--observation", "--persist", "--date", "--market", "--manifest", "--source-archive", "--archive", "--plan", "--max-requests", "--interval-seconds" };
    for (var i = 1; i < args.Length; i++)
    {
        if (!allowedOptions.Contains(args[i])) throw new ArgumentException("Unknown or duplicated positional argument; no live-order options exist.");
        if (args.Take(i).Contains(args[i])) throw new ArgumentException("Repeated option.");
        if (args[i] != "--persist") { if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing {args[i]} value."); i++; }
    }
    var output = Option("--output", "artifacts"); var store = new EvidenceStore(output);
    var sourceSnapshot = SourceSnapshot.Capture(Directory.GetCurrentDirectory(), typeof(ResearchAgent).Assembly, typeof(ResearchDb).Assembly, System.Reflection.Assembly.GetExecutingAssembly());
    var code = sourceSnapshot.Hash;
    var configPath = Option("--config", "config/research.json");
    if (!File.Exists(configPath)) throw new ArgumentException("Research configuration file not found; run from repository root.");
    var settings = Load<Settings>(configPath);
    settings.Costs.Validate(); settings.Risk.Validate(); settings.Plan.Validate();
    Dataset GetData() => command == "demo" ? DataFiles.Demo() : Load<Dataset>(Option("--dataset", "data/dataset.json"));
    ResearchDb Db()
    {
        var connection = Environment.GetEnvironmentVariable("RESEARCH_DB") ?? throw new ArgumentException("Set RESEARCH_DB environment variable; never put secrets in files/arguments.");
        return new(new DbContextOptionsBuilder<ResearchDb>().UseNpgsql(connection).Options);
    }
    string ApiKey(string name) => Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? throw new ArgumentException($"Set {name} as a local environment variable; never pass keys in CLI arguments.");
    switch (command)
    {
        case "reproduce":
        {
            var result = Load<ExperimentArchive>(Option("--archive", "")).Reproduce(sourceSnapshot);
            Print(new { path = store.Save("reproduction", Guid.NewGuid().ToString("N"), result), result.Matches, result.Differences, result.Note }); return result.Matches ? 0 : 2;
        }
        case "reproduce-research":
        {
            var result = EvidenceReplay.Research(GetData(), Load<ResearchResult>(Option("--evidence", "")), Load<SourceSnapshot>(Option("--source-archive", "")), sourceSnapshot);
            Print(new { path = store.Save("reproduction", Guid.NewGuid().ToString("N"), result), result.Matches, result.Differences, result.Note }); return result.Matches ? 0 : 2;
        }
        case "reproduce-backtest":
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Option("--evidence", "")));
            var run = doc.RootElement.TryGetProperty("Net", out var net) ? net.Deserialize<RunResult>()! : doc.RootElement.Deserialize<RunResult>()!;
            var result = EvidenceReplay.Backtest(GetData(), run, Load<SourceSnapshot>(Option("--source-archive", "")), sourceSnapshot);
            Print(new { path = store.Save("reproduction", Guid.NewGuid().ToString("N"), result), result.Matches, result.Differences, result.Note }); return result.Matches ? 0 : 2;
        }
        case "krx-fetch":
        {
            var key = ApiKey("KRX_API_KEY");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var result = await new KrxClient(http).Daily(key, Option("--market", "KOSPI"), DateOnly.Parse(Option("--date", "")));
            Print(new { path = store.Save("krx-raw", result.Id, result), Count = result.Rows.Length, Note = "Raw collection only; an empty result does not certify an exchange holiday." }); break;
        }
        case "krx-collect":
        {
            var plan = Load<KrxCollectionPlan>(Option("--plan", ""));
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var client = new KrxClient(http);
            var result = await new KrxCollector(Path.Combine(output, "krx-collections")).Run(plan,
                int.Parse(Option("--max-requests", "5"), System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(double.Parse(Option("--interval-seconds", "1"), System.Globalization.CultureInfo.InvariantCulture)),
                (market, date, ct) => client.Daily(ApiKey("KRX_API_KEY"), market, date, ct));
            Print(new { Path = Path.GetFullPath(Path.Combine(output, "krx-collections", result.PlanHash, "collection-" + result.Id + ".json")),
                Receipt = result, Note = "Unreviewed raw collection only. Empty responses require review; no automatic retry or calendar inference." });
            return result.Status is "REQUEST_FAILED" or "CANCELLED" or "EMPTY_RESPONSE_REQUIRES_REVIEW" ? 2 : 0;
        }
        case "krx-build":
        {
            var manifest = Load<KrxManifest>(Option("--manifest", ""));
            var data = KrxDatasetBuilder.Build(manifest.SnapshotFiles.Select(Load<KrxSnapshot>).ToArray(), manifest);
            Print(new { path = store.Save("dataset", data.Hash, data), data.PointInTimeCertified, Note = "Review corporate actions, historical universe, sector classification, publication timing and session calendar before certification." }); break;
        }
        case "dart-disclosures":
        {
            var key = ApiKey("OPENDART_API_KEY");
            var start = DateOnly.Parse(Option("--start", DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd")));
            var end = DateOnly.Parse(Option("--end", DateTime.Today.ToString("yyyy-MM-dd")));
            var corp = Option("--corp-code", ""); using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var result = await new OpenDartClient(http).Search(key, start, end, corp.Length == 0 ? null : corp);
            Print(new { path = store.Save("dart-disclosures", result.Id, result), Count = result.Disclosures.Length }); break;
        }
        case "dart-company":
        {
            var key = ApiKey("OPENDART_API_KEY");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var result = await new OpenDartClient(http).Company(key, Option("--corp-code", ""));
            Print(new { path = store.Save("dart-company", Guid.NewGuid().ToString("N"), result), result.Company, result.StockCode }); break;
        }
        case "demo":
        case "research":
        {
            var data = GetData(); data.Validate();
            if (!data.Synthetic)
            {
                // One final holdout access per immutable dataset in this workspace. Preserve reservation even on failure.
                new EvidenceStore("data/private/holdout-seals").Save("seal", data.Hash, new { data.Hash, settings, CodeVersion = code, Time = DateTimeOffset.UtcNow });
            }
            var result = new ResearchAgent().Run(data, settings.Candidates, settings.Plan, settings.Costs, settings.Risk, code);
            var archivePath = store.Save("archive", result.Id, new ExperimentArchive(1, data, sourceSnapshot, Research: result));
            var path = store.Save("research", result.Id, result);
            var reportPath = Path.Combine(output, $"research-{result.Id}.md");
            using (var report = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))) report.Write(ReportWriter.Markdown(result));
            store.Save("dataset", data.Hash + "-" + result.Id, data);
            store.Save("source", code + "-" + result.Id, sourceSnapshot);
            if (args.Contains("--persist"))
            {
                await using var db = Db(); await db.SaveDataset(data); await db.SaveExperiment(result.Id, data.Hash, "research", code, result);
            }
            Print(new { path, archivePath, reportPath, result.Synthetic, result.Evaluation, Holdout = result.Holdout.Metrics, Folds = result.Folds.Length,
                Note = "Backtest/replay only. No forward paper evidence. Daily 1% is a measurement target, never a gate." }); break;
        }
        case "import-csv":
        {
            var path = Option("--csv", ""); var source = Option("--source", "");
            var data = DataFiles.ReadCsv(path, source); // Certification requires a separately reviewed manifest.
            Print(new { path = store.Save("dataset", data.Hash, data), data.Hash, data.PointInTimeCertified }); break;
        }
        case "backtest":
        {
            var data = GetData(); var dates = data.Dates;
            var start = DateOnly.Parse(Option("--start", dates[0].ToString("yyyy-MM-dd")));
            var end = DateOnly.Parse(Option("--end", dates[^1].ToString("yyyy-MM-dd")));
            var engine = new BacktestEngine();
            var net = engine.Run(data, settings.Candidates, start, end, settings.Costs, settings.Risk, codeVersion: code);
            var gross = engine.Run(data, settings.Candidates, start, end, new Costs(0, 0, 0), settings.Risk, codeVersion: code);
            var archivePath = store.Save("archive", net.Id, new ExperimentArchive(1, data, sourceSnapshot, Backtest: net, GrossBacktest: gross));
            var path = store.Save("backtest", net.Id, new { Net = net, Gross = gross });
            store.Save("source", code + "-" + net.Id, sourceSnapshot);
            store.Save("dataset", data.Hash + "-" + net.Id, data);
            Print(new { Net = net.Metrics, Gross = gross.Metrics, Path = path, ArchivePath = archivePath,
                Note = "Gross and net are separate simulations; costs may alter sizing, stops and risk halts." }); break;
        }
        case "paper-start":
        {
            var evidencePaths = Option("--evidence", "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            var archives = evidencePaths.Select(path =>
            {
                var archive = Load<ExperimentArchive>(path);
                if (archive.Research == null) throw new ArgumentException("paper-start --evidence requires complete archive-ID.json files, not standalone research JSON.");
                return archive;
            }).ToArray();
            var state = PaperEngine.Start(archives, GetData(), settings.Costs, settings.Risk, DateTimeOffset.UtcNow, sourceSnapshot);
            new PaperJournal("data/private/paper-journal").Start(state);
            Print(new { path = store.Save("paper", state.SessionId + "-0", state), state.SessionId }); break;
        }
        case "paper-step":
        {
            var before = Load<PaperState>(Option("--state", "")); var observation = Load<Observation>(Option("--observation", ""));
            var after = new PaperJournal("data/private/paper-journal").Commit(before, observation, DateTimeOffset.UtcNow, code);
            Print(new { path = store.Save("paper", after.SessionId + "-" + after.Audit.Length, after), after.Halted, after.Cash }); break;
        }
        case "paper-recover":
        {
            var after = new PaperJournal("data/private/paper-journal").Recover(Load<PaperState>(Option("--state", "")));
            Print(new { path = store.Save("paper-recovered", after.SessionId + "-" + after.Audit.Length + "-" + Guid.NewGuid().ToString("N"), after), after.CodeVersion,
                Note = "Exports an already committed state; no observation replay or repeated trade execution." }); break;
        }
        case "paper-evaluate":
        {
            var state = Load<PaperState>(Option("--state", ""));
            Print(new PaperJournal("data/private/paper-journal").Evaluate(state, code)); break;
        }
        case "db-schema":
        {
            // Generate reviewable SQL without connecting. Use db-init only for a dedicated empty database.
            await using var db = new ResearchDb(new DbContextOptionsBuilder<ResearchDb>().UseNpgsql("Host=localhost;Database=research").Options);
            Directory.CreateDirectory(output); var path = Path.Combine(output, "schema.sql"); File.WriteAllText(path, db.Database.GenerateCreateScript()); Print(new { path }); break;
        }
        case "db-check":
        {
            await using var db = Db(); Print(new { Connected = await db.Database.CanConnectAsync(), DataRevisions = await db.DataRevisions.CountAsync(), Experiments = await db.Experiments.CountAsync() }); break;
        }
        case "db-init":
        {
            await using var db = Db(); var created = await db.Database.EnsureCreatedAsync(); await db.InstallEvidenceGuards(); Print(new { Created = created }); break;
        }
        case "help":
            Console.WriteLine("""
                Commands:
                  demo | import-csv --csv PATH --source SOURCE
                  backtest --dataset JSON | research --dataset JSON [--persist]
                  reproduce --archive JSON
                  reproduce-research --dataset JSON --evidence JSON --source-archive JSON
                  reproduce-backtest --dataset JSON --evidence JSON --source-archive JSON
                  paper-start --dataset JSON --evidence 'archive-A.json;archive-B.json'
                  paper-step --state JSON --observation JSON | paper-recover --state JSON
                  paper-evaluate --state JSON
                  krx-fetch --market KOSPI|KOSDAQ --date DATE | krx-build --manifest JSON
                  krx-collect --plan JSON [--max-requests 5] [--interval-seconds 1]
                  dart-disclosures --start DATE --end DATE [--corp-code CODE] | dart-company --corp-code CODE
                  db-schema | db-init | db-check
                Source is checked against the compiled binary and archived automatically. Execution is virtual.
                """); break;
        default: throw new ArgumentException("Unknown command; use help. No live execution command exists.");
    }
    return 0;
}
catch (Exception ex)
{
    // Do not leak connection strings or provider exception text into logs.
    Console.Error.WriteLine(ex is ArgumentException or InvalidOperationException && ex.GetType().Namespace == "System" ? ex.Message : $"Operation failed ({ex.GetType().Name}); inspect locally without exposing secrets.");
    return 1;
}

internal sealed record Settings(Costs Costs, Risk Risk, ResearchPlan Plan, StrategySpec[] Candidates);
