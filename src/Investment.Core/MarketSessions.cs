namespace Investment.Core;

// Reviewed session inputs. A calendar date or this record alone does not certify a market feed.
public sealed record SessionHours(DateOnly Date, DateTimeOffset OpenAt, DateTimeOffset CloseAt,
    DateTimeOffset AvailableAt, string Evidence);

public static class MarketSessions
{
    private static readonly TimeSpan KoreaOffset = TimeSpan.FromHours(9);

    public static void Validate(SessionHours[]? hours, IEnumerable<DateOnly> requiredDates, bool requireExplicit = false)
    {
        ArgumentNullException.ThrowIfNull(requiredDates);
        var dates = requiredDates.ToHashSet();
        if (dates.Contains(default)) throw new ArgumentException("Invalid required market session date.");
        if (hours is null)
        {
            if (requireExplicit) throw new ArgumentException("Explicit reviewed market session hours required.");
            return;
        }

        var present = new HashSet<DateOnly>();
        DateOnly? previous = null;
        foreach (var row in hours)
        {
            if (row is null || row.Date == default || row.OpenAt == default || row.CloseAt == default ||
                row.AvailableAt == default || string.IsNullOrWhiteSpace(row.Evidence) ||
                DateOnly.FromDateTime(row.OpenAt.ToOffset(KoreaOffset).DateTime) != row.Date ||
                DateOnly.FromDateTime(row.CloseAt.ToOffset(KoreaOffset).DateTime) != row.Date ||
                row.OpenAt >= row.CloseAt || row.AvailableAt > row.OpenAt)
                throw new ArgumentException("Invalid or late market session hours evidence.");
            if (previous is not null && row.Date <= previous.Value || !present.Add(row.Date))
                throw new ArgumentException("Market session hours must have unique dates in ascending order.");
            previous = row.Date;
        }
        if (!dates.IsSubsetOf(present))
            throw new ArgumentException("Explicit market session hours are missing a required date; fallback is forbidden.");
    }

    public static DateTimeOffset OpeningTime(DateOnly date, SessionHours[]? hours, bool requireExplicit = false)
    {
        Validate(hours, [date], requireExplicit);
        return hours is null ? Clock.Open(date) : hours.Single(row => row.Date == date).OpenAt;
    }

    public static DateTimeOffset ClosingTime(DateOnly date, SessionHours[]? hours, bool requireExplicit = false)
    {
        Validate(hours, [date], requireExplicit);
        return hours is null ? Clock.Close(date) : hours.Single(row => row.Date == date).CloseAt;
    }

    // The caller includes required historical effective-date anchors, but excludes future schedule rows.
    // Never turn an explicit, incomplete or not-yet-known timetable into an implicit default clock.
    public static SessionHours[]? Prefix(SessionHours[]? hours, IEnumerable<DateOnly> retainedDates, DateTimeOffset knownAt)
    {
        ArgumentNullException.ThrowIfNull(retainedDates);
        if (knownAt == default) throw new ArgumentException("Missing session prefix knowledge time.");
        var dates = retainedDates.ToHashSet();
        Validate(hours, dates);
        if (hours is null) return null;
        var retained = hours.Where(row => dates.Contains(row.Date)).ToArray();
        if (retained.Any(row => row.AvailableAt > knownAt))
            throw new ArgumentException("Required market session hours were not available at the prefix cutoff.");
        return retained;
    }
}
