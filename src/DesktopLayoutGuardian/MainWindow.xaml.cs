using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopLayoutGuardian.Models;
using DesktopLayoutGuardian.Services;

namespace DesktopLayoutGuardian;

public partial class MainWindow : Window
{
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDeviceChange = 0x0219;

    private readonly DisplayConfigurationService _displayService = new();
    private readonly DiagnosticLogService _logService = new();
    private readonly DesktopIconLayoutService _iconLayoutService = new();
    private readonly DesktopLayoutProfileStore _profileStore = new();
    private readonly DispatcherTimer _debounceTimer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DisplaySnapshot? _currentSnapshot;
    private DesktopLayoutProfileMatch? _currentProfileMatch;
    private string _pendingTrigger = "程序启动";
    private string? _lastRestoredConfigurationKey;
    private bool _isRestoring;

    public MainWindow()
    {
        InitializeComponent();

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _debounceTimer.Tick += DebounceTimer_Tick;

        Loaded += async (_, _) => await RefreshAsync("程序启动");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowMessageHook);
        }
    }

    public void RequestRefresh(string trigger)
    {
        Dispatcher.Invoke(() => ScheduleRefresh(trigger));
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmDisplayChange:
                ScheduleRefresh("WM_DISPLAYCHANGE：分辨率或活动显示器发生变化");
                break;
            case WmDeviceChange:
                ScheduleRefresh("WM_DEVICECHANGE：硬件或虚拟设备发生变化");
                break;
            case WmSettingChange:
                ScheduleRefresh("WM_SETTINGCHANGE：显示设置或缩放可能发生变化");
                break;
        }

        return IntPtr.Zero;
    }

    private void ScheduleRefresh(string trigger)
    {
        _pendingTrigger = trigger;
        _debounceTimer.Stop();
        _debounceTimer.Start();
        StatusText.Text = "检测到系统变化，正在等待显示环境稳定…";
    }

    private async void DebounceTimer_Tick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        await RefreshAsync(_pendingTrigger);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync("界面手动检测");
    }

    private async Task RefreshAsync(string trigger)
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            ScheduleRefresh(trigger);
            return;
        }

        try
        {
            StatusText.Text = "正在读取 Windows 显示配置…";
            var snapshot = await Task.Run(() => _displayService.Capture(trigger));
            await _logService.AppendAsync(snapshot);
            _currentSnapshot = snapshot;
            ShowSnapshot(snapshot);
            await UpdateProfileStateAndMaybeRestoreAsync(snapshot);
        }
        catch (Exception exception)
        {
            EnvironmentBadgeText.Text = "检测失败";
            LiveStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(196, 60, 60));
            StatusText.Text = exception.Message;
            DetailsText.Text = exception.ToString();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void ShowSnapshot(DisplaySnapshot snapshot)
    {
        TimestampText.Text = snapshot.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss");
        IdentityText.Text = $"配置标识：{snapshot.ConfigurationKey}";
        DetailsText.Text = _logService.BuildReport(snapshot);

        if (snapshot.Displays.Count == 0)
        {
            EnvironmentBadgeText.Text = "没有活动显示器";
            CurrentDisplayNameText.Text = "未检测到显示输出";
            ResolutionText.Text = "—";
            ScaleText.Text = "—";
            DisplayTypeText.Text = "—";
            OutputText.Text = "Windows 当前没有报告活动显示路径";
            StatusText.Text = "没有检测到活动显示器，请稍后重新检测。";
            return;
        }

        var display = snapshot.Displays.FirstOrDefault(item => item.IsPrimary) ?? snapshot.Displays[0];
        EnvironmentBadgeText.Text = snapshot.Displays.Count == 1
            ? "单显示器环境 · 已识别"
            : $"{snapshot.Displays.Count} 个活动显示器 · 诊断模式";
        LiveStatusDot.Fill = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        CurrentDisplayNameText.Text = display.FriendlyName;
        ResolutionText.Text = $"{display.Width} × {display.Height}";
        ScaleText.Text = $"{display.ScalePercent}% 缩放";
        DisplayTypeText.Text = display.IsVirtual ? "虚拟显示器" : "实体显示器";
        OutputText.Text = $"{display.OutputTechnology} · {display.RefreshRateHz:0.###} Hz · {display.Rotation}";
        StatusText.Text = display.IsVirtual
            ? "已识别为虚拟显示器。请保留本次记录，用于与下一次 UU 超级屏连接进行比较。"
            : "已记录实体显示器身份。切换到其他屏幕后，程序会自动再次采集。";
    }

    private async Task UpdateProfileStateAndMaybeRestoreAsync(DisplaySnapshot snapshot)
    {
        _currentProfileMatch = await _profileStore.FindMatchAsync(snapshot);
        RestoreLayoutButton.IsEnabled = _currentProfileMatch is not null && snapshot.Displays.Count == 1;
        SaveLayoutButton.IsEnabled = snapshot.Displays.Count == 1;

        if (snapshot.Displays.Count != 1)
        {
            LayoutStatusText.Text = "当前版本只支持单显示器环境，暂不保存或恢复布局。";
            return;
        }

        if (_currentProfileMatch is null)
        {
            LayoutStatusText.Text = "这是尚未保存的显示方案。整理好图标后，请点击“保存当前布局”。";
            return;
        }

        var matchText = _currentProfileMatch.IsExactMatch ? "精确匹配" : "兼容匹配";
        LayoutStatusText.Text = $"已找到“{_currentProfileMatch.Profile.Name}”布局（{matchText}，{_currentProfileMatch.Profile.Layout.Icons.Count} 个图标）。";

        if (!string.Equals(_lastRestoredConfigurationKey, snapshot.ConfigurationKey, StringComparison.OrdinalIgnoreCase))
        {
            await RestoreProfileAsync(_currentProfileMatch.Profile, isAutomatic: true);
        }
    }

    private async void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSnapshot is null || _currentSnapshot.Displays.Count != 1)
        {
            StatusText.Text = "当前没有可保存的单显示器环境。";
            return;
        }

        SaveLayoutButton.IsEnabled = false;
        try
        {
            StatusText.Text = "正在通过 Windows Shell 读取桌面图标位置…";
            var layout = _iconLayoutService.Capture();
            if (layout.AutoArrangeEnabled)
            {
                StatusText.Text = "检测到“自动排列图标”已开启，请关闭后再保存布局。";
                System.Windows.MessageBox.Show(
                    this,
                    "请先在桌面空白处单击右键，进入“查看”，关闭“自动排列图标”。“将图标与网格对齐”可以保持开启。",
                    "无法保存布局",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var profile = await _profileStore.SaveAsync(_currentSnapshot, layout);
            _currentProfileMatch = new DesktopLayoutProfileMatch { Profile = profile, IsExactMatch = true };
            _lastRestoredConfigurationKey = _currentSnapshot.ConfigurationKey;
            RestoreLayoutButton.IsEnabled = true;
            LayoutStatusText.Text = $"已保存“{profile.Name}”布局，共 {layout.Icons.Count} 个图标。再次保存前会自动备份旧版本。";
            StatusText.Text = $"布局保存成功：{profile.Name}。切回该显示环境后将自动恢复。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"保存布局失败：{exception.Message}";
            System.Windows.MessageBox.Show(this, exception.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveLayoutButton.IsEnabled = _currentSnapshot?.Displays.Count == 1;
        }
    }

    private async void RestoreLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfileMatch is null)
        {
            StatusText.Text = "当前显示环境还没有保存过布局。";
            return;
        }

        await RestoreProfileAsync(_currentProfileMatch.Profile, isAutomatic: false);
    }

    private async Task RestoreProfileAsync(DesktopLayoutProfile profile, bool isAutomatic)
    {
        if (_isRestoring)
        {
            return;
        }

        _isRestoring = true;
        RestoreLayoutButton.IsEnabled = false;
        try
        {
            DesktopLayoutRestoreResult? result = null;
            Exception? lastException = null;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    result = _iconLayoutService.Restore(profile.Layout);
                    break;
                }
                catch (COMException exception) when (attempt < 3)
                {
                    lastException = exception;
                    await Task.Delay(700);
                }
            }

            if (result is null)
            {
                throw lastException ?? new InvalidOperationException("Windows 桌面暂时不可用。");
            }

            _lastRestoredConfigurationKey = _currentSnapshot?.ConfigurationKey;
            var mode = isAutomatic ? "自动" : "手动";
            var extra = result.NewIconCount > 0
                ? $"，另有 {result.NewIconCount} 个新图标保持原位"
                : string.Empty;
            StatusText.Text = $"已{mode}恢复“{profile.Name}”：定位 {result.RestoredCount} 个图标{extra}。";
            LayoutStatusText.Text = result.MissingCount > 0
                ? $"布局已恢复；有 {result.MissingCount} 个已保存图标当前不存在。"
                : $"“{profile.Name}”布局已恢复完成。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"恢复布局失败：{exception.Message}";
            if (!isAutomatic)
            {
                System.Windows.MessageBox.Show(this, exception.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isRestoring = false;
            RestoreLayoutButton.IsEnabled = _currentProfileMatch is not null;
        }
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSnapshot is null)
        {
            StatusText.Text = "当前还没有可复制的诊断信息。";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_logService.BuildReport(_currentSnapshot));
            StatusText.Text = "完整诊断信息已复制到剪贴板。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制失败：{exception.Message}";
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logService.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _logService.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法打开日志文件夹：{exception.Message}";
        }
    }
}
