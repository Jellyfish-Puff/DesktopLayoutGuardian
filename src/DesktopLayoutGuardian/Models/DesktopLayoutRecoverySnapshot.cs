namespace DesktopLayoutGuardian.Models;

public sealed class DesktopLayoutRecoverySnapshot
{
    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset CapturedAt { get; init; }

    public string ConfigurationKey { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string TargetProfileName { get; init; } = string.Empty;

    public DesktopLayoutSnapshot Layout { get; init; } = new();
}

