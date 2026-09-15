namespace DesktopLayoutGuardian.Models;

public sealed class ApplicationSettings
{
    public int SchemaVersion { get; init; } = 1;

    public bool StartWithWindows { get; init; }

    public bool ShowRestoreNotifications { get; init; }

    public int DisplayChangeDelayMilliseconds { get; init; } = 3000;

    public int StabilityProbeDelayMilliseconds { get; init; } = 800;
}

