using System.Text.Json.Serialization;

namespace Investment.Core;

public sealed record CostStressScenario(string ScenarioKey, decimal CommissionAdd, decimal SlippageAdd)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ScenarioKey) || ScenarioKey.Length > 64 ||
            ScenarioKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) || CommissionAdd < 0 || SlippageAdd < 0 ||
            CommissionAdd >= 1 || SlippageAdd >= 1 || CommissionAdd == 0 && SlippageAdd == 0)
            throw new ArgumentException("Cost stress requires a key and positive total, nonnegative commission/slippage additions below one.");
    }
    public Costs Apply(Costs baseline)
    {
        Validate();
        var stressed = baseline with { Commission = baseline.Commission + CommissionAdd, Slippage = baseline.Slippage + SlippageAdd };
        stressed.Validate();
        return stressed;
    }
}

// A finite diagnostic plan is stored in the research reservation before any evaluation.
public sealed class CostStressPlan : IEquatable<CostStressPlan>
{
    private readonly CostStressScenario[] scenarios;
    public CostStressScenario[] Scenarios => (CostStressScenario[])scenarios.Clone();

    [JsonConstructor]
    public CostStressPlan(CostStressScenario[] scenarios)
    {
        this.scenarios = scenarios?.ToArray() ?? throw new ArgumentException("Missing cost stress scenarios.");
        Validate();
    }
    public void Validate()
    {
        if (scenarios.Length is < 1 or > 4 || scenarios.Any(s => s is null))
            throw new ArgumentException("Register one to four cost stress scenarios.");
        foreach (var scenario in scenarios) scenario.Validate();
        if (scenarios.Select(s => s.ScenarioKey).Distinct(StringComparer.Ordinal).Count() != scenarios.Length)
            throw new ArgumentException("Cost stress scenario keys must be unique.");
    }
    public bool Equals(CostStressPlan? other) => other is not null && scenarios.SequenceEqual(other.scenarios);
    public override bool Equals(object? obj) => obj is CostStressPlan other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode(); foreach (var scenario in scenarios) hash.Add(scenario); return hash.ToHashCode();
    }
}

public sealed record CostSensitivityDelta(decimal TotalReturn, decimal AverageDailyReturn,
    decimal MaximumDrawdown, decimal ExpectedValuePerTrade, int NumberOfTrades, decimal Turnover, int OpenPositions);
public sealed record CostSensitivityScenarioResult(string ScenarioKey, decimal CommissionAdd, decimal SlippageAdd,
    decimal Commission, decimal Slippage, Metrics Metrics, CostSensitivityDelta Delta, string[] Events);
public sealed record CostSensitivityWindow(string WindowKey, string Role, DateOnly Start, DateOnly End,
    StrategySpec[] Strategies, decimal InitialCapital, Metrics BaselineMetrics, string[] BaselineEvents,
    CostSensitivityScenarioResult[] Scenarios);
public sealed record CostSensitivityReport(string InputDataHash, DateOnly HoldoutStart, string CodeVersion,
    CostSensitivityWindow[] Windows,
    string Note = "Descriptive pre-holdout diagnostics only. Fixed strategies; unchanged taxes and risk. Windows may overlap; no independent evidence, statistical test or promotion decision.");

// Deliberately excludes the full-data hash, holdout results and stochastic run identifiers.
internal sealed record CostSensitivityWindowInput(string WindowKey, string Role, DateOnly Start, DateOnly End,
    StrategySpec[] Strategies, decimal InitialCapital, Metrics BaselineMetrics, string[] BaselineEvents)
{
    internal static CostSensitivityWindowInput FromRun(string windowKey, string role, RunResult run) =>
        new(windowKey, role, run.Start, run.End, run.Strategies.ToArray(), run.InitialCapital, run.Metrics, run.Events.ToArray());
}

internal static class CostSensitivityData
{
    internal static Dataset BeforeHoldout(Dataset data, DateOnly holdoutStart)
    {
        var bars = data.Bars.Where(b => b.Date < holdoutStart).ToArray();
        var tickers = bars.Select(b => b.Ticker).ToHashSet(StringComparer.Ordinal);
        var events = data.LifecycleEvents?.Where(e => e.Date < holdoutStart && tickers.Contains(e.Ticker)).ToArray();
        if (bars.Length == 0) throw new ArgumentException("Cost diagnostics require observations before holdout.");
        var units = ShareUnitPrefix.Select(data, tickers, bars.Max(b => b.Date), Clock.Open(holdoutStart).AddTicks(-1));
        var trimmed = data with { Bars = bars, Sessions = data.Sessions?.Where(d => d < holdoutStart).ToArray(),
            LifecycleEvents = events is { Length: > 0 } ? events : null,
            ShareUnitChanges = units.Changes, ShareInventoryCredits = units.Credits };
        trimmed.Validate();
        return trimmed;
    }
}

internal static class ShareUnitPrefix
{
    internal static (ShareUnitChange[]? Changes, ShareInventoryCredit[]? Credits) Select(Dataset data,
        HashSet<string> tickers, DateOnly economicDate, DateTimeOffset knownAt)
    {
        // An earlier effective event can still explain old quote units or pending inventory.
        // Keep its known future schedule, but never import a later observed credit or event.
        var changes = data.ShareUnitChanges?.Where(c => tickers.Contains(c.Ticker) &&
            c.EffectiveDate <= economicDate && c.AvailableAt <= knownAt).ToArray();
        var keys = (changes ?? []).Select(c => c.ActionKey).ToHashSet(StringComparer.Ordinal);
        var credits = data.ShareInventoryCredits?.Where(c => keys.Contains(c.ActionKey) &&
            c.CreditedAt <= knownAt && c.AvailableAt <= knownAt).ToArray();
        return (changes is { Length: > 0 } ? changes : null, credits is { Length: > 0 } ? credits : null);
    }
}

public static class CostSensitivityRunner
{
    public static void Preflight(CostStressPlan? plan, Costs costs, IEnumerable<DateOnly> sessions)
    {
        costs.Validate();
        var dates = sessions.ToArray(); costs.RequireCoverage(dates);
        if (plan == null) return;
        plan.Validate();
        foreach (var scenario in plan.Scenarios)
        {
            var stressed = scenario.Apply(costs);
            stressed.RequireCoverage(dates);
        }
    }

    internal static CostSensitivityReport Run(Dataset beforeHoldout, DateOnly holdoutStart, CostStressPlan plan,
        Costs costs, Risk risk, CostSensitivityWindowInput[] windows, string codeVersion)
    {
        beforeHoldout.Validate(); risk.Validate();
        if (beforeHoldout.Dates.Any(d => d >= holdoutStart) || windows.Length == 0 ||
            windows.Select(w => w.WindowKey).Distinct(StringComparer.Ordinal).Count() != windows.Length ||
            windows.Any(w => w.Role is not ("walk-forward-test" or "final-validation") || w.Start > w.End || w.End >= holdoutStart))
            throw new ArgumentException("Cost diagnostics accept fixed pre-holdout test/validation windows only.");
        Preflight(plan, costs, beforeHoldout.Dates);
        var engine = new BacktestEngine();
        var results = windows.Select(window =>
        {
            var scenarios = plan.Scenarios.Select(scenario =>
            {
                var stressed = scenario.Apply(costs);
                var result = engine.Run(beforeHoldout, window.Strategies, window.Start, window.End,
                    stressed, risk, window.InitialCapital, codeVersion);
                var original = window.BaselineMetrics; var actual = result.Metrics;
                var delta = new CostSensitivityDelta(actual.TotalReturn - original.TotalReturn,
                    actual.AverageDailyReturn - original.AverageDailyReturn, actual.MaximumDrawdown - original.MaximumDrawdown,
                    actual.ExpectedValuePerTrade - original.ExpectedValuePerTrade, actual.NumberOfTrades - original.NumberOfTrades,
                    actual.Turnover - original.Turnover, actual.OpenPositions - original.OpenPositions);
                return new CostSensitivityScenarioResult(scenario.ScenarioKey, scenario.CommissionAdd, scenario.SlippageAdd,
                    stressed.Commission, stressed.Slippage, actual, delta, result.Events);
            }).ToArray();
            return new CostSensitivityWindow(window.WindowKey, window.Role, window.Start, window.End,
                window.Strategies, window.InitialCapital, window.BaselineMetrics, window.BaselineEvents, scenarios);
        }).ToArray();
        return new(beforeHoldout.Hash, holdoutStart, codeVersion, results);
    }
}
