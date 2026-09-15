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
        DeletedDirectory = Path.Combine(DataRoot, "deleted");
        PreviewDirectory = Path.Combine(DataRoot, "previews");
    }

    public string DataRoot { get; }

    public string ProfileDirectory { get; }

    public string HistoryDirectory { get; }

    public string DeletedDirectory { get; }

    public string PreviewDirectory { get; }

    public async Task<DesktopLayoutProfile> SaveAsync(
        DisplaySnapshot display,
        DesktopLayoutSnapshot layout,
        string? previewImageFileName = null,
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
            Name = existing?.Name ?? CreateDefaultName(display.Displays[0]),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            PreviewImageFileName = string.IsNullOrWhiteSpace(previewImageFileName)
                ? existing?.PreviewImageFileName ?? string.Empty
                : Path.GetFileName(previewImageFileName),
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
        var compatibleProfiles = new List<DesktopLayoutProfile>();
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
                compatibleProfiles.Add(candidate);
            }
        }

        var newestCompatibleProfile = compatibleProfiles
            .OrderByDescending(profile => profile.UpdatedAt)
            .FirstOrDefault();
        return newestCompatibleProfile is null
            ? null
            : new DesktopLayoutProfileMatch { Profile = newestCompatibleProfile, IsExactMatch = false };
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

    public async Task<IReadOnlyList<DesktopLayoutProfile>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(HistoryDirectory))
        {
            return [];
        }

        var profiles = new List<DesktopLayoutProfile>();
        foreach (var profilePath in Directory.EnumerateFiles(HistoryDirectory, "*.json", SearchOption.AllDirectories))
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

    public string? GetPreviewPath(DesktopLayoutProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.PreviewImageFileName))
        {
            return null;
        }

        var path = Path.Combine(PreviewDirectory, Path.GetFileName(profile.PreviewImageFileName));
        return File.Exists(path) ? path : null;
    }

    public async Task<DesktopLayoutProfile> RenameAsync(
        string profileId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = newName.Trim();
        if (normalizedName.Length is < 1 or > 50)
        {
            throw new ArgumentException("方案名称应为 1 到 50 个字符。", nameof(newName));
        }

        var targetPath = GetProfilePath(profileId);
        var existing = await TryReadAsync(targetPath, cancellationToken)
            ?? throw new FileNotFoundException("找不到要重命名的显示方案。", targetPath);

        await BackupAsync(existing, targetPath, cancellationToken);
        var renamed = new DesktopLayoutProfile
        {
            Id = existing.Id,
            Name = normalizedName,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.Now,
            PreviewImageFileName = existing.PreviewImageFileName,
            Display = existing.Display,
            Layout = existing.Layout
        };
        await WriteAtomicallyAsync(targetPath, renamed, cancellationToken);
        return renamed;
    }

    public async Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var sourcePath = GetProfilePath(profileId);
        var existing = await TryReadAsync(sourcePath, cancellationToken)
            ?? throw new FileNotFoundException("找不到要删除的显示方案。", sourcePath);

        Directory.CreateDirectory(DeletedDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var destinationPath = Path.Combine(
            DeletedDirectory,
            $"{timestamp}-{SanitizeFileName(existing.Id)}.json");
        File.Move(sourcePath, destinationPath, overwrite: false);
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
        await AtomicJsonFile.WriteAsync(targetPath, profile, JsonOptions, cancellationToken);
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
