namespace SuperSurveyor;

internal sealed record Measurement(
    string Key, string Label, double? Percent, double Threshold, TimeSpan Duration, string? Detail = null)
{
    internal string ToSms(string host, TimeSpan duration)
    {
        var reading = FormattableString.Invariant($"{Percent:0.##}%");
        var threshold = FormattableString.Invariant($"{Threshold:0.##}%");
        var prefix = $"SuperSurveyor | {host} | ";
        var result = Key switch
        {
            "cpu" => FormattableString.Invariant(
                $"{prefix}CPU usage is {reading}, above {threshold} for {duration.TotalMinutes:0.##} minutes."),
            "memory" => $"{prefix}Memory usage is {reading} (threshold: {threshold}).",
            _ => $"{prefix}{Label} is {reading} full (threshold: {threshold}). {Detail}".TrimEnd()
        };
        return result;
    }
}
