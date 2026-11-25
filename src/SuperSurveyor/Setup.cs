using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SuperSurveyor;

[SupportedOSPlatform("linux")]
internal sealed partial class Setup(FreeMobileSms sms, string settingsPath)
{
    internal async Task<int> Run(CancellationToken cancellationToken)
    {
        if (GetEffectiveUserId() != 0 || Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Run sudo supersurveyor configure from an interactive terminal.");
            return 1;
        }

        try
        {
            var directory = Path.GetDirectoryName(settingsPath) ?? throw new InvalidDataException();
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var setupLock = new FileStream(Path.Combine(directory, ".configure.lock"), new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            var existing = Storage.Read(settingsPath, SurveyorJson.Default.Settings);
            var settings = existing ?? new Settings();
            var replaceCredentials = existing is null || !settings.IsValid()
                || Confirm("Replace or revalidate Free Mobile credentials?", cancellationToken);

            if (replaceCredentials)
            {
                settings = settings with
                {
                    SubscriberId = ReadRequired("Free Mobile subscriber ID: ", cancellationToken),
                    SmsKey = ReadSecret("Free Mobile SMS notification key: ", cancellationToken),
                    CredentialRevision = Guid.NewGuid()
                };
            }

            settings = settings with
            {
                CpuThreshold = ReadNumber("CPU threshold (%)", settings.CpuThreshold, 0, 100, false, cancellationToken),
                CpuDurationSeconds = (int)ReadNumber("Continuous CPU duration (seconds)", settings.CpuDurationSeconds,
                    30, 86400, true, cancellationToken),
                MemoryThreshold = ReadNumber("Memory threshold (%)", settings.MemoryThreshold, 0, 100, false, cancellationToken),
                DiskThreshold = ReadNumber("Default disk threshold (%)", settings.DiskThreshold, 0, 100, false, cancellationToken)
            };

            if (Confirm("Replace the per-volume threshold list?", cancellationToken))
            {
                settings = settings with { DiskThresholds = ReadDiskThresholds(settings.DiskThreshold, cancellationToken) };
            }

            var quietHours = ReadQuietHours(settings.QuietHours ?? new(), cancellationToken);
            settings = settings with { QuietHours = quietHours };

            if (replaceCredentials)
            {
                var test = await sms.Send(settings,
                    $"SuperSurveyor | {Environment.MachineName} | Test message. SMS notifications are configured.",
                    cancellationToken);
                if (test != SmsOutcome.Accepted)
                {
                    var reason = test switch
                    {
                        SmsOutcome.CredentialsRejected => "Free Mobile rejected the credentials. Check your subscriber ID, key, and SMS option.",
                        SmsOutcome.TemporaryFailure => "Free Mobile could not accept the test SMS. Try again later.",
                        _ => "Free Mobile rejected the test SMS request."
                    };
                    Console.Error.WriteLine(reason);
                    Console.Error.WriteLine("Settings were not changed.");
                    return 1;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Storage.Write(settingsPath, settings, SurveyorJson.Default.Settings);
            var enabled = await Systemctl("enable", cancellationToken);
            var started = enabled && await Systemctl("restart", cancellationToken);
            if (!started)
            {
                Console.Error.WriteLine("Settings were saved. Run sudo systemctl enable --now supersurveyor to start the service.");
                return 1;
            }

            Console.WriteLine("Settings saved. SuperSurveyor is running.");
            return 0;
        }
        catch (EndOfStreamException)
        {
            Console.Error.WriteLine("Setup was cancelled. Settings were not changed.");
            return 1;
        }
    }

    private static async Task<bool> Systemctl(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/systemctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("supersurveyor.service");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var drainOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var drainErrors = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(drainOutput, drainErrors, process.WaitForExitAsync(cancellationToken));
            var result = process.ExitCode == 0;
            return result;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static Dictionary<string, double> ReadDiskThresholds(double defaultValue, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        while (true)
        {
            var path = ReadLine("Mount path (press Enter to finish): ", cancellationToken);
            if (path.Length == 0)
            {
                return result;
            }

            if (!path.StartsWith('/'))
            {
                Console.WriteLine("Enter an absolute mount path, such as /data.");
                continue;
            }

            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            result[normalized] = ReadNumber("Threshold (%)", defaultValue, 0, 100, false, cancellationToken);
        }
    }

    private static QuietHours ReadQuietHours(QuietHours current, CancellationToken cancellationToken)
    {
        Console.WriteLine("SMS quiet hours use the server's local time.");
        while (true)
        {
            var result = new QuietHours
            {
                Start = ReadTime("Evening quiet hours start", current.Start, cancellationToken),
                WeekdayResume = ReadTime("Weekday SMS resume time", current.WeekdayResume, cancellationToken),
                WeekendResume = ReadTime("Weekend SMS resume time", current.WeekendResume, cancellationToken)
            };
            if (result.IsValid())
            {
                return result;
            }

            Console.WriteLine("The evening start must be later than both morning resume times.");
            current = result;
        }
    }

    private static TimeOnly ReadTime(string label, TimeOnly currentValue, CancellationToken cancellationToken)
    {
        var formatted = currentValue.ToString("HH:mm", CultureInfo.InvariantCulture);
        while (true)
        {
            var input = ReadLine($"{label} (HH:mm) [{formatted}]: ", cancellationToken);
            if (input.Length == 0)
            {
                return currentValue;
            }

            if (TimeOnly.TryParseExact(input, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            {
                return result;
            }

            Console.WriteLine("Enter a time in 24-hour HH:mm format, such as 08:30.");
        }
    }

    private static double ReadNumber(string label, double currentValue, double minimum, double maximum,
        bool integersOnly, CancellationToken cancellationToken)
    {
        while (true)
        {
            var input = ReadLine(FormattableString.Invariant($"{label} [{currentValue:0.##}]: "), cancellationToken);
            if (input.Length == 0)
            {
                return currentValue;
            }

            var parsed = double.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value);
            var inRange = value >= minimum && value <= maximum;
            var validPrecision = !integersOnly || value == Math.Truncate(value);
            if (parsed && inRange && validPrecision)
            {
                return value;
            }

            var format = integersOnly ? "a whole number" : "a number";
            Console.WriteLine(FormattableString.Invariant($"Enter {format} between {minimum} and {maximum}."));
        }
    }

    private static string ReadRequired(string prompt, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = ReadLine(prompt, cancellationToken);
            if (result.Length > 0)
            {
                return result;
            }

            Console.WriteLine("This value is required.");
        }
    }

    private static bool Confirm(string prompt, CancellationToken cancellationToken)
    {
        var answer = ReadLine($"{prompt} [y/N]: ", cancellationToken);
        var result = answer.Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static string ReadLine(string prompt, CancellationToken cancellationToken)
    {
        Console.Write(prompt);
        var input = Console.ReadLine();
        cancellationToken.ThrowIfCancellationRequested();
        var result = input?.Trim() ?? throw new EndOfStreamException();
        return result;
    }

    private static string ReadSecret(string prompt, CancellationToken cancellationToken)
    {
        while (true)
        {
            Console.Write(prompt);
            var buffer = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    throw new EndOfStreamException();
                }

                if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
                {
                    buffer.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                }
            }

            Console.WriteLine();
            var result = buffer.ToString().Trim();
            if (result.Length > 0)
            {
                return result;
            }

            Console.WriteLine("This value is required.");
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
