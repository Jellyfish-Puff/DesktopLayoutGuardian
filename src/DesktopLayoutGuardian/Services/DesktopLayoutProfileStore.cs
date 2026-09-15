using System.IO;
using System.Text.Json;
using DesktopLayoutGuardian.Models;

namespace DesktopLayoutGuardian.Services;

public sealed class DesktopLayoutProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public DesktopLayoutProfileStore()
    {
        var dataRootOverride = Environment.GetEnvironmentVariable("DLG_DATA_ROOT");
        DataRoot = string.IsNullOrWhiteSpace(dataRootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopLayoutGuardian")
            : Path.GetFullPath(dataRootOverride);
        ProfileDirectory = Path.Combine(DataRoot, "profiles");
        HistoryDirectory = Path.Combine(DataRoot, "history");
    }

    public string DataRoot { get; }

    public string ProfileDirectory { get; }

    public string HistoryDirectory { get; }

    public async Task<DesktopLayoutProfile> SaveAsync(
        DisplaySnapshot display,
        DesktopLayoutSnapshot layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(layout);

        if (display.Displays.Count != 1)
        {
            throw new InvalidOperationException("当前版本只支持恰好一个活动显示器的布局。");
        }

        Directory.CreateDirectory(ProfileDirectory);
        var targetPath = GetProfilePath(display.ConfigurationKey);
        var existing = await TryReadAsync(targetPath, cancellationToken);
        if (existing is not null)
        {
            await BackupAsync(existing, targetPath, cancellationToken);
        }

        var now = DateTimeOffset.Now;
        var profile = new DesktopLayoutProfile
        {
            Id = display.ConfigurationKey,
            Name = CreateDefaultName(display.Displays[0]),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Display = display,
            Layout = layout
        };

        await WriteAtomicallyAsync(targetPath, profile, cancellationToken);
        return profile;
    }

    public async Task<DesktopLayoutProfileMatch?> FindMatchAsync(
        DisplaySnapshot display,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (display.Displays.Count != 1)
        {
            return null;
        }

        var exact = await TryReadAsync(GetProfilePath(display.ConfigurationKey), cancellationToken);
        if (exact is not null)
        {
            return new DesktopLayoutProfileMatch { Profile = exact, IsExactMatch = true };
        }

        if (!Directory.Exists(ProfileDirectory))
        {
            return null;
        }

        var currentMonitor = display.Displays[0];
        foreach (var profilePath in Directory.EnumerateFiles(ProfileDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = await TryReadAsync(profilePath, cancellationToken);
            if (candidate?.Display.Displays.Count != 1)
            {
                continue;
            }

            var savedMonitor = candidate.Display.Displays[0];
            if (string.Equals(
                    savedMonitor.CompatibilityIdentityKey,
                    currentMonitor.CompatibilityIdentityKey,
                    StringComparison.OrdinalIgnoreCase) &&
                savedMonitor.Width == currentMonitor.Width &&
                savedMonitor.Height == currentMonitor.Height &&
                savedMonitor.ScalePercent == currentMonitor.ScalePercent &&
                string.Equals(savedMonitor.Rotation, currentMonitor.Rotation, StringComparison.Ordinal))
            {
                return new DesktopLayoutProfileMatch { Profile = candidate, IsExactMatch = false };
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<DesktopLayoutProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ProfileDirectory))
        {
            return [];
        }

        var profiles = new List<DesktopLayoutProfile>();
        foreach (var profilePath in Directory.EnumerateFiles(ProfileDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = await TryReadAsync(profilePath, cancellationToken);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles.OrderByDescending(profile => profile.UpdatedAt).ToArray();
    }

    private async Task BackupAsync(
        DesktopLayoutProfile existing,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var profileHistoryDirectory = Path.Combine(HistoryDirectory, SanitizeFileName(existing.Id));
        Directory.CreateDirectory(profileHistoryDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupPath = Path.Combine(profileHistoryDirectory, $"{timestamp}.json");
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task WriteAtomicallyAsync(
        string targetPath,
        DesktopLayoutProfile profile,
        CancellationToken cancellationToken)
    {
        var temporaryPath = targetPath + ".tmp";
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, targetPath, overwrite: true);
    }

    private static async Task<DesktopLayoutProfile?> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<DesktopLayoutProfile>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string GetProfilePath(string configurationKey) =>
        Path.Combine(ProfileDirectory, $"{SanitizeFileName(configurationKey)}.json");

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
    }

    private static string CreateDefaultName(DisplayMonitorInfo monitor)
    {
        if (monitor.IsVirtual && monitor.AdapterDevicePath.Contains("ROOT#DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return "UU 超级屏";
        }

        if (monitor.OutputTechnology.Contains("内部", StringComparison.OrdinalIgnoreCase))
        {
            return "笔记本内屏";
        }

        return monitor.FriendlyName;
    }
}
