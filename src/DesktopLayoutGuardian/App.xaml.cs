using System.Threading;
using System.Windows;
using DesktopLayoutGuardian.Services;
using Forms = System.Windows.Forms;

namespace DesktopLayoutGuardian;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\DesktopLayoutGuardian.SingleInstance";
    private const string ActivationEventName = @"Local\DesktopLayoutGuardian.Activate";

    private readonly CancellationTokenSource _activationCancellation = new();
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _statusMenuItem;
    private Forms.ToolStripMenuItem? _saveMenuItem;
    private Forms.ToolStripMenuItem? _restoreMenuItem;
    private Forms.ToolStripMenuItem? _undoMenuItem;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private Task? _activationListenerTask;
    private bool _ownsSingleInstanceMutex;
    private bool _isExiting;
    private bool _hasShownTrayTip;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            _activationEvent.Set();
            Shutdown();
            return;
        }

        var settingsStore = new ApplicationSettingsStore();
        var startupService = new StartupRegistrationService();
        _mainWindow = new MainWindow(settingsStore, startupService);
        MainWindow = _mainWindow;
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.CommandStateChanged += OnCommandStateChanged;
        _mainWindow.NotificationRequested += OnNotificationRequested;

        CreateNotifyIcon();
        StartActivationListener();

        // Show once so Windows creates the native window that receives display-change messages.
        _mainWindow.Show();
        if (e.Args.Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase)))
        {
            _mainWindow.Hide();
        }
    }

    private void CreateNotifyIcon()
    {
        _statusMenuItem = new Forms.ToolStripMenuItem("正在读取显示环境…") { Enabled = false };
        _saveMenuItem = new Forms.ToolStripMenuItem("保存当前布局", null, async (_, _) => await RunTrayCommandAsync(window => window.SaveCurrentLayoutAsync()));
        _restoreMenuItem = new Forms.ToolStripMenuItem("恢复已保存布局", null, async (_, _) => await RunTrayCommandAsync(window => window.RestoreCurrentLayoutAsync()));
        _undoMenuItem = new Forms.ToolStripMenuItem("撤销最近一次恢复", null, async (_, _) => await RunTrayCommandAsync(window => window.UndoLastRestoreAsync()));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开主界面", null, (_, _) => ShowMainWindow());
        menu.Items.Add(_saveMenuItem);
        menu.Items.Add(_restoreMenuItem);
        menu.Items.Add(_undoMenuItem);
        menu.Items.Add("立即重新检测", null, (_, _) => _mainWindow?.RequestRefresh("托盘手动检测"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "桌面布局 · 正在启动",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private async Task RunTrayCommandAsync(Func<MainWindow, Task> command)
    {
        if (_mainWindow is null)
        {
            return;
        }

        await _mainWindow.Dispatcher.InvokeAsync(async () => await command(_mainWindow)).Task.Unwrap();
    }

    private void OnCommandStateChanged(object? sender, MainWindowCommandState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_statusMenuItem is not null)
            {
                _statusMenuItem.Text = state.Summary;
            }

            if (_saveMenuItem is not null)
            {
                _saveMenuItem.Enabled = state.CanSave;
            }

            if (_restoreMenuItem is not null)
            {
                _restoreMenuItem.Enabled = state.CanRestore;
            }

            if (_undoMenuItem is not null)
            {
                _undoMenuItem.Enabled = state.CanUndo;
            }

            if (_notifyIcon is not null)
            {
                var tooltip = $"桌面布局 · {state.Summary}";
                _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
            }
        });
    }

    private void OnNotificationRequested(object? sender, MainWindowNotification notification)
    {
        _notifyIcon?.ShowBalloonTip(
            2200,
            notification.Title,
            notification.Message,
            notification.IsError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
    }

    private void StartActivationListener()
    {
        var activationEvent = _activationEvent;
        if (activationEvent is null)
        {
            return;
        }

        _activationListenerTask = Task.Run(() =>
        {
            var handles = new WaitHandle[] { activationEvent, _activationCancellation.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        });
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _mainWindow?.Hide();
        if (!_hasShownTrayTip)
        {
            _hasShownTrayTip = true;
            _notifyIcon?.ShowBalloonTip(
                1800,
                "桌面布局仍在运行",
                "程序已隐藏到系统托盘，将继续自动恢复已保存的布局。",
                Forms.ToolTipIcon.Info);
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _activationCancellation.Cancel();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation.Cancel();
        try
        {
            _activationListenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown should continue even if the background activation listener has already stopped.
        }

        _activationEvent?.Dispose();

        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The operating system already released ownership during shutdown.
            }
        }

        _singleInstanceMutex?.Dispose();
        _activationCancellation.Dispose();
        base.OnExit(e);
    }
}
