using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopLayoutGuardian.Models;
using DesktopLayoutGuardian.Services;
using DesktopLayoutGuardian.ViewModels;

namespace DesktopLayoutGuardian;

public partial class MainWindow : Window
{
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDeviceChange = 0x0219;
    private const int WmDpiChanged = 0x02E0;

    private readonly DisplayConfigurationService _displayService = new();
    private readonly DiagnosticLogService _logService = new();
    private readonly DesktopIconLayoutService _iconLayoutService = new();
    private readonly DesktopLayoutProfileStore _profileStore = new();
    private readonly DesktopLayoutRecoveryStore _recoveryStore = new();
    private readonly DesktopLayoutPreviewService _previewService = new();
    private readonly ApplicationSettingsStore _settingsStore;
    private readonly StartupRegistrationService _startupService;
    private readonly DispatcherTimer _debounceTimer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _desktopOperationLock = new(1, 1);

    private ApplicationSettings _settings = new();
    private DisplaySnapshot? _currentSnapshot;
    private DesktopLayoutProfileMatch? _currentProfileMatch;
    private DesktopLayoutRecoverySnapshot? _undoSnapshot;
    private string _pendingTrigger = "程序启动";
    private string _traySummary = "正在启动";
    private string? _lastRestoredConfigurationKey;
    private int _displayChangeRevision;
    private bool _isDesktopOperationRunning;
    private bool _isLoadingSettings;

    public MainWindow(ApplicationSettingsStore settingsStore, StartupRegistrationService startupService)
    {
        _settingsStore = settingsStore;
        _startupService = startupService;
        InitializeComponent();

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background);
        _debounceTimer.Tick += DebounceTimer_Tick;
        Loaded += MainWindow_Loaded;
    }

    public event EventHandler<MainWindowCommandState>? CommandStateChanged;

    public event EventHandler<MainWindowNotification>? NotificationRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowMessageHook);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await LoadSettingsAsync();
        _undoSnapshot = await _recoveryStore.LoadAsync();
        await RefreshAsync("程序启动");
    }

    public void RequestRefresh(string trigger)
    {
        Dispatcher.Invoke(() => ScheduleRefresh(trigger));
    }

    public Task SaveCurrentLayoutAsync() => SaveLayoutAsync();

    public async Task RestoreCurrentLayoutAsync()
    {
        if (_currentProfileMatch is null)
        {
            SetStatus("当前显示环境还没有保存过布局。", "当前环境没有方案");
            return;
        }

        await RestoreProfileAsync(_currentProfileMatch.Profile, isAutomatic: false);
    }

    public async Task UndoLastRestoreAsync()
    {
        var undoSnapshot = _undoSnapshot ?? await _recoveryStore.LoadAsync();
        if (undoSnapshot is null)
        {
            SetStatus("当前没有可以撤销的恢复操作。", "没有可撤销操作");
            return;
        }

        if (_currentSnapshot is null ||
            !string.Equals(
                undoSnapshot.ConfigurationKey,
                _currentSnapshot.ConfigurationKey,
                StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("最近的撤销快照属于另一个显示环境，请切回对应屏幕后再试。", "撤销快照属于其他屏幕");
            return;
        }

        if (!await _desktopOperationLock.WaitAsync(0))
        {
            SetStatus("另一个桌面操作正在执行，请稍后再试。", "桌面操作进行中");
            return;
        }

        _isDesktopOperationRunning = true;
        PublishCommandState();
        try
        {
            if (!await IsDisplayConfigurationCurrentAsync(undoSnapshot.ConfigurationKey))
            {
                ScheduleRefresh("撤销前确认：显示环境已变化");
                return;
            }

            SetStatus("正在撤销最近一次布局恢复…", "正在撤销恢复");
            var result = await RestoreWithRetryAsync(undoSnapshot.Layout);
            await _recoveryStore.ClearAsync();
            _undoSnapshot = null;
            var extra = result.NewIconCount > 0 ? $"，{result.NewIconCount} 个新图标保持原位" : string.Empty;
            SetStatus($"已撤销最近一次恢复：定位 {result.RestoredCount} 个图标{extra}。", "撤销完成");
            LayoutStatusText.Text = "已恢复到自动操作前的桌面排列。";

            if (_settings.ShowRestoreNotifications)
            {
                RequestNotification("桌面布局", "已撤销最近一次布局恢复。", isError: false);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"撤销失败：{exception.Message}", "撤销失败");
            RequestNotification("桌面布局撤销失败", exception.Message, isError: true);
            if (IsVisible)
            {
                System.Windows.MessageBox.Show(this, exception.Message, "撤销失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isDesktopOperationRunning = false;
            _desktopOperationLock.Release();
            PublishCommandState();
        }
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
            case WmDpiChanged:
                ScheduleRefresh("WM_DPICHANGED：显示缩放发生变化");
                break;
            case WmSettingChange:
                ScheduleRefresh("WM_SETTINGCHANGE：显示设置可能发生变化");
                break;
        }

        return IntPtr.Zero;
    }

    private void ScheduleRefresh(string trigger)
    {
        _displayChangeRevision++;
        _pendingTrigger = trigger;
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_settings.DisplayChangeDelayMilliseconds, 1500, 8000));
        _debounceTimer.Stop();
        _debounceTimer.Start();
        SetStatus("检测到系统变化，正在等待显示环境稳定…", "等待显示环境稳定");
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
            SetStatus("正在读取 Windows 显示配置…", "正在检测显示环境");
            var revisionAtStart = _displayChangeRevision;
            var firstSnapshot = await Task.Run(() => _displayService.Capture(trigger));

            SetStatus("正在确认分辨率和缩放已经稳定…", "正在确认显示环境");
            await Task.Delay(Math.Clamp(_settings.StabilityProbeDelayMilliseconds, 400, 2500));
            var stableSnapshot = await Task.Run(() => _displayService.Capture(trigger));

            if (revisionAtStart != _displayChangeRevision)
            {
                SetStatus("显示环境在确认期间再次变化，等待下一次检测…", "显示环境仍在变化");
                return;
            }

            if (!string.Equals(
                    firstSnapshot.ConfigurationKey,
                    stableSnapshot.ConfigurationKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                ScheduleRefresh("稳定性确认：显示配置仍在变化");
                return;
            }

            await _logService.AppendAsync(stableSnapshot);
            _currentSnapshot = stableSnapshot;
            ShowSnapshot(stableSnapshot);
            await UpdateProfileStateAndMaybeRestoreAsync(stableSnapshot);
        }
        catch (Exception exception)
        {
            EnvironmentBadgeText.Text = "检测失败";
            LiveStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(196, 60, 60));
            DetailsText.Text = exception.ToString();
            SetStatus($"显示环境检测失败：{exception.Message}", "显示环境检测失败");
            RequestNotification("桌面布局检测失败", exception.Message, isError: true);
        }
        finally
        {
            _refreshLock.Release();
            PublishCommandState();
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
            SetStatus("没有检测到活动显示器，请稍后重新检测。", "没有活动显示器");
            return;
        }

        var display = snapshot.Displays.FirstOrDefault(item => item.IsPrimary) ?? snapshot.Displays[0];
        EnvironmentBadgeText.Text = snapshot.Displays.Count == 1
            ? "单显示器环境 · 已稳定"
            : $"{snapshot.Displays.Count} 个活动显示器 · 只读模式";
        LiveStatusDot.Fill = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        CurrentDisplayNameText.Text = display.FriendlyName;
        ResolutionText.Text = $"{display.Width} × {display.Height}";
        ScaleText.Text = $"{display.ScalePercent}% 缩放";
        DisplayTypeText.Text = display.IsVirtual ? "虚拟显示器" : "实体显示器";
        OutputText.Text = $"{display.OutputTechnology} · {display.RefreshRateHz:0.###} Hz · {display.Rotation}";
        _traySummary = $"{display.FriendlyName} · {display.Width}×{display.Height}";
    }

    private async Task UpdateProfileStateAndMaybeRestoreAsync(DisplaySnapshot snapshot)
    {
        _currentProfileMatch = await _profileStore.FindMatchAsync(snapshot);
        _undoSnapshot = await _recoveryStore.LoadAsync();
        PublishCommandState();
        if (ProfilesPanel.Visibility == Visibility.Visible)
        {
            await LoadProfilesAsync();
        }
        else if (HistoryPanel.Visibility == Visibility.Visible)
        {
            await LoadHistoryAsync();
        }

        if (snapshot.Displays.Count != 1)
        {
            LayoutStatusText.Text = "当前版本只支持单显示器环境，暂不保存或恢复布局。";
            SetStatus("已进入只读模式，不会移动桌面图标。", "多显示器只读模式");
            return;
        }

        if (_currentProfileMatch is null)
        {
            LayoutStatusText.Text = "这是尚未保存的显示方案。整理好图标后，请点击“保存当前布局”。";
            SetStatus("显示环境已稳定；尚未保存对应布局。", $"{snapshot.Displays[0].FriendlyName} · 未保存方案");
            return;
        }

        var matchText = _currentProfileMatch.IsExactMatch ? "精确匹配" : "兼容匹配";
        LayoutStatusText.Text = $"已找到“{_currentProfileMatch.Profile.Name}”布局（{matchText}，{_currentProfileMatch.Profile.Layout.Icons.Count} 个图标）。";

        if (!string.Equals(_lastRestoredConfigurationKey, snapshot.ConfigurationKey, StringComparison.OrdinalIgnoreCase))
        {
            await RestoreProfileAsync(_currentProfileMatch.Profile, isAutomatic: true);
        }
        else
        {
            SetStatus("显示环境已稳定，当前布局已经处理。", $"{_currentProfileMatch.Profile.Name} · 保护中");
        }
    }

    private async void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        await SaveLayoutAsync();
    }

    private async Task SaveLayoutAsync()
    {
        if (_currentSnapshot is null || _currentSnapshot.Displays.Count != 1)
        {
            SetStatus("当前没有可保存的单显示器环境。", "当前环境不可保存");
            return;
        }

        if (!await _desktopOperationLock.WaitAsync(0))
        {
            SetStatus("另一个桌面操作正在执行，请稍后再试。", "桌面操作进行中");
            return;
        }

        _isDesktopOperationRunning = true;
        PublishCommandState();
        try
        {
            var expectedConfigurationKey = _currentSnapshot.ConfigurationKey;
            if (!await IsDisplayConfigurationCurrentAsync(expectedConfigurationKey))
            {
                ScheduleRefresh("保存前确认：显示环境已变化");
                return;
            }

            SetStatus("正在通过 Windows Shell 读取桌面图标位置…", "正在保存布局");
            var layout = await CaptureLayoutWithRetryAsync();
            if (layout.AutoArrangeEnabled)
            {
                SetStatus("检测到“自动排列图标”已开启，请关闭后再保存布局。", "自动排列已开启");
                if (IsVisible)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        "请先在桌面空白处单击右键，进入“查看”，关闭“自动排列图标”。“将图标与网格对齐”可以保持开启。",
                        "无法保存布局",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    RequestNotification("无法保存布局", "请先关闭桌面的“自动排列图标”。", isError: true);
                }
                return;
            }

            if (!await IsDisplayConfigurationCurrentAsync(expectedConfigurationKey))
            {
                ScheduleRefresh("保存后确认：显示环境已变化");
                return;
            }

            string? previewFileName = null;
            string? previewWarning = null;
            try
            {
                SetStatus("正在生成不包含其他窗口的桌面布局预览…", "正在生成布局预览");
                previewFileName = await _previewService.CreateAsync(_currentSnapshot, layout);
            }
            catch (Exception exception) when (exception is ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                previewWarning = exception.Message;
            }

            var profile = await _profileStore.SaveAsync(_currentSnapshot, layout, previewFileName);
            _currentProfileMatch = new DesktopLayoutProfileMatch { Profile = profile, IsExactMatch = true };
            _lastRestoredConfigurationKey = _currentSnapshot.ConfigurationKey;
            LayoutStatusText.Text = $"已保存“{profile.Name}”布局，共 {layout.Icons.Count} 个图标。再次保存前会自动备份旧版本。";
            SetStatus(
                previewWarning is null
                    ? $"布局和桌面预览保存成功：{profile.Name}。切回该显示环境后将自动恢复。"
                    : $"布局保存成功，但预览生成失败：{previewWarning}",
                $"{profile.Name} · 已保存");

            if (ProfilesPanel.Visibility == Visibility.Visible)
            {
                await LoadProfilesAsync();
            }
        }
        catch (Exception exception)
        {
            SetStatus($"保存布局失败：{exception.Message}", "布局保存失败");
            RequestNotification("桌面布局保存失败", exception.Message, isError: true);
            if (IsVisible)
            {
                System.Windows.MessageBox.Show(this, exception.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isDesktopOperationRunning = false;
            _desktopOperationLock.Release();
            PublishCommandState();
        }
    }

    private async void RestoreLayout_Click(object sender, RoutedEventArgs e)
    {
        await RestoreCurrentLayoutAsync();
    }

    private async void UndoLastRestore_Click(object sender, RoutedEventArgs e)
    {
        await UndoLastRestoreAsync();
    }

    private async Task<bool> RestoreProfileAsync(DesktopLayoutProfile profile, bool isAutomatic)
    {
        if (_currentSnapshot is null || _currentSnapshot.Displays.Count != 1)
        {
            SetStatus("当前显示环境不允许恢复布局。", "当前环境不可恢复");
            return false;
        }

        if (!await _desktopOperationLock.WaitAsync(0))
        {
            return false;
        }

        _isDesktopOperationRunning = true;
        PublishCommandState();
        try
        {
            var expectedConfigurationKey = _currentSnapshot.ConfigurationKey;
            if (!await IsDisplayConfigurationCurrentAsync(expectedConfigurationKey))
            {
                ScheduleRefresh("恢复前确认：显示环境已变化");
                return false;
            }

            SetStatus("正在创建恢复前的安全快照…", "正在创建撤销快照");
            var beforeRestore = await CaptureLayoutWithRetryAsync();
            if (beforeRestore.AutoArrangeEnabled)
            {
                throw new InvalidOperationException("检测到桌面的“自动排列图标”已开启，已取消恢复以保护当前布局。");
            }

            await _recoveryStore.SaveAsync(_currentSnapshot, beforeRestore, profile.Name);
            _undoSnapshot = await _recoveryStore.LoadAsync();

            if (!await IsDisplayConfigurationCurrentAsync(expectedConfigurationKey))
            {
                ScheduleRefresh("写回前确认：显示环境已变化");
                return false;
            }

            SetStatus("正在恢复已保存的桌面布局…", "正在恢复布局");
            var result = await RestoreWithRetryAsync(profile.Layout);
            _lastRestoredConfigurationKey = _currentSnapshot.ConfigurationKey;
            var mode = isAutomatic ? "自动" : "手动";
            var extra = result.NewIconCount > 0
                ? $"，另有 {result.NewIconCount} 个新图标保持原位"
                : string.Empty;
            SetStatus($"已{mode}恢复“{profile.Name}”：定位 {result.RestoredCount} 个图标{extra}。", $"{profile.Name} · 保护中");
            LayoutStatusText.Text = result.MissingCount > 0
                ? $"布局已恢复；有 {result.MissingCount} 个已保存图标当前不存在。可使用“撤销最近恢复”。"
                : $"“{profile.Name}”布局已恢复完成；如有需要可撤销最近恢复。";

            if (_settings.ShowRestoreNotifications)
            {
                RequestNotification("桌面布局已恢复", $"已应用“{profile.Name}”，定位 {result.RestoredCount} 个图标。", isError: false);
            }

            return true;
        }
        catch (Exception exception)
        {
            SetStatus($"恢复布局失败：{exception.Message}", "布局恢复失败");
            RequestNotification("桌面布局恢复失败", exception.Message, isError: true);
            if (!isAutomatic && IsVisible)
            {
                System.Windows.MessageBox.Show(this, exception.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return false;
        }
        finally
        {
            _isDesktopOperationRunning = false;
            _desktopOperationLock.Release();
            PublishCommandState();
        }
    }

    private async Task<DesktopLayoutSnapshot> CaptureLayoutWithRetryAsync()
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return _iconLayoutService.Capture();
            }
            catch (COMException exception) when (attempt < 3)
            {
                lastException = exception;
                await Task.Delay(600);
            }
        }

        throw lastException ?? new InvalidOperationException("Windows 桌面暂时不可用。");
    }

    private async Task<DesktopLayoutRestoreResult> RestoreWithRetryAsync(DesktopLayoutSnapshot layout)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return _iconLayoutService.Restore(layout);
            }
            catch (COMException exception) when (attempt < 3)
            {
                lastException = exception;
                await Task.Delay(700);
            }
        }

        throw lastException ?? new InvalidOperationException("Windows 桌面暂时不可用。");
    }

    private async Task<bool> IsDisplayConfigurationCurrentAsync(string expectedConfigurationKey)
    {
        var confirmation = await Task.Run(() => _displayService.Capture("桌面操作前最终确认"));
        return confirmation.Displays.Count == 1 &&
            string.Equals(
                confirmation.ConfigurationKey,
                expectedConfigurationKey,
                StringComparison.OrdinalIgnoreCase);
    }

    private void NavigateCurrent_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(CurrentEnvironmentPanel, CurrentEnvironmentNavButton);
    }

    private async void NavigateProfiles_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ProfilesPanel, ProfilesNavButton);
        await LoadProfilesAsync();
    }

    private async void NavigateHistory_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(HistoryPanel, HistoryNavButton);
        await LoadHistoryAsync();
    }

    private async void RefreshProfiles_Click(object sender, RoutedEventArgs e)
    {
        await LoadProfilesAsync();
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        await LoadHistoryAsync();
    }

    private void ShowPage(UIElement page, System.Windows.Controls.Button selectedButton)
    {
        CurrentEnvironmentPanel.Visibility = page == CurrentEnvironmentPanel ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPanel.Visibility = page == ProfilesPanel ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = page == HistoryPanel ? Visibility.Visible : Visibility.Collapsed;

        foreach (var button in new[] { CurrentEnvironmentNavButton, ProfilesNavButton, HistoryNavButton })
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            button.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        }

        selectedButton.Background = (System.Windows.Media.Brush)FindResource("PrimarySoftBrush");
        selectedButton.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _profileStore.GetAllAsync();
            var cards = profiles
                .Select(profile =>
                {
                    var canRestore = IsProfileCompatibleWithCurrentDisplay(profile);
                    var isCurrent = _currentProfileMatch?.Profile.Id == profile.Id && canRestore;
                    return new ProfileCardViewModel(
                        profile,
                        _profileStore.GetPreviewPath(profile),
                        isCurrent,
                        canRestore);
                })
                .ToArray();
            ProfilesItemsControl.ItemsSource = cards;
            ProfilesEmptyText.Visibility = cards.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ProfilesEmptyText.Text = $"读取显示方案失败：{exception.Message}";
            ProfilesEmptyText.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await _profileStore.GetHistoryAsync();
            var cards = history
                .Select(profile => new HistoryCardViewModel(
                    profile,
                    _profileStore.GetPreviewPath(profile),
                    IsProfileCompatibleWithCurrentDisplay(profile)))
                .ToArray();
            HistoryItemsControl.ItemsSource = cards;
            HistoryEmptyText.Visibility = cards.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            HistoryEmptyText.Text = $"读取恢复记录失败：{exception.Message}";
            HistoryEmptyText.Visibility = Visibility.Visible;
        }
    }

    private bool IsProfileCompatibleWithCurrentDisplay(DesktopLayoutProfile profile)
    {
        if (_currentSnapshot?.Displays.Count != 1 || profile.Display.Displays.Count != 1)
        {
            return false;
        }

        if (string.Equals(profile.Id, _currentSnapshot.ConfigurationKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var current = _currentSnapshot.Displays[0];
        var saved = profile.Display.Displays[0];
        return string.Equals(
                current.CompatibilityIdentityKey,
                saved.CompatibilityIdentityKey,
                StringComparison.OrdinalIgnoreCase) &&
            current.Width == saved.Width &&
            current.Height == saved.Height &&
            current.ScalePercent == saved.ScalePercent &&
            string.Equals(current.Rotation, saved.Rotation, StringComparison.Ordinal);
    }

    private async void RestoreProfileCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileCardViewModel card || !card.CanRestore)
        {
            return;
        }

        await RestoreProfileAsync(card.Profile, isAutomatic: false);
        await LoadProfilesAsync();
    }

    private async void RestoreHistoryCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryCardViewModel card || !card.CanRestore)
        {
            return;
        }

        if (await RestoreProfileAsync(card.Profile, isAutomatic: false))
        {
            SetStatus(
                $"已临时恢复 {card.SavedAt.Replace("保存于 ", string.Empty)} 的旧布局；确认无误后请在“当前环境”重新保存。",
                $"{card.ProfileName} · 历史布局");
        }
    }

    private async void RenameProfileCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileCardViewModel card)
        {
            return;
        }

        if (!await _desktopOperationLock.WaitAsync(0))
        {
            SetStatus("另一个桌面操作正在执行，请稍后再修改方案。", "桌面操作进行中");
            return;
        }

        _isDesktopOperationRunning = true;
        PublishCommandState();
        try
        {
            var renamed = await _profileStore.RenameAsync(card.Profile.Id, card.EditableName);
            if (_currentProfileMatch?.Profile.Id == renamed.Id)
            {
                _currentProfileMatch = new DesktopLayoutProfileMatch
                {
                    Profile = renamed,
                    IsExactMatch = _currentProfileMatch.IsExactMatch
                };
            }

            SetStatus($"方案已重命名为“{renamed.Name}”。", $"{renamed.Name} · 保护中");
            await LoadProfilesAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"重命名方案失败：{exception.Message}", "方案重命名失败");
            System.Windows.MessageBox.Show(this, exception.Message, "重命名失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isDesktopOperationRunning = false;
            _desktopOperationLock.Release();
            PublishCommandState();
        }
    }

    private async void DeleteProfileCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileCardViewModel card)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"确定删除“{card.Profile.Name}”吗？\n\n方案会移动到本地 deleted 备份目录，不会立即永久删除。",
            "删除显示方案",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _desktopOperationLock.WaitAsync(0))
        {
            SetStatus("另一个桌面操作正在执行，请稍后再删除方案。", "桌面操作进行中");
            return;
        }

        _isDesktopOperationRunning = true;
        PublishCommandState();
        try
        {
            await _profileStore.DeleteAsync(card.Profile.Id);
            if (_currentProfileMatch?.Profile.Id == card.Profile.Id)
            {
                _currentProfileMatch = null;
                _lastRestoredConfigurationKey = null;
                LayoutStatusText.Text = "当前显示环境的方案已删除；桌面图标不会再自动恢复，直到重新保存。";
            }

            SetStatus($"已删除“{card.Profile.Name}”；原始记录已移入 deleted 备份目录。", "方案已安全删除");
            await LoadProfilesAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"删除方案失败：{exception.Message}", "方案删除失败");
            System.Windows.MessageBox.Show(this, exception.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isDesktopOperationRunning = false;
            _desktopOperationLock.Release();
            PublishCommandState();
        }
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        _isLoadingSettings = true;
        try
        {
            StartWithWindowsCheckBox.IsChecked = _startupService.IsEnabled();
            ShowNotificationsCheckBox.IsChecked = _settings.ShowRestoreNotifications;
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Clamp(_settings.DisplayChangeDelayMilliseconds, 1500, 8000));
        }
        catch (Exception exception)
        {
            SetStatus($"读取启动设置失败：{exception.Message}", "启动设置不可用");
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private async void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            _startupService.SetEnabled(enabled);
            await SaveSettingsAsync(enabled, ShowNotificationsCheckBox.IsChecked == true);
            SetStatus(enabled ? "已开启登录 Windows 后自动运行。" : "已关闭登录 Windows 后自动运行。",
                enabled ? "开机启动已开启" : "开机启动已关闭");
        }
        catch (Exception exception)
        {
            _isLoadingSettings = true;
            StartWithWindowsCheckBox.IsChecked = !enabled;
            _isLoadingSettings = false;
            SetStatus($"更新开机启动失败：{exception.Message}", "开机启动设置失败");
        }
    }

    private async void ShowNotifications_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var showNotifications = ShowNotificationsCheckBox.IsChecked == true;
        try
        {
            await SaveSettingsAsync(StartWithWindowsCheckBox.IsChecked == true, showNotifications);
            SetStatus(showNotifications ? "已开启恢复成功通知。" : "已关闭恢复成功通知；失败仍会提醒。",
                showNotifications ? "成功通知已开启" : "静默保护中");
        }
        catch (Exception exception)
        {
            SetStatus($"保存通知设置失败：{exception.Message}", "通知设置保存失败");
        }
    }

    private async Task SaveSettingsAsync(bool startWithWindows, bool showNotifications)
    {
        _settings = new ApplicationSettings
        {
            StartWithWindows = startWithWindows,
            ShowRestoreNotifications = showNotifications,
            DisplayChangeDelayMilliseconds = _settings.DisplayChangeDelayMilliseconds,
            StabilityProbeDelayMilliseconds = _settings.StabilityProbeDelayMilliseconds
        };
        await _settingsStore.SaveAsync(_settings);
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSnapshot is null)
        {
            SetStatus("当前还没有可复制的诊断信息。", "尚无诊断信息");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_logService.BuildReport(_currentSnapshot));
            SetStatus("完整诊断信息已复制到剪贴板。", "诊断信息已复制");
        }
        catch (Exception exception)
        {
            SetStatus($"复制失败：{exception.Message}", "复制诊断信息失败");
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
            SetStatus($"无法打开日志文件夹：{exception.Message}", "无法打开日志文件夹");
        }
    }

    private void SetStatus(string message, string summary)
    {
        StatusText.Text = message;
        _traySummary = summary;
        PublishCommandState();
    }

    private void PublishCommandState()
    {
        var singleDisplay = _currentSnapshot?.Displays.Count == 1;
        var canUndo = singleDisplay &&
            _undoSnapshot is not null &&
            string.Equals(
                _undoSnapshot.ConfigurationKey,
                _currentSnapshot?.ConfigurationKey,
                StringComparison.OrdinalIgnoreCase);

        var state = new MainWindowCommandState
        {
            Summary = _traySummary,
            CanSave = singleDisplay && !_isDesktopOperationRunning,
            CanRestore = singleDisplay && _currentProfileMatch is not null && !_isDesktopOperationRunning,
            CanUndo = canUndo && !_isDesktopOperationRunning
        };

        SaveLayoutButton.IsEnabled = state.CanSave;
        RestoreLayoutButton.IsEnabled = state.CanRestore;
        UndoLastRestoreButton.IsEnabled = state.CanUndo;
        CommandStateChanged?.Invoke(this, state);
    }

    private void RequestNotification(string title, string message, bool isError)
    {
        NotificationRequested?.Invoke(this, new MainWindowNotification
        {
            Title = title,
            Message = message,
            IsError = isError
        });
    }
}

public sealed class MainWindowCommandState : EventArgs
{
    public string Summary { get; init; } = string.Empty;

    public bool CanSave { get; init; }

    public bool CanRestore { get; init; }

    public bool CanUndo { get; init; }
}

public sealed class MainWindowNotification : EventArgs
{
    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool IsError { get; init; }
}
