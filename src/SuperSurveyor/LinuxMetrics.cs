using System.Globalization;

namespace SuperSurveyor;

internal sealed class LinuxMetrics
{
    private const string CpuPath = "/proc/stat";
    private const string MemoryPath = "/proc/meminfo";
    private const string MountsPath = "/proc/self/mountinfo";
    private const double BytesPerMebibyte = 1024.0 * 1024.0;
    private const double BytesPerGibibyte = BytesPerMebibyte * 1024.0;
    private static readonly char[] FieldSeparators = [' ', '\t'];

    private CpuCounters? _previousCpu;

    internal IReadOnlyList<Measurement> Read(Settings settings)
    {
        var cpu = ReadCpu();
        var memory = ReadMemory();
        var result = new List<Measurement>
        {
            new("cpu", "CPU", cpu, settings.CpuThreshold, TimeSpan.FromSeconds(settings.CpuDurationSeconds)),
            new("memory", "Memory", memory, settings.MemoryThreshold, TimeSpan.Zero)
        };

        try
        {
            var identifiers = ReadDiskIdentifiers();
            foreach (var mount in ParseMounts(File.ReadLines(MountsPath), source =>
            {
                var canonicalSource = ResolveDevice(source);
                var identifier = identifiers.GetValueOrDefault(canonicalSource);
                return identifier;
            }))
            {
                result.Add(ReadDisk(mount, settings));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.Add(new Measurement("disks", "Disk discovery", null, settings.DiskThreshold, TimeSpan.Zero));
        }

        return result;
    }

    internal static CpuCounters? ParseCpu(string line)
    {
        var fields = line.Split(FieldSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 9 || fields[0] != "cpu")
        {
            return null;
        }

        ulong busy = 0;
        ulong total = 0;

        // Guest ticks are already included in the user and nice counters.
        for (var index = 1; index <= 8; index++)
        {
            var parsed = TryParseCounter(fields[index], out var value);
            if (!parsed || value > ulong.MaxValue - total)
            {
                return null;
            }

            total += value;

            if (index is not (4 or 5))
            {
                busy += value;
            }
        }

        var result = new CpuCounters(busy, total);
        return result;
    }

    internal static double? CalculateCpu(CpuCounters before, CpuCounters after)
    {
        if (after.Busy < before.Busy || after.Total <= before.Total)
        {
            return null;
        }

        var busy = after.Busy - before.Busy;
        var total = after.Total - before.Total;
        if (busy > total)
        {
            return null;
        }

        var result = busy * 100.0 / total;
        return result;
    }

    internal static double? ParseMemory(string contents)
    {
        ulong? total = null;
        ulong? available = null;

        using var reader = new StringReader(contents);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = ParseMemoryCounter(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = ParseMemoryCounter(line);
            }

            if (total.HasValue && available.HasValue)
            {
                break;
            }
        }

        if (total is null or 0 || available is null || available > total)
        {
            return null;
        }

        var result = (total.Value - available.Value) * 100.0 / total.Value;
        return result;
    }

    internal static IReadOnlyList<DiskMount> ParseMounts(IEnumerable<string> lines,
        Func<string, string?>? resolveUuid = null)
    {
        var mounts = new List<MountEntry>();
        foreach (var line in lines)
        {
            if (ParseMount(line) is { } mount)
            {
                mounts.Add(mount);
            }
        }

        var visibleMounts = new List<MountEntry>();
        foreach (var atSamePath in mounts.GroupBy(mount => mount.MountPoint, StringComparer.Ordinal))
        {
            // An overmount lists the previous mount's ID as its parent at the same path.
            var parentIds = atSamePath.Select(mount => mount.ParentId).ToHashSet();
            var visible = atSamePath.LastOrDefault(mount => !parentIds.Contains(mount.Id));
            if (visible is not null && IsLocalVolume(visible))
            {
                visibleMounts.Add(visible);
            }
        }

        var result = new List<DiskMount>();
        var identifiedMounts = visibleMounts.Select(mount =>
        {
            var uuid = mount.Source.StartsWith("/dev/", StringComparison.Ordinal)
                ? resolveUuid?.Invoke(mount.Source)
                : null;
            var identity = uuid is null ? GetVolumeIdentity(mount) : $"uuid:{uuid}";
            return (Identity: identity, Uuid: uuid, Mount: mount);
        });
        foreach (var volume in identifiedMounts.GroupBy(item => item.Identity, StringComparer.Ordinal))
        {
            var aliasesForVolume = volume.Select(item => item.Mount);
            var hasSystemRoot = aliasesForVolume.Any(mount => mount.MountPoint == "/");
            var isDedicatedBootVolume = aliasesForVolume.Any(mount => mount.Root == "/" && IsBootMount(mount));
            if (isDedicatedBootVolume && !hasSystemRoot)
            {
                continue;
            }

            var ordered = aliasesForVolume.Where(mount => !IsBootMount(mount))
                .OrderBy(mount => mount.MountPoint != "/")
                .ThenBy(mount => mount.Root != "/")
                .ThenBy(mount => mount.MountPoint.Length)
                .ThenBy(mount => mount.MountPoint, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length == 0)
            {
                continue;
            }

            var primary = ordered[0];
            var aliases = ordered.Select(mount => mount.MountPoint).ToArray();

            // UUIDs maintain the alert state even when the kernel assigns a different device name.
            var uuid = volume.First().Uuid;
            var key = uuid is null ? $"disk:{primary.FileSystem}:{primary.Source}" : $"disk:uuid:{uuid}";
            result.Add(new DiskMount(key, primary.MountPoint, aliases));
        }

        return result;
    }

    private double? ReadCpu()
    {
        try
        {
            using var reader = File.OpenText(CpuPath);
            var line = reader.ReadLine();
            var current = line is null ? null : ParseCpu(line);
            var previous = _previousCpu;
            _previousCpu = current;

            if (current is null || previous is null)
            {
                return null;
            }

            var result = CalculateCpu(previous.Value, current.Value);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _previousCpu = null;
            return null;
        }
    }

    private static double? ReadMemory()
    {
        try
        {
            var result = ParseMemory(File.ReadAllText(MemoryPath));
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Measurement ReadDisk(DiskMount mount, Settings settings)
    {
        var printableMountPoint = string.Concat(mount.MountPoint.Select(character => char.IsControl(character) ? '?' : character));
        var label = $"Disk {printableMountPoint}";
        var threshold = settings.DiskThreshold;
        foreach (var alias in mount.Aliases)
        {
            if (settings.DiskThresholds.TryGetValue(alias, out var configured))
            {
                threshold = configured;
                break;
            }
        }

        foreach (var alias in mount.Aliases)
        {
            if (ReadDiskCapacity(alias) is { } capacity)
            {
                var result = new Measurement(mount.Key, label, capacity.Percent, threshold, TimeSpan.Zero,
                    FormatFreeSpace(capacity.Available));
                return result;
            }
        }

        var unavailable = new Measurement(mount.Key, label, null, threshold, TimeSpan.Zero);
        return unavailable;
    }

    private static (double Percent, long Available)? ReadDiskCapacity(string mountPoint)
    {
        try
        {
            var drive = new DriveInfo(mountPoint);
            var size = drive.TotalSize;
            var available = Math.Max(0, drive.AvailableFreeSpace);
            if (size <= 0 || available > size)
            {
                return null;
            }

            var result = ((size - available) * 100.0 / size, available);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            // A mount can disappear or become inaccessible while the service is reading its capacity.
            return null;
        }
    }

    private static string FormatFreeSpace(long size)
    {
        var result = size >= BytesPerGibibyte
            ? FormattableString.Invariant($"Free space: {size / BytesPerGibibyte:0.#} GiB.")
            : FormattableString.Invariant($"Free space: {size / BytesPerMebibyte:0.#} MiB.");
        return result;
    }

    private static ulong? ParseMemoryCounter(string line)
    {
        var fields = line.Split(FieldSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[2] != "kB" || !TryParseCounter(fields[1], out var parsed))
        {
            return null;
        }

        return parsed;
    }

    private static bool TryParseCounter(string field, out ulong value)
    {
        var result = ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return result;
    }

    private static MountEntry? ParseMount(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var separator = Array.IndexOf(fields, "-");
        if (separator < 6 || fields.Length < separator + 4)
        {
            return null;
        }

        var mountPoint = DecodeMountField(fields[4]);
        if (!mountPoint.StartsWith('/'))
        {
            return null;
        }

        var result = new MountEntry(
            fields[0], fields[1], fields[2], DecodeMountField(fields[3]), mountPoint,
            fields[separator + 1], DecodeMountField(fields[separator + 2]));
        return result;
    }

    private static bool IsBootMount(MountEntry mount)
    {
        var result = mount.MountPoint is "/boot" or "/efi"
            || mount.MountPoint.StartsWith("/boot/", StringComparison.Ordinal)
            || mount.MountPoint.StartsWith("/efi/", StringComparison.Ordinal);
        return result;
    }

    private static bool IsLocalVolume(MountEntry mount)
    {
        var isSwap = mount.FileSystem == "swap";
        var isVirtualDevice = mount.Source == "/dev/fuse"
            || mount.Source.StartsWith("/dev/loop", StringComparison.Ordinal)
            || mount.Source.StartsWith("/dev/ram", StringComparison.Ordinal)
            || mount.Source.StartsWith("/dev/zram", StringComparison.Ordinal);
        var isLocalDevice = mount.Source.StartsWith("/dev/", StringComparison.Ordinal)
            && !isVirtualDevice;
        var isZfsDataset = mount.FileSystem == "zfs";
        var result = !isSwap && (isLocalDevice || isZfsDataset);
        return result;
    }

    private static Dictionary<string, string> ReadDiskIdentifiers()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries("/dev/disk/by-uuid"))
            {
                var device = ResolveDevice(entry);
                if (device != entry)
                {
                    result.TryAdd(device, Path.GetFileName(entry));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A filesystem can still be monitored if udev's UUID links are unavailable.
        }

        return result;
    }

    private static string ResolveDevice(string path)
    {
        try
        {
            var result = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static string GetVolumeIdentity(MountEntry mount)
    {
        // Btrfs subvolumes can have different device numbers while sharing storage.
        // ZFS datasets have their own capacity and are identified by dataset name.
        var result = mount.FileSystem is "btrfs" or "zfs"
            ? $"{mount.FileSystem}:{mount.Source}"
            : mount.Device;
        return result;
    }

    private static string DecodeMountField(string field)
    {
        var result = field.Replace(@"\040", " ", StringComparison.Ordinal)
            .Replace(@"\011", "\t", StringComparison.Ordinal)
            .Replace(@"\012", "\n", StringComparison.Ordinal)
            .Replace(@"\134", @"\", StringComparison.Ordinal);
        return result;
    }

    internal readonly record struct CpuCounters(ulong Busy, ulong Total);

    internal sealed record DiskMount(string Key, string MountPoint, IReadOnlyList<string> Aliases);

    private sealed record MountEntry(
        string Id, string ParentId, string Device, string Root, string MountPoint, string FileSystem, string Source);
}
