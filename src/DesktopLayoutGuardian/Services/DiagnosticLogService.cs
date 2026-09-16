using System.IO;
using System.Text;
using System.Text.Json;
using DesktopLayoutGuardian.Models;

namespace DesktopLayoutGuardian.Services;

public sealed class DiagnosticLogService
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public DiagnosticLogService()
    {
        var diagnosticRoot = Environment.GetEnvironmentVariable("DLG_DIAGNOSTIC_ROOT");
        LogDirectory = string.IsNullOrWhiteSpace(diagnosticRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopLayoutGuardian",
                "diagnostics")
            : Path.GetFullPath(diagnosticRoot);
        LogFilePath = Path.Combine(LogDirectory, "display-events.jsonl");
    }

    public string LogDirectory { get; }

    public string LogFilePath { get; }

    public async Task AppendAsync(DisplaySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _appendLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded();
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.AppendAllTextAsync(LogFilePath, json + Environment.NewLine, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        await _appendLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                return 0;
            }

            var deletedCount = 0;
            foreach (var path in Directory.EnumerateFiles(LogDirectory, "display-events*.jsonl", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(path);
                deletedCount++;
            }

            return deletedCount;
        }
        finally
        {
            _appendLock.Release();
        }
    }

    private void RotateIfNeeded()
    {
        var logFile = new FileInfo(LogFilePath);
        if (!logFile.Exists || logFile.Length < MaxLogBytes)
        {
            return;
        }

        var previousPath = Path.Combine(LogDirectory, "display-events.previous.jsonl");
        File.Move(LogFilePath, previousPath, overwrite: true);
    }

    public string BuildReport(DisplaySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("桌面布局 - 显示环境诊断");
        builder.AppendLine($"采集时间：{snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"触发原因：{snapshot.Trigger}");
        builder.AppendLine($"配置标识：{snapshot.ConfigurationKey}");
        builder.AppendLine($"活动显示器数量：{snapshot.Displays.Count}");

        for (var index = 0; index < snapshot.Displays.Count; index++)
        {
            var display = snapshot.Displays[index];
            builder.AppendLine();
            builder.AppendLine($"显示器 {index + 1}");
            builder.AppendLine($"  名称：{display.FriendlyName}");
            builder.AppendLine($"  类型：{(display.IsVirtual ? "虚拟显示器" : "实体显示器")}");
            builder.AppendLine($"  分辨率：{display.Width} × {display.Height}");
            builder.AppendLine($"  缩放：{display.ScalePercent}%（{display.Dpi} DPI）");
            builder.AppendLine($"  刷新率：{display.RefreshRateHz:0.###} Hz（不参与布局匹配）");
            builder.AppendLine($"  方向：{display.Rotation}");
            builder.AppendLine($"  输出方式：{display.OutputTechnology}");
            builder.AppendLine($"  主显示器：{(display.IsPrimary ? "是" : "否")}");
            builder.AppendLine($"  桌面坐标：({display.PositionX}, {display.PositionY})");
            builder.AppendLine($"  GDI 设备名：{display.SourceDeviceName}");
            builder.AppendLine($"  设备路径：{display.MonitorDevicePath}");
            builder.AppendLine($"  适配器标识：{display.AdapterIdentity}");
            builder.AppendLine($"  适配器路径：{display.AdapterDevicePath}");
            builder.AppendLine($"  Source/Target：{display.SourceId}/{display.TargetId}");
            builder.AppendLine($"  EDID：{display.EdidManufacturerId:X4}:{display.EdidProductCodeId:X4}");
            builder.AppendLine($"  精确身份：{display.ExactIdentityKey}");
            builder.AppendLine($"  兼容身份：{display.CompatibilityIdentityKey}");
        }

        return builder.ToString();
    }
}
