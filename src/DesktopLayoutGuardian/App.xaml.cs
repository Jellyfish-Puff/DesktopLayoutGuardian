using System.Windows;
using Forms = System.Windows.Forms;

namespace DesktopLayoutGuardian;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Closing += OnMainWindowClosing;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开诊断界面", null, (_, _) => ShowMainWindow());
        menu.Items.Add("立即重新检测", null, (_, _) => _mainWindow.RequestRefresh("托盘手动检测"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "桌面布局 - 自动恢复运行中",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        _mainWindow.Show();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _mainWindow?.Hide();
        _notifyIcon?.ShowBalloonTip(
            1800,
            "桌面布局仍在运行",
            "程序已缩小到系统托盘，将继续监听显示变化并恢复已保存的布局。",
            Forms.ToolTipIcon.Info);
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
    }

    private void ExitApplication()
    {
        _isExiting = true;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _mainWindow?.Close();
        Shutdown();
    }
}
