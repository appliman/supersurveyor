namespace SuperSurveyor;

internal sealed record Settings
{
    public string SubscriberId { get; init; } = "";
    public string SmsKey { get; init; } = "";
    public Guid CredentialRevision { get; init; }
    public double CpuThreshold { get; init; } = 90;
    public double MemoryThreshold { get; init; } = 90;
    public double DiskThreshold { get; init; } = 85;
    public int CpuDurationSeconds { get; init; } = 300;
    public Dictionary<string, double> DiskThresholds { get; init; } = new(StringComparer.Ordinal);

    internal bool IsValid()
    {
        var validCredentials = !string.IsNullOrWhiteSpace(SubscriberId)
            && !string.IsNullOrWhiteSpace(SmsKey) && CredentialRevision != Guid.Empty;
        var validThresholds = IsThreshold(CpuThreshold) && IsThreshold(MemoryThreshold)
            && IsThreshold(DiskThreshold) && CpuDurationSeconds is >= 30 and <= 86400;
        var validMounts = DiskThresholds is not null && DiskThresholds.All(pair =>
            pair.Key.StartsWith('/') && IsThreshold(pair.Value));
        var result = validCredentials && validThresholds && validMounts;
        return result;
    }

    internal static bool IsThreshold(double value)
    {
        var result = value is >= 0 and <= 100;
        return result;
    }
}
