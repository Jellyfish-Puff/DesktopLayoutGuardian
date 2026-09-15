using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DesktopLayoutGuardian.Models;

namespace DesktopLayoutGuardian.Services;

public sealed class DisplayConfigurationService
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;
    private const uint MonitorInfoPrimary = 0x00000001;

    public DisplaySnapshot Capture(string trigger)
    {
        var paths = QueryActivePaths(out var modes);
        var monitorHandles = EnumerateMonitorHandles();
        var displays = new List<DisplayMonitorInfo>(paths.Length);

        foreach (var path in paths)
        {
            var targetName = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);
            var sourceName = GetSourceName(path.sourceInfo.adapterId, path.sourceInfo.id);
            var adapterName = GetAdapterName(path.targetInfo.adapterId);
            var sourceMode = FindSourceMode(path.sourceInfo, modes);
            monitorHandles.TryGetValue(sourceName.viewGdiDeviceName ?? string.Empty, out var nativeMonitor);

            var dpi = TryGetEffectiveDpi(nativeMonitor.Handle);
            var scale = dpi == 0 ? 100 : (int)Math.Round(dpi * 100d / 96d);
            var friendlyName = Clean(targetName.monitorFriendlyDeviceName);
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                friendlyName = Clean(sourceName.viewGdiDeviceName);
            }

            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                friendlyName = "未命名显示器";
            }

            var devicePath = Clean(targetName.monitorDevicePath);
            var adapterDevicePath = Clean(adapterName.adapterDevicePath);
            var technology = TechnologyName(path.targetInfo.outputTechnology);
            var isVirtual = IsVirtualDisplay(
                friendlyName,
                devicePath,
                sourceName.viewGdiDeviceName,
                adapterDevicePath,
                path.targetInfo.outputTechnology);
            var exactIdentity = CreateHash(string.Join("|",
                devicePath,
                adapterDevicePath,
                path.targetInfo.id.ToString()));
            var compatibilityIdentity = CreateHash(string.Join("|",
                isVirtual ? "virtual" : "physical",
                Normalize(friendlyName),
                path.targetInfo.outputTechnology.ToString(),
                targetName.edidManufactureId.ToString(),
                targetName.edidProductCodeId.ToString()));

            var refreshRate = path.targetInfo.refreshRate.Denominator == 0
                ? 0
                : path.targetInfo.refreshRate.Numerator / (double)path.targetInfo.refreshRate.Denominator;

            displays.Add(new DisplayMonitorInfo
            {
                FriendlyName = friendlyName,
                SourceDeviceName = Clean(sourceName.viewGdiDeviceName),
                MonitorDevicePath = devicePath,
                AdapterIdentity = FormatLuid(path.targetInfo.adapterId),
                AdapterDevicePath = adapterDevicePath,
                SourceId = path.sourceInfo.id,
                TargetId = path.targetInfo.id,
                Width = sourceMode?.width is > 0 ? (int)sourceMode.Value.width : nativeMonitor.Width,
                Height = sourceMode?.height is > 0 ? (int)sourceMode.Value.height : nativeMonitor.Height,
                PositionX = sourceMode?.position.x ?? nativeMonitor.Left,
                PositionY = sourceMode?.position.y ?? nativeMonitor.Top,
                ScalePercent = scale,
                Dpi = dpi,
                RefreshRateHz = refreshRate,
                Rotation = RotationName(path.targetInfo.rotation),
                OutputTechnology = technology,
                IsVirtual = isVirtual,
                IsPrimary = nativeMonitor.IsPrimary,
                EdidManufacturerId = targetName.edidManufactureId,
                EdidProductCodeId = targetName.edidProductCodeId,
                ExactIdentityKey = exactIdentity,
                CompatibilityIdentityKey = compatibilityIdentity
            });
        }

        var orderedDisplays = displays
            .OrderBy(display => display.PositionX)
            .ThenBy(display => display.PositionY)
            .ThenBy(display => display.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var configurationMaterial = string.Join("||", orderedDisplays.Select(display => string.Join("|",
            display.ExactIdentityKey,
            $"{display.Width}x{display.Height}",
            display.ScalePercent,
            display.Rotation,
            display.IsPrimary)));

        return new DisplaySnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Trigger = trigger,
            ConfigurationKey = CreateHash(configurationMaterial),
            Displays = orderedDisplays
        };
    }

    private static DISPLAYCONFIG_PATH_INFO[] QueryActivePaths(out DISPLAYCONFIG_MODE_INFO[] modes)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = NativeMethods.GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
            if (result != 0)
            {
                throw new Win32Exception(result, "无法获取显示配置缓冲区大小。");
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            result = NativeMethods.QueryDisplayConfig(
                QdcOnlyActivePaths,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero);

            if (result == ErrorInsufficientBuffer)
            {
                continue;
            }

            if (result != 0)
            {
                throw new Win32Exception(result, "无法查询当前活动显示配置。");
            }

            Array.Resize(ref paths, checked((int)pathCount));
            Array.Resize(ref modes, checked((int)modeCount));
            return paths;
        }

        throw new InvalidOperationException("显示配置正在频繁变化，请稍后重试。");
    }

    private static DISPLAYCONFIG_SOURCE_MODE? FindSourceMode(
        DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo,
        IReadOnlyList<DISPLAYCONFIG_MODE_INFO> modes)
    {
        if (sourceInfo.modeInfoIdx < modes.Count)
        {
            var indexedMode = modes[(int)sourceInfo.modeInfoIdx];
            if (indexedMode.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.Source)
            {
                return indexedMode.modeInfo.sourceMode;
            }
        }

        foreach (var mode in modes)
        {
            if (mode.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.Source &&
                mode.id == sourceInfo.id &&
                SameLuid(mode.adapterId, sourceInfo.adapterId))
            {
                return mode.modeInfo.sourceMode;
            }
        }

        return null;
    }

    private static DISPLAYCONFIG_TARGET_DEVICE_NAME GetTargetName(LUID adapterId, uint targetId)
    {
        var value = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetTargetName,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId
            }
        };

        var result = NativeMethods.DisplayConfigGetDeviceInfo(ref value);
        return result == 0 ? value : default;
    }

    private static DISPLAYCONFIG_SOURCE_DEVICE_NAME GetSourceName(LUID adapterId, uint sourceId)
    {
        var value = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetSourceName,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId
            }
        };

        var result = NativeMethods.DisplayConfigGetDeviceInfo(ref value);
        return result == 0 ? value : default;
    }

    private static DISPLAYCONFIG_ADAPTER_NAME GetAdapterName(LUID adapterId)
    {
        var value = new DISPLAYCONFIG_ADAPTER_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetAdapterName,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>(),
                adapterId = adapterId,
                id = 0
            }
        };

        var result = NativeMethods.DisplayConfigGetDeviceInfo(ref value);
        return result == 0 ? value : default;
    }

    private static Dictionary<string, NativeMonitorInfo> EnumerateMonitorHandles()
    {
        var result = new Dictionary<string, NativeMonitorInfo>(StringComparer.OrdinalIgnoreCase);
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>()
            };

            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                result[Clean(info.szDevice)] = new NativeMonitorInfo(
                    monitor,
                    info.rcMonitor.left,
                    info.rcMonitor.top,
                    info.rcMonitor.right - info.rcMonitor.left,
                    info.rcMonitor.bottom - info.rcMonitor.top,
                    (info.dwFlags & MonitorInfoPrimary) != 0);
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static uint TryGetEffectiveDpi(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return 96;
        }

        try
        {
            return NativeMethods.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.Effective, out var x, out _) == 0
                ? x
                : 96;
        }
        catch (DllNotFoundException)
        {
            return 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private static bool IsVirtualDisplay(
        string? friendlyName,
        string? devicePath,
        string? sourceName,
        string? adapterDevicePath,
        uint technology)
    {
        if (technology is 16 or 17)
        {
            return true;
        }

        var combined = $"{friendlyName}|{devicePath}|{sourceName}|{adapterDevicePath}";
        return new[] { "virtual", "indirect", "idd", "remote", "uuyc", "uu remote", "超级屏", "root#display" }
            .Any(keyword => combined.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string TechnologyName(uint value) => value switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "笔记本内置面板（LVDS）",
        10 => "DisplayPort",
        11 => "内置 DisplayPort",
        15 => "Miracast",
        16 => "间接有线显示器",
        17 => "间接虚拟显示器",
        0x80000000 => "内部显示器",
        0xFFFFFFFF => "未知",
        _ => $"其他（{value}）"
    };

    private static string RotationName(uint value) => value switch
    {
        1 => "横向（0°）",
        2 => "旋转 90°",
        3 => "旋转 180°",
        4 => "旋转 270°",
        _ => $"未知（{value}）"
    };

    private static string FormatLuid(LUID value) => $"{value.HighPart:X8}:{value.LowPart:X8}";

    private static bool SameLuid(LUID left, LUID right) =>
        left.LowPart == right.LowPart && left.HighPart == right.HighPart;

    private static string CreateHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 10));
    }

    private static string Normalize(string? value) => Clean(value).ToUpperInvariant();

    private static string Clean(string? value) => value?.TrimEnd('\0').Trim() ?? string.Empty;

    private readonly record struct NativeMonitorInfo(
        IntPtr Handle,
        int Left,
        int Top,
        int Width,
        int Height,
        bool IsPrimary);

    private static class NativeMethods
    {
        internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [DllImport("user32.dll")]
        internal static extern int QueryDisplayConfig(
            uint flags,
            ref uint pathCount,
            [Out] DISPLAYCONFIG_PATH_INFO[] paths,
            ref uint modeCount,
            [Out] DISPLAYCONFIG_MODE_INFO[] modes,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME requestPacket);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr clip,
            MonitorEnumProc callback,
            IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            IntPtr monitor,
            MONITOR_DPI_TYPE dpiType,
            out uint dpiX,
            out uint dpiY);
    }
}

internal enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
{
    Source = 1,
    Target = 2,
    DesktopImage = 3
}

internal enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
{
    GetSourceName = 1,
    GetTargetName = 2,
    GetAdapterName = 4
}

internal enum MONITOR_DPI_TYPE
{
    Effective = 0
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;

    [MarshalAs(UnmanagedType.Bool)]
    public bool targetAvailable;

    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SOURCE_MODE
{
    public uint width;
    public uint height;
    public uint pixelFormat;
    public POINTL position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong pixelRate;
    public DISPLAYCONFIG_RATIONAL hSyncFreq;
    public DISPLAYCONFIG_RATIONAL vSyncFreq;
    public uint activeWidth;
    public uint activeHeight;
    public uint totalWidth;
    public uint totalHeight;
    public uint videoStandard;
    public uint scanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_TARGET_MODE
{
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
}

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct DISPLAYCONFIG_MODE_INFO_UNION
{
    [FieldOffset(0)]
    public DISPLAYCONFIG_TARGET_MODE targetMode;

    [FieldOffset(0)]
    public DISPLAYCONFIG_SOURCE_MODE sourceMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public DISPLAYCONFIG_MODE_INFO_TYPE infoType;
    public uint id;
    public LUID adapterId;
    public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string? monitorFriendlyDeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string? monitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string? viewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_ADAPTER_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string? adapterDevicePath;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string? szDevice;
}
