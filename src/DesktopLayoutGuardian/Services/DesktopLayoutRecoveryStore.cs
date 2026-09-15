using System.IO;
using System.Text.Json;
using DesktopLayoutGuardian.Models;

namespace DesktopLayoutGuardian.Services;

public sealed class DesktopLayoutRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public DesktopLayoutRecoveryStore()
    {
        var dataRootOverride = Environment.GetEnvironmentVariable("DLG_DATA_ROOT");
        var dataRoot = string.IsNullOrWhiteSpace(dataRootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopLayoutGuardian")
            : Path.GetFullPath(dataRootOverride);
        RecoveryPath = Path.Combine(dataRoot, "recovery", "last-before-restore.json");
    }

    public string RecoveryPath { get; }

    public async Task SaveAsync(
        DisplaySnapshot display,
        DesktopLayoutSnapshot layout,
        string targetProfileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(layout);

        var snapshot = new DesktopLayoutRecoverySnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            ConfigurationKey = display.ConfigurationKey,
            DisplayName = display.Displays.FirstOrDefault()?.FriendlyName ?? "未知显示器",
            TargetProfileName = targetProfileName,
            Layout = layout
        };

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await AtomicJsonFile.WriteAsync(RecoveryPath, snapshot, JsonOptions, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<DesktopLayoutRecoverySnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(RecoveryPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<DesktopLayoutRecoverySnapshot>(stream, JsonOptions, cancellationToken);
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

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            File.Delete(RecoveryPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
