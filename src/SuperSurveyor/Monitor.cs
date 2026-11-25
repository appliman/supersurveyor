using System.Diagnostics;
using System.Runtime.Versioning;

namespace SuperSurveyor;

[SupportedOSPlatform("linux")]
internal sealed class Monitor(FreeMobileSms sms)
{
    private static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(30);

    internal async Task Run(Settings settings, string statePath, CancellationToken cancellationToken)
    {
        var state = Storage.Read(statePath, SurveyorJson.Default.MonitorState) ?? new();
        if (state.Episodes is null || state.BufferedAlerts is null)
        {
            throw new InvalidDataException("The alert state has an invalid format.");
        }

        var rules = new AlertRules(state);
        var metrics = new LinuxMetrics();
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var host = Environment.MachineName;
        var timeZone = TimeZoneInfo.Local;
        using var timer = new PeriodicTimer(SamplePeriod);

        var previousSample = Stopwatch.GetTimestamp();
        var firstSample = true;
        Console.WriteLine("SuperSurveyor is monitoring this system.");
        do
        {
            var timestamp = Stopwatch.GetTimestamp();
            var interval = Stopwatch.GetElapsedTime(previousSample, timestamp);
            previousSample = timestamp;

            // A gap of more than half a sampling period cannot establish a continuous reading.
            if (firstSample || interval > SamplePeriod * 1.5)
            {
                interval = TimeSpan.Zero;
            }

            var stateEntries = state.Episodes.ToArray();
            var bufferedCount = state.BufferedAlerts.Count;
            var rejectedRevision = state.RejectedCredentialRevision;
            var pending = new List<(Measurement Measurement, TimeSpan Duration)>();
            var now = DateTimeOffset.UtcNow;
            foreach (var measurement in metrics.Read(settings))
            {
                if (measurement.Percent is null)
                {
                    if ((!firstSample || measurement.Key != "cpu") && unavailable.Add(measurement.Key))
                    {
                        Console.Error.WriteLine($"{measurement.Label} measurement is unavailable.");
                    }
                }
                else if (unavailable.Remove(measurement.Key))
                {
                    Console.WriteLine($"{measurement.Label} measurement is available again.");
                }

                var decision = rules.Observe(measurement, now, interval);
                if (decision.Send)
                {
                    pending.Add((measurement, decision.Duration));
                }
            }

            var dispatchAt = DateTimeOffset.UtcNow;
            var quietHoursActive = settings.QuietHours.IsActive(dispatchAt, timeZone);
            if (quietHoursActive || settings.QuietHours.IsActive(now, timeZone))
            {
                foreach (var item in pending)
                {
                    rules.Defer(item.Measurement, now);
                }

                pending.Clear();
            }

            if (bufferedCount != state.BufferedAlerts.Count || !stateEntries.SequenceEqual(state.Episodes))
            {
                Storage.Write(statePath, state, SurveyorJson.Default.MonitorState);
            }

            if (!quietHoursActive && rejectedRevision != settings.CredentialRevision)
            {
                // Start each send in this sampling window so an API delay cannot add up across disks.
                var attempts = pending.Select(item =>
                    Send(item.Measurement, item.Measurement.ToSms(host, item.Duration), settings, cancellationToken))
                    .ToList();
                if (rules.ReadSummary(host, dispatchAt) is { } summary)
                {
                    attempts.Add(Send(null, summary, settings, cancellationToken));
                }

                while (attempts.Count > 0)
                {
                    var attempt = await Task.WhenAny(attempts);
                    attempts.Remove(attempt);
                    if (attempt.IsCanceled)
                    {
                        continue;
                    }

                    var delivery = await attempt;
                    var label = "quiet-hours summary";
                    if (delivery.Measurement is { } measurement)
                    {
                        rules.Record(measurement.Key, delivery.Outcome, delivery.FinishedAt);
                        label = measurement.Label;
                    }
                    else
                    {
                        rules.RecordSummary(delivery.Outcome, delivery.FinishedAt);
                    }

                    if (delivery.Outcome == SmsOutcome.CredentialsRejected)
                    {
                        if (state.RejectedCredentialRevision != settings.CredentialRevision)
                        {
                            state.RejectedCredentialRevision = settings.CredentialRevision;
                            Console.Error.WriteLine("Free Mobile rejected the credentials. Run sudo supersurveyor configure to resume SMS alerts.");
                        }
                    }
                    else if (delivery.Outcome == SmsOutcome.Accepted)
                    {
                        Console.WriteLine($"Free Mobile accepted the {label} SMS.");
                    }
                    else if (delivery.Outcome == SmsOutcome.TemporaryFailure || delivery.Measurement is null)
                    {
                        Console.Error.WriteLine($"The {label} SMS could not be sent and will be retried.");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Free Mobile rejected the {label} SMS request.");
                    }

                    Storage.Write(statePath, state, SurveyorJson.Default.MonitorState);
                }
            }

            firstSample = false;
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task<(Measurement? Measurement, SmsOutcome Outcome, DateTimeOffset FinishedAt)> Send(
        Measurement? measurement, string message, Settings settings, CancellationToken cancellationToken)
    {
        var outcome = await sms.Send(settings, message, cancellationToken);
        var result = (measurement, outcome, DateTimeOffset.UtcNow);
        return result;
    }
}
