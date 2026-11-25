namespace SuperSurveyor;

internal sealed record QuietHours
{
    public TimeOnly Start { get; init; } = new(22, 0);
    public TimeOnly WeekdayResume { get; init; } = new(7, 0);
    public TimeOnly WeekendResume { get; init; } = new(8, 30);

    internal bool IsValid()
    {
        var result = Start > WeekdayResume && Start > WeekendResume;
        return result;
    }

    internal bool IsActive(DateTimeOffset utcNow, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);

        // The local calendar day determines the morning cutoff, including after a Friday or Sunday evening.
        var isWeekend = localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var resume = isWeekend ? WeekendResume : WeekdayResume;
        var result = localTime >= Start || localTime < resume;
        return result;
    }
}
