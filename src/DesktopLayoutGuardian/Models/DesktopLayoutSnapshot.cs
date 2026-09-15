namespace DesktopLayoutGuardian.Models;

public sealed class DesktopLayoutSnapshot
{
    public DateTimeOffset CapturedAt { get; init; }

    public bool AutoArrangeEnabled { get; init; }

    public int HorizontalSpacing { get; init; }

    public int VerticalSpacing { get; init; }

    public IReadOnlyList<DesktopIconPosition> Icons { get; init; } = [];
}

public sealed class DesktopIconPosition
{
    public string Identity { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public int X { get; init; }

    public int Y { get; init; }
}

public sealed class DesktopLayoutRestoreResult
{
    public int RestoredCount { get; init; }

    public int MissingCount { get; init; }

    public int NewIconCount { get; init; }
}
