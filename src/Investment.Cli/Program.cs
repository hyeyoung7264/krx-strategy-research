using Investment.Core;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

try
{
    string Option(string name, string fallback) { var i = Array.IndexOf(args, name); return i < 0 ? fallback : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : throw new ArgumentException($"Missing {name} value."); }
    T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new ArgumentException("Empty JSON.");
    void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, DataFiles.Json));
    DateTimeOffset Timestamp(string name)
    {
        var value = Option(name, "");
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z") ||
            !DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var timestamp))
            throw new ArgumentException($"{name} requires a full ISO date and time with UTC Z or a numeric offset.");
        return timestamp;
    }
    var command = args.FirstOrDefault() ?? "help";
    var allowedOptions = new HashSet<string> { "--dataset", "--output", "--config", "--csv", "--source", "--start", "--end", "--corp-code", "--evidence", "--state", "--observation", "--persist", "--date", "--year", "--market", "--manifest", "--source-archive", "--archive", "--plan", "--max-requests", "--interval-seconds", "--ai-config", "--cutoff", "--recorded-cutoff", "--timing" };
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
    Dataset GetData() => command is "demo" or "cohort-demo" ? DataFiles.Demo() : Load<Dataset>(Option("--dataset", "data/dataset.json"));
    ResearchDb Db()
    {
        var connection = Environment.GetEnvironmentVariable("RESEARCH_DB") ?? throw new ArgumentException("Set RESEARCH_DB environment variable; never put secrets in files/arguments.");
        return new(new DbContextOptionsBuilder<ResearchDb>().UseNpgsql(connection).Options);
    }
    string ApiKey(string name) => Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? throw new ArgumentException($"Set {name} as a local environment variable; never pass keys in CLI arguments.");
    switch (command)
    {
        case "ai-explore":
        case "ai-cohort":
        {
            var ai = Load<AiSettings>(Option("--ai-config", "config/ai.example.json")); ai.Validate();
            var key = ApiKey("OPENAI_API_KEY"); var data = GetData();
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            var client = new OpenAiHypotheses(http);
            var result = await new AiResearchWorker(output).Run(data, settings.Plan, settings.Costs, settings.Risk, ai, code,
                (input, ct) => client.Generate(key, ai, input, ct));
            if (command == "ai-cohort" && result.Status == "REQUEST_BUDGET_EXHAUSTED" && result.Rounds.Length > 0)
            {
                var candidates = result.Rounds.SelectMany(r => r.Proposal.Output.Candidates).ToArray();
                if (!data.Synthetic)
                    new HoldoutRegistry("data/private/holdout-seals").Reserve(data, candidates, settings.Plan, settings.Costs, settings.Risk, code, "cohort", DateTimeOffset.UtcNow);
                var cohort = new CohortAgent().Run(data, candidates, settings.Plan, settings.Costs, settings.Risk, code,
                    result.Rounds.Max(r => r.Proposal.ReceivedAt));
                var archivePath = store.Save("archive", cohort.Id, new ExperimentArchive(2, data, sourceSnapshot, Cohort: cohort, Ai: result));
                Print(new { archivePath, cohort.Evaluation, cohort.DeclaredHypotheses, Note = "All generated candidates are counted; AI-created historical forward windows cannot establish prospective eligibility." }); break;
            }
            Print(new { result.Id, result.Status, Rounds = result.Rounds.Length, result.Note });
            return result.Status is "GENERATION_FAILED" or "INPUT_BUDGET_EXCEEDED" or "REPEATED_CANDIDATE_REQUIRES_REVIEW" ? 2 : 0;
        }
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
        case "krx-basic-fetch":
        {
            var key = ApiKey("KRX_API_KEY");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var result = await new KrxReferenceClient(http).BasicInfo(key, Option("--market", "KOSPI"), DateOnly.Parse(Option("--date", "")));
            Print(new { path = store.Save("krx-basic-raw", result.Id, result), Count = result.Rows.Length,
                Note = "Unreviewed reference data only; listing section is not an industry and an empty response is not a certified holiday." }); break;
        }
        case "krx-index-fetch":
        {
            var key = ApiKey("KRX_API_KEY");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var result = await new KrxReferenceClient(http).IndexDaily(key, Option("--market", "KOSPI"), DateOnly.Parse(Option("--date", "")));
            Print(new { path = store.Save("krx-index-raw", result.Id, result), Count = result.Rows.Length,
                Note = "Unreviewed reference data only; select a declared index before comparing strategy returns." }); break;
        }
        case "krx-collect":
        {
            var plan = Load<KrxCollectionPlan>(Option("--plan", ""));
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var client = new KrxClient(http);
            var reference = new KrxReferenceClient(http);
            var collector = new KrxCollector(Path.Combine(output, "krx-collections"));
            var budget = int.Parse(Option("--max-requests", "5"), System.Globalization.CultureInfo.InvariantCulture);
            var interval = TimeSpan.FromSeconds(double.Parse(Option("--interval-seconds", "1"), System.Globalization.CultureInfo.InvariantCulture));
            var result = plan.Service switch
            {
                null or "DAILY" => await collector.Run(plan, budget, interval,
                    (market, date, ct) => client.Daily(ApiKey("KRX_API_KEY"), market, date, ct)),
                "BASIC" => await collector.RunBasic(plan, budget, interval,
                    (market, date, ct) => reference.BasicInfo(ApiKey("KRX_API_KEY"), market, date, ct)),
                "INDEX" => await collector.RunIndex(plan, budget, interval,
                    (market, date, ct) => reference.IndexDaily(ApiKey("KRX_API_KEY"), market, date, ct)),
                _ => throw new ArgumentException("Collection service must be DAILY, BASIC or INDEX.")
            };
            Print(new { Path = Path.GetFullPath(Path.Combine(output, "krx-collections", result.PlanHash, "collection-" + result.Id + ".json")),
                Service = plan.Service ?? "DAILY", Receipt = result,
                Note = "Unreviewed raw collection only. Empty responses require review; no automatic retry, calendar inference, historical publication certification, or cross-service dataset merge." });
            return result.Status is "REQUEST_FAILED" or "CANCELLED" or "EMPTY_RESPONSE_REQUIRES_REVIEW" ? 2 : 0;
        }
        case "corporate-append":
        {
            var manifest = Load<CorporateActionImport>(Option("--manifest", ""));
            var prior = Option("--state", "");
            var ledger = prior.Length == 0 ? new CorporateActionLedger([], []) : Load<CorporateActionLedger>(prior);
            var next = ledger.Append(manifest.Revision, manifest.SnapshotFiles.Select(Load<KindNoticeSnapshot>).ToArray(), DateTimeOffset.UtcNow);
            Print(new { path = store.Save("corporate-ledger", next.Hash, next), next.Hash,
                Note = "Append-only interpretation evidence; no historical completeness, accounting application, point-in-time certification, or paper eligibility." });
            break;
        }
        case "corporate-at":
        {
            var ledger = Load<CorporateActionLedger>(Option("--state", ""));
            var result = ledger.At(Timestamp("--cutoff"), Timestamp("--recorded-cutoff"),
                Option("--timing", "OBSERVED"), DateTimeOffset.UtcNow);
            Print(new { path = store.Save("corporate-view", Guid.NewGuid().ToString("N"), result), result.Hash,
                Count = result.Actions.Length, result.RetrospectiveTiming, result.Note });
            break;
        }
        case "kind-notice":
        {
            using var client = new KindNoticeClient();
            var snapshot = await client.Fetch(Option("--source", ""));
            snapshot.Validate(DateTimeOffset.UtcNow);
            Print(new { path = store.Save("kind-notice", Guid.NewGuid().ToString("N"), snapshot), snapshot.Source,
                snapshot.ObservedAt, snapshot.RawHash,
                Note = "Exact response bytes observed now; no original publication timestamp, interpreted terms, accounting application, or certification." });
            break;
        }
        case "kind-delistings":
        {
            var query = new KindDelistingQuery(
                DateOnly.Parse(Option("--start", ""), System.Globalization.CultureInfo.InvariantCulture),
                DateOnly.Parse(Option("--end", ""), System.Globalization.CultureInfo.InvariantCulture),
                Option("--market", "ALL"));
            using var client = new KindDelistingClient();
            var pages = new List<string>();
            var snapshot = await client.Fetch(query,
                maxPages: int.Parse(Option("--max-requests", "5"), System.Globalization.CultureInfo.InvariantCulture),
                interval: TimeSpan.FromSeconds(double.Parse(Option("--interval-seconds", "1"), System.Globalization.CultureInfo.InvariantCulture)),
                preservePage: page =>
                {
                    pages.Add(store.Save("kind-delisting-page", Guid.NewGuid().ToString("N"), page));
                    return Task.CompletedTask;
                });
            snapshot.Validate(DateTimeOffset.UtcNow);
            Print(new { path = store.Save("kind-delistings", snapshot.Id, new { CodeVersion = code, Snapshot = snapshot, PageFiles = pages }),
                Pages = snapshot.Pages.Length, Rows = snapshot.Pages.Sum(page => page.Rows.Length),
                Note = "Retrospective official listing screen only. Issuer IDs are not tickers/ISINs; no original disclosure time, execution eligibility, settlement proceeds, or promotion certification." });
            break;
        }
        case "krx-calendar":
        {
            var year = int.Parse(Option("--year", ""), System.Globalization.CultureInfo.InvariantCulture);
            using var client = new KrxCalendarClient();
            var snapshot = await client.Year(year);
            var from = DateOnly.Parse(Option("--start", $"{year}-01-01"), System.Globalization.CultureInfo.InvariantCulture);
            var through = DateOnly.Parse(Option("--end", $"{year}-12-31"), System.Globalization.CultureInfo.InvariantCulture);
            var dates = KrxCalendarClient.CandidateSessions(snapshot, from, through);
            var snapshotPath = store.Save("krx-calendar", snapshot.Id, snapshot);
            var candidatesPath = store.Save("krx-calendar-candidates", snapshot.Id, new { SnapshotPath = snapshotPath, From = from, Through = through, CandidateDates = dates,
                Note = "Public holiday-screen candidates only; not certified historical publication timing, settlement dates, or intraday opening hours." });
            Print(new { snapshotPath, candidatesPath, Holidays = snapshot.Holidays.Length, CandidateSessions = dates.Length });
            break;
        }
        case "krx-audit":
        {
            var plan = Load<KrxAuditPlan>(Option("--plan", ""));
            var report = KrxDataAudit.Run(plan.SnapshotFiles.Select(Load<KrxSnapshot>).ToArray(), plan.ExpectedDates, plan.Markets, DateTimeOffset.UtcNow);
            Print(new { path = store.Save("krx-audit", report.Id, new { CodeVersion = code, Report = report }), report.Status,
                Snapshots = report.Inputs.Length, Rows = report.Days.Sum(d => d.Rows), NoTradeRows = report.Days.Sum(d => d.NoTradeRows),
                Issues = report.Issues.Length, Changes = report.Changes.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count()), report.Note });
            break;
        }
        case "krx-reference-audit":
        {
            var plan = Load<KrxReferenceAuditPlan>(Option("--plan", ""));
            var report = KrxReferenceAudit.Run(plan, DateTimeOffset.UtcNow);
            Print(new { path = store.Save("krx-reference-audit", report.Id, new { CodeVersion = code, Report = report }),
                report.Status, Snapshots = report.Inputs.Length, MarketDays = report.Days.Length,
                KnownPriceRows = report.Days.Sum(d => d.PriceRows ?? 0), KnownBasicRows = report.Days.Sum(d => d.BasicRows ?? 0),
                KnownIndexRows = report.Days.Sum(d => d.IndexRows ?? 0), Issues = report.Issues.Length,
                IdentityChanges = report.IdentityChanges.Length, report.Note });
            break;
        }
        case "krx-review-queue":
        {
            var plan = Load<KrxReviewQueuePlan>(Option("--plan", ""));
            if (plan.PriceAuditFiles == null || plan.ReferenceAuditFiles == null)
                throw new ArgumentException("Both review report file lists are required.");
            var paths = plan.PriceAuditFiles.Concat(plan.ReferenceAuditFiles).ToArray();
            if (paths.Length is < 1 or > KrxReviewQueue.MaximumReports)
                throw new ArgumentException("Review input exceeds the bounded report count.");
            long fileBytes = 0;
            string ReadReport(string path)
            {
                using var stream = File.OpenRead(path);
                var length = stream.Length;
                if (length > KrxReviewQueue.MaximumInputBytes - fileBytes)
                    throw new ArgumentException("Review input exceeds the bounded file size.");
                fileBytes += length;
                var bytes = new byte[checked((int)length)];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1)
                    throw new ArgumentException("Review input changed while being read.");
                var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
                return new System.Text.UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            var report = KrxReviewQueue.Build(plan.PriceAuditFiles.Select(ReadReport).ToArray(),
                plan.ReferenceAuditFiles.Select(ReadReport).ToArray(), DateTimeOffset.UtcNow);
            Print(new { path = store.Save("krx-review-queue", report.Id, new { CodeVersion = code, Report = report }),
                report.Hash, report.Status, Sources = report.Sources.Length, Buckets = report.Buckets.Length, report.Note });
            break;
        }
        case "krx-build":
        {
            var manifest = Load<KrxManifest>(Option("--manifest", ""));
            var data = KrxDatasetBuilder.Build(manifest.SnapshotFiles.Select(Load<KrxSnapshot>).ToArray(), manifest);
            Print(new { path = store.Save("dataset", data.Hash, data), data.PointInTimeCertified, Note = "Review corporate actions, historical universe, sector classification, publication timing and session calendar before certification." }); break;
        }
        case "dart-collect":
        {
            var plan = Load<DartCollectionPlan>(Option("--plan", ""));
            var sourceArchivePath = store.Save("dart-source", Guid.NewGuid().ToString("N"), sourceSnapshot);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var client = new OpenDartClient(http);
            var result = await new DartCollector(Path.Combine(output, "dart-collections")).Run(plan,
                int.Parse(Option("--max-requests", "3"), System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(double.Parse(Option("--interval-seconds", "1"), System.Globalization.CultureInfo.InvariantCulture)), code,
                (query, ct) => client.Facts(ApiKey("OPENDART_API_KEY"), query, ct));
            var path = store.Save("dart-run", result.Id, new { SourceArchivePath = sourceArchivePath, Receipt = result });
            Print(new { path, result.Status, result.Attempts, result.HttpStatus, Snapshots = result.SnapshotFiles.Length,
                Pending = result.Pending.Length, result.Note }); return result.Status is "REQUEST_FAILED" or "CANCELLED" ? 2 : 0;
        }
        case "dart-context":
        {
            var plan = Load<DartContextPlan>(Option("--plan", ""));
            var facts = DartResearchIndex.At(plan.SnapshotFiles.Select(Load<DartFactSnapshot>).ToArray(),
                plan.DisclosureFiles.Select(Load<DisclosureBatch>).ToArray(), plan.CorpCode, plan.Cutoff, DateTimeOffset.UtcNow);
            var id = Guid.NewGuid().ToString("N");
            var path = store.Save("dart-context", id, new { Id = id, CodeVersion = code, plan.CorpCode, plan.Cutoff, Facts = facts,
                Note = "Research inputs only; unlinked or not-yet-observed facts excluded. No trading signal or return evidence." });
            Print(new { path, Count = facts.Length }); break;
        }
        case "dart-check":
        {
            var key = ApiKey("OPENDART_API_KEY"); var id = Guid.NewGuid().ToString("N");
            var corp = Option("--corp-code", "00126380");
            if (key.Length != 40 || !key.All(char.IsAsciiLetterOrDigit) || corp.Length != 8 || !corp.All(char.IsAsciiDigit))
                throw new ArgumentException("A local 40-character OpenDART key and eight-digit company code are required.");
            store.Save("dart-attempt", id, new { Id = id, CorpCode = corp, StartedAt = DateTimeOffset.UtcNow, MaximumRequests = 1 });
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var result = await DartConnection.Check(http, key, corp, id);
            Print(new { path = store.Save("dart-check", id, result), result.Status, result.Authenticated, result.HttpStatus, result.ApiStatus, result.RedirectClass });
            return result.Authenticated ? 0 : 2;
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
        case "cohort-demo":
        case "cohort-research":
        {
            var data = GetData(); data.Validate();
            if (!data.Synthetic)
                new HoldoutRegistry("data/private/holdout-seals").Reserve(data, settings.Candidates, settings.Plan, settings.Costs, settings.Risk, code, "cohort", DateTimeOffset.UtcNow);
            var result = new CohortAgent().Run(data, settings.Candidates, settings.Plan, settings.Costs, settings.Risk, code);
            var archivePath = store.Save("archive", result.Id, new ExperimentArchive(2, data, sourceSnapshot, Cohort: result));
            var path = store.Save("cohort", result.Id, result);
            var reportPath = Path.Combine(output, $"cohort-{result.Id}.md");
            using (var report = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))) report.Write(ReportWriter.Markdown(result));
            store.Save("dataset", data.Hash + "-" + result.Id, data);
            store.Save("source", code + "-" + result.Id, sourceSnapshot);
            if (args.Contains("--persist"))
            {
                await using var db = Db(); await db.SaveDataset(data); await db.SaveExperiment(result.Id, data.Hash, "cohort", code, result);
            }
            Print(new { path, archivePath, reportPath, result.Synthetic, result.Evaluation, result.DeclaredHypotheses,
                Families = result.Families.Select(f => new { Family = f.Holdout.Strategies.Single().Family, f.Evaluation }),
                Portfolio = result.Holdout.Metrics, Folds = result.Folds.Length,
                Note = "One preregistered family-and-portfolio cohort; no independent evidence is created by repeating holdout calculations." }); break;
        }
        case "demo":
        case "research":
        {
            var data = GetData(); data.Validate();
            if (!data.Synthetic)
            {
                // Preserve all reserved sessions even on failure; data/hash changes cannot unlock inspected holdout.
                new HoldoutRegistry("data/private/holdout-seals").Reserve(data, settings.Candidates, settings.Plan, settings.Costs, settings.Risk, code, "single", DateTimeOffset.UtcNow);
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
                if (archive.Research == null && archive.Cohort == null) throw new ArgumentException("paper-start --evidence requires a complete cohort archive, not standalone result JSON.");
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
                  cohort-demo | cohort-research --dataset JSON [--persist]
                  ai-explore --dataset JSON --ai-config JSON | ai-cohort --dataset JSON --ai-config JSON
                  reproduce --archive JSON
                  reproduce-research --dataset JSON --evidence JSON --source-archive JSON
                  reproduce-backtest --dataset JSON --evidence JSON --source-archive JSON
                  paper-start --dataset JSON --evidence archive-COHORT.json
                  paper-step --state JSON --observation JSON | paper-recover --state JSON
                  paper-evaluate --state JSON
                  krx-fetch --market KOSPI|KOSDAQ --date DATE | krx-build --manifest JSON
                  krx-calendar --year YEAR [--start DATE --end DATE] | krx-audit --plan JSON
                  krx-reference-audit --plan JSON
                  krx-review-queue --plan JSON
                  kind-delistings --start DATE --end DATE [--market ALL|KOSPI|KOSDAQ|KONEX --max-requests 5 --interval-seconds 1]
                  kind-notice --source KIND_EXTERNAL_HTML_URL
                  corporate-append --manifest JSON [--state prior-corporate-ledger.json]
                  corporate-at --state JSON --cutoff TIMESTAMP --recorded-cutoff TIMESTAMP [--timing OBSERVED|REVIEWED_PUBLICATION]
                  krx-basic-fetch --market KOSPI|KOSDAQ --date DATE
                  krx-index-fetch --market KOSPI|KOSDAQ --date DATE
                  krx-collect --plan JSON [--max-requests 5] [--interval-seconds 1]
                  dart-disclosures --start DATE --end DATE [--corp-code CODE] | dart-company --corp-code CODE
                  dart-check [--corp-code CODE]
                  dart-collect --plan JSON [--max-requests 3] [--interval-seconds 1] | dart-context --plan JSON
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
internal sealed record DartContextPlan(string[] SnapshotFiles, string[] DisclosureFiles, string CorpCode, DateTimeOffset Cutoff);
internal sealed record CorporateActionImport(CorporateActionRevision Revision, string[] SnapshotFiles);
internal sealed record KrxReviewQueuePlan(string[] PriceAuditFiles, string[] ReferenceAuditFiles);
