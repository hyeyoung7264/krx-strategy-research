using System.Text.Json.Serialization;

namespace Investment.Core;

public sealed record SellTaxSession(DateOnly TradeDate, DateOnly SettlementDate, decimal Rate, string Evidence);

// Explicit portfolio-wide rates, not a certified exchange calendar or an approved tax policy.
// Settlement dates and the rate applied to them must be supplied with their evidence.
public sealed class SellTaxSchedule : IEquatable<SellTaxSchedule>
{
    private readonly SellTaxSession[] sessions;
    public string Source { get; }
    public SellTaxSession[] Sessions => (SellTaxSession[])sessions.Clone();

    [JsonConstructor]
    public SellTaxSchedule(string source, SellTaxSession[] sessions)
    {
        Source = source;
        this.sessions = sessions?.ToArray() ?? throw new ArgumentException("Missing sell-tax sessions.");
        Validate();
    }

    public void Validate(decimal commission = 0)
    {
        if (string.IsNullOrWhiteSpace(Source) || sessions.Length == 0 || commission < 0)
            throw new ArgumentException("Missing sell-tax schedule source/sessions or invalid commission.");
        for (var i = 0; i < sessions.Length; i++)
        {
            var row = sessions[i];
            if (row is null || row.SettlementDate <= row.TradeDate || row.Rate < 0 ||
                commission + row.Rate >= 1 || string.IsNullOrWhiteSpace(row.Evidence) ||
                i > 0 && sessions[i - 1].TradeDate >= row.TradeDate)
                throw new ArgumentException("Sell-tax sessions require ordered unique trade dates, later settlement dates, valid rates and evidence.");
        }
    }

    public decimal RateOn(DateOnly tradeDate)
    {
        var low = 0; var high = sessions.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var comparison = sessions[middle].TradeDate.CompareTo(tradeDate);
            if (comparison == 0) return sessions[middle].Rate;
            if (comparison < 0) low = middle + 1; else high = middle - 1;
        }
        throw new ArgumentException($"Missing explicit sell-tax/settlement mapping for trade date {tradeDate:yyyy-MM-dd}.");
    }

    public bool Equals(SellTaxSchedule? other) => other is not null &&
        Source == other.Source && sessions.SequenceEqual(other.sessions);
    public override bool Equals(object? obj) => obj is SellTaxSchedule other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Source, StringComparer.Ordinal);
        foreach (var row in sessions) hash.Add(row);
        return hash.ToHashCode();
    }
}
