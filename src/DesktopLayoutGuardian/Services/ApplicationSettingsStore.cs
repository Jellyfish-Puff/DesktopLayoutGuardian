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
            return await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, JsonOptions, cancellationToken)
                ?? new ApplicationSettings();
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
}

