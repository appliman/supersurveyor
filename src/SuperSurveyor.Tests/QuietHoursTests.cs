using System.Text.Json;
using SuperSurveyor;

internal static class QuietHoursTests
{
    internal static void Run()
    {
        VerifySchedule();
        VerifyConfigurationCompatibility();
        VerifyBufferedSummary();
        VerifySummaryRetries();
    }

    private static void VerifySchedule()
    {
        var schedule = new Settings().QuietHours;
        var offset = TimeSpan.FromHours(2);
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestServer", offset, "Test server", "Test server");

        // November 27, 2026 is a Friday. Local midnight falls on the previous UTC date.
        Verify(27, 21, 59, false);
        Verify(27, 22, 0, true);
        Verify(28, 0, 0, true);
        Verify(28, 7, 0, true);
        Verify(28, 8, 29, true);
        Verify(28, 8, 30, false);
        Verify(29, 8, 29, true);
        Verify(29, 8, 30, false);
        Verify(29, 22, 0, true);
        Verify(30, 0, 0, true);
        Verify(30, 6, 59, true);
        Verify(30, 7, 0, false);

        schedule = new QuietHours
        {
            Start = new TimeOnly(21, 15),
            WeekdayResume = new TimeOnly(6, 45),
            WeekendResume = new TimeOnly(9, 5)
        };
        Check(schedule.IsValid(), "An overnight schedule was rejected.");
        Verify(27, 21, 14, false);
        Verify(27, 21, 15, true);
        Verify(28, 9, 4, true);
        Verify(28, 9, 5, false);
        Verify(30, 6, 44, true);
        Verify(30, 6, 45, false);

        Check(!(schedule with { WeekdayResume = schedule.Start }).IsValid(),
            "A schedule with equal weekday start and resume times was accepted.");
        Check(!(schedule with { WeekendResume = new TimeOnly(23, 30) }).IsValid(),
            "A weekend resume time after the evening start was accepted.");

        void Verify(int day, int hour, int minute, bool expected)
        {
            var local = new DateTimeOffset(2026, 11, day, hour, minute, 0, offset);
            Check(schedule.IsActive(local.ToUniversalTime(), zone) == expected,
                $"Unexpected quiet-hours status at {local:yyyy-MM-dd HH:mm zzz}.");
        }
    }

    private static void VerifyConfigurationCompatibility()
    {
        const string legacySettings = """
            {"SubscriberId":"simulated-user","SmsKey":"simulated-key",
             "CredentialRevision":"e606d4be-4085-4c72-9a90-0ab1dbe71bc9"}
            """;
        var previous = JsonSerializer.Deserialize(legacySettings, SurveyorJson.Default.Settings)
            ?? throw new InvalidOperationException("Legacy settings could not be loaded.");
        Check(previous.IsValid() && previous.QuietHours == new QuietHours(),
            "An existing installation did not inherit the default quiet-hours schedule.");

        var custom = new QuietHours
        {
            Start = new TimeOnly(23, 15),
            WeekdayResume = new TimeOnly(6, 30),
            WeekendResume = new TimeOnly(8, 45)
        };
        var updated = previous with { QuietHours = custom };
        var payload = JsonSerializer.Serialize(updated, SurveyorJson.Default.Settings);
        var reloaded = JsonSerializer.Deserialize(payload, SurveyorJson.Default.Settings);
        Check(reloaded?.QuietHours == custom, "Custom quiet hours changed after a settings round trip.");
    }

    private static void VerifyBufferedSummary()
    {
        const string host = "server-01";
        var evening = new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.Zero);
        var morning = evening.AddHours(10.5);
        var period = TimeSpan.FromSeconds(30);
        var memory = new Measurement("memory", "Memory", 94, 90, TimeSpan.Zero);
        var disk = new Measurement("disk:ext4:/dev/sdb1", "Disk /data", 91, 85, TimeSpan.Zero);
        var cpu = new Measurement("cpu", "CPU", 96, 90, TimeSpan.FromMinutes(5));
        var legacy = JsonSerializer.Deserialize("""{"Episodes":{}}""", SurveyorJson.Default.MonitorState)
            ?? throw new InvalidOperationException("Legacy alert state could not be loaded.");
        var rules = new AlertRules(legacy);

        Check(rules.ReadSummary(host, evening) is null, "An empty buffer produced an SMS.");
        Check(rules.Observe(memory, evening, period).Send, "High memory did not qualify for buffering.");
        rules.Defer(memory, evening);
        Check(!rules.Observe(memory, evening + period, period).Send, "An ongoing episode was buffered twice.");
        rules.Defer(memory, evening + period);
        Check(!rules.Observe(memory with { Percent = 90 }, evening.AddMinutes(1), period).Send,
            "A recovery generated a buffered alert.");

        var secondMemoryAlert = memory with { Percent = 98 };
        Check(rules.Observe(secondMemoryAlert, evening.AddMinutes(2), period).Send,
            "A second memory episode did not qualify.");
        rules.Defer(secondMemoryAlert, evening.AddMinutes(2));
        rules.Observe(memory with { Percent = 80 }, evening.AddMinutes(3), period);

        Check(rules.Observe(disk, evening.AddMinutes(1), period).Send, "A disk alert did not qualify.");
        rules.Defer(disk, evening.AddMinutes(1));
        for (var sample = 1; sample <= 10; sample++)
        {
            var observedAt = evening + period * sample;
            var decision = rules.Observe(cpu, observedAt, period);
            Check(decision.Send == (sample == 10), "The quiet-period CPU alert did not honor its duration.");
            if (decision.Send)
            {
                rules.Defer(cpu, observedAt);
            }
        }

        rules.Observe(cpu with { Percent = 60 }, evening.AddMinutes(6), period);
        var reloaded = Reload(legacy);
        Check(reloaded.BufferedAlerts.Count == 4, "The buffer lost incidents or retained a duplicate.");
        var restarted = new AlertRules(reloaded);
        var summary = restarted.ReadSummary(host, morning);
        Check(summary is not null && summary.Contains("Quiet hours: 4 alerts.", StringComparison.Ordinal)
            && summary.Contains("CPU 1 (max 96%)", StringComparison.Ordinal)
            && summary.Contains("RAM 2 (max 98%)", StringComparison.Ordinal)
            && summary.Contains("Disk 1 (max 91%)", StringComparison.Ordinal),
            "The SMS did not summarize all buffered episodes and their peaks.");
        restarted.RecordSummary(SmsOutcome.Accepted, morning);

        var afterDelivery = Reload(reloaded);
        var afterRestart = new AlertRules(afterDelivery);
        Check(afterDelivery.BufferedAlerts.Count == 0 && afterRestart.ReadSummary(host, morning) is null,
            "An accepted summary would be sent again after restarting.");
        Check(!afterRestart.Observe(disk, morning + period, period).Send,
            "An ongoing episode was sent again as an individual alert after the summary.");
    }

    private static void VerifySummaryRetries()
    {
        const string host = "server-01";
        var now = new DateTimeOffset(2026, 11, 30, 7, 0, 0, TimeSpan.Zero);
        var memory = new Measurement("memory", "Memory", 94, 90, TimeSpan.Zero);
        var state = new MonitorState();
        var rules = new AlertRules(state);
        rules.Defer(memory, now.AddHours(-2));
        var original = rules.ReadSummary(host, now);
        Check(original is not null, "A buffered alert did not produce a summary.");

        rules.RecordSummary(SmsOutcome.CredentialsRejected, now);
        Check(rules.ReadSummary(host, now) == original, "Rejected credentials discarded buffered alerts.");
        foreach (var minutes in new[] { 1, 5, 15, 15 })
        {
            rules.RecordSummary(SmsOutcome.TemporaryFailure, now);
            state = Reload(state);
            rules = new AlertRules(state);
            now = now.AddMinutes(minutes);
            Check(rules.ReadSummary(host, now.AddSeconds(-1)) is null, "A buffered SMS retried too early.");
            Check(rules.ReadSummary(host, now) == original, "A buffered SMS changed or was lost during retry.");
        }

        rules.RecordSummary(SmsOutcome.RequestRejected, now);
        Check(state.BufferedAlerts.Count == 1 && rules.ReadSummary(host, now) is null,
            "A rejected request discarded buffered alerts or caused an immediate retry.");
        Check(rules.ReadSummary(host, now.AddMinutes(15)) == original,
            "A rejected request did not retry the retained summary.");
    }

    private static MonitorState Reload(MonitorState state)
    {
        var json = JsonSerializer.Serialize(state, SurveyorJson.Default.MonitorState);
        var result = JsonSerializer.Deserialize(json, SurveyorJson.Default.MonitorState)
            ?? throw new InvalidOperationException("The buffered state could not be loaded.");
        return result;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
