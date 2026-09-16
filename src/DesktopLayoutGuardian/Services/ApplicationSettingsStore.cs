using System.IO;
using System.Text.Json;
using DesktopLayoutGuardian.Models;

namespace DesktopLayoutGuardian.Services;

public sealed class ApplicationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ApplicationSettingsStore()
    {
        var dataRootOverride = Environment.GetEnvironmentVariable("DLG_DATA_ROOT");
        DataRoot = string.IsNullOrWhiteSpace(dataRootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopLayoutGuardian")
            : Path.GetFullPath(dataRootOverride);
        SettingsPath = Path.Combine(DataRoot, "settings.json");
    }

    public string DataRoot { get; }

    public string SettingsPath { get; }

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return new ApplicationSettings();
        }

        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, JsonOptions, cancellationToken)
                ?? new ApplicationSettings();
            return Normalize(settings);
        }
        catch (JsonException)
        {
            return new ApplicationSettings();
        }
        catch (IOException)
        {
            return new ApplicationSettings();
        }
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Normalize(settings);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DataRoot);
            await AtomicJsonFile.WriteAsync(SettingsPath, settings, JsonOptions, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static ApplicationSettings Normalize(ApplicationSettings settings)
    {
        var strategy = settings.DetectionStrategy switch
        {
            "Fast" => "Fast",
            "Stable" => "Stable",
            _ => "Standard"
        };
        var (displayDelay, stabilityDelay) = strategy switch
        {
            "Fast" => (1600, 450),
            "Stable" => (5000, 1400),
            _ => (3000, 800)
        };

        return new ApplicationSettings
        {
            SchemaVersion = 2,
            StartWithWindows = settings.StartWithWindows,
            ShowRestoreNotifications = settings.ShowRestoreNotifications,
            AutoRestoreEnabled = settings.AutoRestoreEnabled,
            DetectionStrategy = strategy,
            HistoryRetentionPerProfile = Math.Clamp(settings.HistoryRetentionPerProfile, 1, 100),
            DisplayChangeDelayMilliseconds = displayDelay,
            StabilityProbeDelayMilliseconds = stabilityDelay
        };
    }
}
