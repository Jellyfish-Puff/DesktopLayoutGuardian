namespace DesktopLayoutGuardian.Models;

public sealed class DesktopLayoutProfile
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string PreviewImageFileName { get; init; } = string.Empty;

    public DisplaySnapshot Display { get; init; } = new();

    public DesktopLayoutSnapshot Layout { get; init; } = new();
}

public sealed class DesktopLayoutProfileMatch
{
    public required DesktopLayoutProfile Profile { get; init; }

    public bool IsExactMatch { get; init; }
}
