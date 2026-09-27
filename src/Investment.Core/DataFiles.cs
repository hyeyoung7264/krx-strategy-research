using System.Globalization;
using System.Text.Json;

namespace Investment.Core;

public static class DataFiles
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public const string Header = "Ticker,Sector,Date,AvailableAt,Open,High,Low,Close,Volume,TradingValue,Tradable,Member,CorporateAction";
    public static Dataset ReadCsv(string path, string source, bool pointInTimeCertified = false)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2 || lines[0] != Header) throw new ArgumentException($"Required header: {Header}");
        decimal D(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
        var bars = lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l =>
        {
            var f = l.Split(','); if (f.Length != 13) throw new ArgumentException("CSV needs exactly 13 unquoted fields.");
            if (!f[3].EndsWith('Z') && !(f[3].Length >= 6 && (f[3][^6] == '+' || f[3][^6] == '-'))) throw new ArgumentException("Explicit timezone required.");
            return new Bar(f[0], f[1], DateOnly.ParseExact(f[2], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(f[3], CultureInfo.InvariantCulture), D(f[4]), D(f[5]), D(f[6]), D(f[7]),
                long.Parse(f[8], CultureInfo.InvariantCulture), D(f[9]), bool.Parse(f[10]), bool.Parse(f[11]), bool.Parse(f[12]));
        }).ToArray();
        var data = new Dataset(source, false, pointInTimeCertified, bars); data.Validate(); return data;
    }
    public static Dataset Demo(int sessions = 540, int seed = 20260927)
    {
        var random = new Random(seed); var bars = new List<Bar>(); var date = new DateOnly(2022, 1, 3);
        var prices = Enumerable.Repeat(10000m, 6).ToArray();
        for (var i = 0; i < sessions; i++)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
            for (var t = 0; t < prices.Length; t++)
            {
                var open = decimal.Round(prices[t] * (1 + (decimal)(random.NextDouble() - .5) * .01m), 2);
                var close = decimal.Round(open * (1 + (decimal)(random.NextDouble() - .48) * .03m), 2);
                var high = decimal.Round(Math.Max(open, close) * 1.01m, 2); var low = decimal.Round(Math.Min(open, close) * .99m, 2);
                bars.Add(new($"DEMO{t:00}", $"sector{t % 3}", date, Clock.Close(date), open, high, low, close, 1_000_000, close * 1_000_000, true, true)); prices[t] = close;
            }
            date = date.AddDays(1);
        }
        return new("SYNTHETIC seeded engine fixture; weekdays are NOT a KRX calendar", true, false, bars.ToArray());
    }
}

public sealed class EvidenceStore(string directory)
{
    public string Save<T>(string kind, string id, T value)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(id) || kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid evidence identifier.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DataFiles.Json);
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, $"{kind}-{id}.json"));
        var pending = Path.Combine(Path.GetDirectoryName(path)!, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(pending, path, overwrite: false); return path;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
