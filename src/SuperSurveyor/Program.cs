using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SuperSurveyor;

internal static class Program
{
    private const string SettingsPath = "/etc/supersuveyor/settings.json";
    private const string StatePath = "/var/lib/supersuveyor/state.json";

    internal static async Task<int> Main(string[] args)
    {
        var command = args.Length == 0 ? "" : args[0];
        if (args.Length == 1 && (command == "--help" || command == "-h"))
        {
            PrintHelp();
            return 0;
        }

        if (args.Length > 1 || (command != "" && command != "configure" && command != "monitor"))
        {
            PrintHelp();
            return 2;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("SuperSurveyor requires Linux.");
            return 1;
        }

        using var shutdown = new CancellationTokenSource();
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator()
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var sms = new FreeMobileSms(client);

        try
        {
            if (command == "monitor")
            {
                var credentialsDirectory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
                if (string.IsNullOrWhiteSpace(credentialsDirectory))
                {
                    Console.Error.WriteLine("Start the monitor with systemctl start supersuveyor.");
                    return 1;
                }

                var settings = Storage.Read(Path.Combine(credentialsDirectory, "settings.json"), SurveyorJson.Default.Settings);
                if (settings is null || !settings.IsValid())
                {
                    Console.Error.WriteLine("Settings are invalid. Run sudo supersuveyor configure.");
                    return 1;
                }

                await new Monitor(sms).Run(settings, StatePath, shutdown.Token);
                return 0;
            }

            if (command == "" && File.Exists(SettingsPath))
            {
                Console.WriteLine("SuperSurveyor is configured. Run sudo supersuveyor configure to change its settings.");
                return 0;
            }

            var result = await new Setup(sms, SettingsPath).Run(shutdown.Token);
            return result;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            Console.WriteLine("SuperSurveyor stopped.");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // File exceptions can contain filenames or raw configuration values.
            Console.Error.WriteLine("SuperSurveyor could not read or write its settings or alert state.");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: sudo supersuveyor [configure]

            Run without arguments to set up SuperSurveyor on this machine.
            Run configure to change thresholds or replace Free Mobile credentials.
            """);
    }
}
