using System.Net;
using System.Text.Json;
using SuperSurveyor;

var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
var period = TimeSpan.FromSeconds(30);
var cpu = new Measurement("cpu", "CPU", 91, 90, TimeSpan.FromMinutes(5));
var memory = new Measurement("memory", "Memory", 94, 90, TimeSpan.Zero);
var disk = new Measurement("disk:ext4:/dev/sdb1", "Disk /data", 91, 85, TimeSpan.Zero, "Free space: 18 GiB.");

VerifyCpu();
VerifyRetryAndPersistence();
VerifyMetrics();
await VerifySms();
if (OperatingSystem.IsLinux())
{
    VerifyLinuxStorage();
}
Console.WriteLine("All SuperSurveyor checks passed.");

void VerifyCpu()
{
    var state = new MonitorState();
    var rules = new AlertRules(state);
    for (var sample = 1; sample < 10; sample++)
    {
        Check(!rules.Observe(cpu, now + period * sample, period).Send, "CPU alerted before five minutes.");
    }

    Check(!rules.Observe(cpu with { Percent = 90 }, now + period * 10, period).Send,
        "CPU alerted at exactly 90%.");
    Check(!rules.Observe(cpu, now + period * 11, period).Send, "A value of 90% did not reset CPU duration.");
    Check(!rules.Observe(cpu with { Percent = null }, now + period * 12, period).Send,
        "An unavailable CPU reading qualified.");
    for (var sample = 13; sample < 22; sample++)
    {
        Check(!rules.Observe(cpu, now + period * sample, period).Send, "A missing reading did not reset CPU duration.");
    }

    Check(!rules.Observe(cpu, now + period * 23, TimeSpan.Zero).Send, "A gap qualified for CPU continuity.");
    for (var sample = 24; sample < 33; sample++)
    {
        Check(!rules.Observe(cpu, now + period * sample, period).Send, "A gap did not reset CPU continuity.");
    }

    var decision = rules.Observe(cpu, now + period * 33, period);
    Check(decision.Send && decision.Duration == cpu.Duration, "Five minutes of high CPU did not qualify.");
}

void VerifyRetryAndPersistence()
{
    var state = new MonitorState();
    var rules = new AlertRules(state);
    Check(!rules.Observe(memory with { Percent = 90 }, now, period).Send, "Memory alerted at its threshold.");
    Check(!rules.Observe(disk with { Percent = 85 }, now, period).Send, "Disk alerted at its threshold.");
    Check(rules.Observe(memory, now, period).Send, "High memory did not alert immediately.");
    Check(rules.Observe(disk, now, period).Send, "A full disk did not alert immediately.");

    var retryAt = now;
    foreach (var minutes in new[] { 1, 5, 15, 15 })
    {
        rules.Record(memory.Key, SmsOutcome.TemporaryFailure, retryAt);
        retryAt = retryAt.AddMinutes(minutes);
        Check(!rules.Observe(memory, retryAt.AddSeconds(-1), period).Send, "An alert retried too early.");
        Check(rules.Observe(memory, retryAt, period).Send, "An alert failed to retry on schedule.");
    }

    rules.Record(memory.Key, SmsOutcome.Accepted, retryAt);
    rules.Record(disk.Key, SmsOutcome.Accepted, retryAt);
    state.RejectedCredentialRevision = Guid.NewGuid();
    var json = JsonSerializer.Serialize(state, SurveyorJson.Default.MonitorState);
    var restored = JsonSerializer.Deserialize(json, SurveyorJson.Default.MonitorState)
        ?? throw new InvalidOperationException("Alert state could not be restored.");
    var restarted = new AlertRules(restored);
    Check(restored.RejectedCredentialRevision == state.RejectedCredentialRevision, "Credential rejection was lost.");
    Check(!restarted.Observe(memory, retryAt + period, period).Send, "Restart caused a duplicate memory alert.");
    Check(!restarted.Observe(disk with { Percent = null }, retryAt + period, period).Send,
        "An unavailable disk caused an alert.");
    Check(!restarted.Observe(disk, retryAt + period * 2, period).Send, "Remount caused a duplicate disk alert.");
    Check(!restarted.Observe(memory with { Percent = 90 }, retryAt + period * 2, period).Send,
        "Recovery generated an alert.");
    Check(restarted.Observe(memory, retryAt + period * 3, period).Send, "A new episode did not generate an alert.");
    Check(!disk.ToSms("server-01", TimeSpan.Zero).EndsWith(' '), "Disk SMS ends with whitespace.");
}

void VerifyMetrics()
{
    var before = LinuxMetrics.ParseCpu("cpu 100 0 40 800 20 0 0 0 50 0");
    var after = LinuxMetrics.ParseCpu("cpu 120 0 50 860 30 0 0 0 60 0");
    Check(before is not null && after is not null, "CPU counters were not parsed.");
    if (before is { } first && after is { } second)
    {
        Check(LinuxMetrics.CalculateCpu(first, second) == 30, "CPU usage double-counted guest or I/O wait.");
        Check(LinuxMetrics.CalculateCpu(second, first) is null, "Reset CPU counters produced a reading.");
    }

    Check(LinuxMetrics.ParseMemory("MemTotal: 1000 kB\nMemFree: 10 kB\nMemAvailable: 150 kB") == 85,
        "Memory usage did not use MemAvailable.");
    Check(LinuxMetrics.ParseMemory("MemTotal: 1000 kB\nMemFree: 10 kB") is null,
        "Missing memory availability produced a reading.");

    var mounts = LinuxMetrics.ParseMounts(
    [
        "30 1 8:1 / / rw - ext4 /dev/sda1 rw",
        "31 30 8:2 / /boot rw - ext4 /dev/sda2 rw",
        "32 31 8:3 / /boot/efi rw - vfat /dev/sda3 rw",
        "33 30 8:4 / /efi rw - vfat /dev/sda4 rw",
        "34 30 8:5 / /data rw - ext4 /dev/sda5 rw",
        "35 30 8:5 /test /mirror rw - ext4 /dev/sda5 rw",
        "36 30 0:14 / /run rw - tmpfs tmpfs rw",
        "37 30 0:15 / /share rw - nfs storage:/share rw",
        "38 30 8:6 / /mnt/added\\040disk rw - ext4 /dev/sda6 rw"
    ]);
    Check(mounts.Count == 3 && mounts.Any(mount => mount.MountPoint == "/")
        && mounts.Any(mount => mount.MountPoint == "/data" && mount.Aliases.Contains("/mirror"))
        && mounts.Any(mount => mount.MountPoint == "/mnt/added disk"),
        "Disk discovery or filtering returned unexpected mount points.");

    var firstBoot = LinuxMetrics.ParseMounts(["50 1 8:5 / /data rw - ext4 /dev/sda5 rw"], _ => "data-uuid");
    var nextBoot = LinuxMetrics.ParseMounts(["51 1 8:21 / /data rw - ext4 /dev/sdb5 rw"], _ => "data-uuid");
    Check(firstBoot.Single().Key == nextBoot.Single().Key, "Renaming a device changed its alert key.");
}

async Task VerifySms()
{
    using var handler = new FakeFreeMobileHandler();
    using var client = new HttpClient(handler);
    var sms = new FreeMobileSms(client);
    var settings = new Settings { SubscriberId = "test+account", SmsKey = "test&key" };
    var message = cpu.ToSms("server-01", TimeSpan.FromMinutes(5));
    Check(message == "SuperSurveyor | server-01 | CPU usage is 91%, above 90% for 5 minutes.", "Unexpected CPU SMS text.");
    foreach (var item in new[]
    {
        (200, SmsOutcome.Accepted), (402, SmsOutcome.TemporaryFailure), (403, SmsOutcome.CredentialsRejected),
        (429, SmsOutcome.TemporaryFailure), (500, SmsOutcome.TemporaryFailure), (400, SmsOutcome.RequestRejected),
        (302, SmsOutcome.RequestRejected)
    })
    {
        handler.StatusCode = item.Item1;
        Check(await sms.Send(settings, message, CancellationToken.None) == item.Item2, "Unexpected Free Mobile status handling.");
    }

    handler.FailConnection = true;
    Check(await sms.Send(settings, message, CancellationToken.None) == SmsOutcome.TemporaryFailure,
        "A connection failure cannot be retried.");
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
static void VerifyLinuxStorage()
{
    var directory = Directory.CreateTempSubdirectory("supersuveyor-tests-");
    try
    {
        var path = Path.Combine(directory.FullName, "settings.json");
        var settings = new Settings
        {
            SubscriberId = "simulated-user", SmsKey = "simulated-key", CredentialRevision = Guid.NewGuid()
        };
        Storage.Write(path, settings, SurveyorJson.Default.Settings);
        var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Check(File.GetUnixFileMode(path) == privateMode, "The settings file is accessible to another user.");
        var updated = settings with { CpuThreshold = 95 };
        Storage.Write(path, updated, SurveyorJson.Default.Settings);
        var stored = Storage.Read(path, SurveyorJson.Default.Settings);
        Check(stored?.CpuThreshold == 95 && stored.SmsKey == settings.SmsKey, "Settings replacement lost data.");
        Check(File.GetUnixFileMode(path) == privateMode && directory.GetFiles().Length == 1,
            "Replacing settings changed permissions or left a temporary file.");
    }
    finally
    {
        directory.Delete(recursive: true);
    }
}

static void Check(bool passed, string message)
{
    if (!passed)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FakeFreeMobileHandler : HttpMessageHandler
{
    internal int StatusCode { get; set; }
    internal bool FailConnection { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailConnection)
        {
            throw new HttpRequestException("Simulated connection failure.");
        }

        var address = request.RequestUri?.AbsoluteUri;
        if (request.Method != HttpMethod.Post || address != "https://smsapi.free-mobile.fr/sendmsg" || request.Content is null)
        {
            throw new InvalidOperationException("Unexpected SMS request or credentials in the URI.");
        }

        var content = await request.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(content);
        var validContent = request.Content.Headers.ContentType?.MediaType == "application/json"
            && json.RootElement.GetProperty("user").GetString() == "test+account"
            && json.RootElement.GetProperty("pass").GetString() == "test&key"
            && json.RootElement.GetProperty("msg").GetString()?.StartsWith("SuperSurveyor |", StringComparison.Ordinal) == true;
        if (!validContent)
        {
            throw new InvalidOperationException("The SMS JSON request is not encoded correctly.");
        }

        var result = new HttpResponseMessage((HttpStatusCode)StatusCode);
        return result;
    }
}
