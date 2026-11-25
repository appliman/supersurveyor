namespace SuperSurveyor;

internal sealed record BufferedAlert(DateTimeOffset OccurredAt, string Key, double Percent);
