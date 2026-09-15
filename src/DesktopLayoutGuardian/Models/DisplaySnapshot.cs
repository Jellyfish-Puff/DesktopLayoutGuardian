namespace DesktopLayoutGuardian.Models;

public sealed class DisplaySnapshot
{
    public DateTimeOffset CapturedAt { get; init; }

    public string Trigger { get; init; } = string.Empty;

    public string ConfigurationKey { get; init; } = string.Empty;

    public IReadOnlyList<DisplayMonitorInfo> Displays { get; init; } = [];
}

public sealed class DisplayMonitorInfo
{
    public string FriendlyName { get; init; } = string.Empty;

    public string SourceDeviceName { get; init; } = string.Empty;

    public string MonitorDevicePath { get; init; } = string.Empty;

    public string AdapterIdentity { get; init; } = string.Empty;

    public string AdapterDevicePath { get; init; } = string.Empty;

    public uint SourceId { get; init; }

    public uint TargetId { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int PositionX { get; init; }

    public int PositionY { get; init; }

    public int ScalePercent { get; init; }

    public uint Dpi { get; init; }

    public double RefreshRateHz { get; init; }

    public string Rotation { get; init; } = string.Empty;

    public string OutputTechnology { get; init; } = string.Empty;

    public bool IsVirtual { get; init; }

    public bool IsPrimary { get; init; }

    public ushort EdidManufacturerId { get; init; }

    public ushort EdidProductCodeId { get; init; }

    public string ExactIdentityKey { get; init; } = string.Empty;

    public string CompatibilityIdentityKey { get; init; } = string.Empty;
}
