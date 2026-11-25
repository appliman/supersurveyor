namespace SuperSurveyor;

internal sealed class AlertRules(MonitorState state)
{
    private readonly Dictionary<string, TimeSpan> _continuous = new(StringComparer.Ordinal);

    internal (bool Send, TimeSpan Duration) Observe(
        Measurement measurement, DateTimeOffset now, TimeSpan interval)
    {
        if (measurement.Percent is null)
        {
            _continuous.Remove(measurement.Key);
            return default;
        }

        if (measurement.Percent <= measurement.Threshold)
        {
            _continuous.Remove(measurement.Key);
            state.Episodes.Remove(measurement.Key);
            return default;
        }

        // An incomplete interval cannot establish continuous high CPU usage.
        if (measurement.Duration > TimeSpan.Zero && interval <= TimeSpan.Zero)
        {
            _continuous.Remove(measurement.Key);
            return default;
        }

        var duration = _continuous.GetValueOrDefault(measurement.Key);
        if (measurement.Duration > TimeSpan.Zero)
        {
            duration += interval;
            _continuous[measurement.Key] = duration;
        }

        var episode = state.Episodes.GetValueOrDefault(measurement.Key);
        var eligible = episode is null || (!episode.Completed && episode.RetryAt <= now);
        var result = (eligible && duration >= measurement.Duration, duration);
        return result;
    }

    internal void Record(string key, SmsOutcome outcome, DateTimeOffset now)
    {
        if (outcome == SmsOutcome.CredentialsRejected)
        {
            return;
        }

        if (outcome == SmsOutcome.Accepted || outcome == SmsOutcome.RequestRejected)
        {
            state.Episodes[key] = new() { Completed = true };
            return;
        }

        var attempt = Math.Min((state.Episodes.GetValueOrDefault(key)?.Failures ?? 0) + 1, 3);
        var minutes = attempt switch { 1 => 1, 2 => 5, _ => 15 };
        state.Episodes[key] = new() { Failures = attempt, RetryAt = now.AddMinutes(minutes) };
    }
}

internal sealed class MonitorState
{
    public Dictionary<string, AlertEpisode> Episodes { get; init; } = new(StringComparer.Ordinal);
    public Guid? RejectedCredentialRevision { get; set; }
}

internal sealed record AlertEpisode
{
    public bool Completed { get; init; }
    public int Failures { get; init; }
    public DateTimeOffset RetryAt { get; init; }
}
