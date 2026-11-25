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
        state.Episodes[key] = new() { Failures = attempt, RetryAt = RetryAt(now, attempt) };
    }

    internal void Defer(Measurement measurement, DateTimeOffset now)
    {
        if (measurement.Percent is not double percent || state.Episodes.GetValueOrDefault(measurement.Key)?.Completed == true)
        {
            return;
        }

        // Tracking this episode separately prevents recovery from removing the queued notification.
        state.BufferedAlerts.Add(new BufferedAlert(now, measurement.Key, percent));
        state.Episodes[measurement.Key] = new() { Completed = true };
    }

    internal string? ReadSummary(string host, DateTimeOffset now)
    {
        if (state.BufferedAlerts.Count == 0 || state.BufferedAlertRetryAt > now)
        {
            return null;
        }

        var categories = new List<string>(3);
        AddCategory("CPU", alert => alert.Key == "cpu");
        AddCategory("RAM", alert => alert.Key == "memory");
        AddCategory("Disk", alert => alert.Key.StartsWith("disk:", StringComparison.Ordinal));

        // Keep the recap short enough for one SMS, including long system hostnames.
        var safeHost = string.Concat(host.Take(24).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-'));
        var count = state.BufferedAlerts.Count;
        var suffix = count == 1 ? "alert" : "alerts";
        var totals = string.Join(", ", categories);
        var result = FormattableString.Invariant(
            $"SuperSurveyor | {safeHost} | Quiet hours: {count} {suffix}. {totals}.");
        return result;

        void AddCategory(string label, Func<BufferedAlert, bool> matches)
        {
            var count = 0;
            double peak = 0;
            foreach (var alert in state.BufferedAlerts)
            {
                if (matches(alert))
                {
                    count++;
                    peak = Math.Max(peak, alert.Percent);
                }
            }

            if (count > 0)
            {
                categories.Add(FormattableString.Invariant($"{label} {count} (max {peak:0.#}%)"));
            }
        }
    }

    internal void RecordSummary(SmsOutcome outcome, DateTimeOffset now)
    {
        if (outcome == SmsOutcome.Accepted)
        {
            state.BufferedAlerts.Clear();
            state.BufferedAlertFailures = 0;
            state.BufferedAlertRetryAt = default;
        }
        else if (outcome != SmsOutcome.CredentialsRejected)
        {
            state.BufferedAlertFailures = Math.Min(state.BufferedAlertFailures + 1, 3);
            state.BufferedAlertRetryAt = RetryAt(now, state.BufferedAlertFailures);
        }
    }

    private static DateTimeOffset RetryAt(DateTimeOffset now, int attempt)
    {
        var minutes = attempt switch { 1 => 1, 2 => 5, _ => 15 };
        var result = now.AddMinutes(minutes);
        return result;
    }
}

internal sealed class MonitorState
{
    public Dictionary<string, AlertEpisode> Episodes { get; init; } = new(StringComparer.Ordinal);
    public Guid? RejectedCredentialRevision { get; set; }
    public List<BufferedAlert> BufferedAlerts { get; init; } = [];
    public int BufferedAlertFailures { get; set; }
    public DateTimeOffset BufferedAlertRetryAt { get; set; }
}

internal sealed record AlertEpisode
{
    public bool Completed { get; init; }
    public int Failures { get; init; }
    public DateTimeOffset RetryAt { get; init; }
}
