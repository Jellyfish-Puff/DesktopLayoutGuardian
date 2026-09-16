namespace DesktopLayoutGuardian.Models;

public sealed class ApplicationSettings
{
    public int SchemaVersion { get; init; } = 2;

    public bool StartWithWindows { get; init; }

    public bool ShowRestoreNotifications { get; init; }

    public bool AutoRestoreEnabled { get; init; } = true;

    public string DetectionStrategy { get; init; } = "Standard";

    public int HistoryRetentionPerProfile { get; init; } = 10;

    public int DisplayChangeDelayMilliseconds { get; init; } = 3000;

    public int StabilityProbeDelayMilliseconds { get; init; } = 800;
}
