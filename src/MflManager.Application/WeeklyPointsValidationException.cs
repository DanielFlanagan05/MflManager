namespace MflManager.Application;

public sealed class WeeklyPointsValidationException(int week) : InvalidOperationException(
    $"Points for week {week} could not be verified with MFL after retrying. Weekly points are unavailable.")
{
    public int Week { get; } = week;
}
