using System;
using System.Threading;
using System.Windows;
using ShutAfter.Core;

namespace ShutAfter;

public partial class App : Application
{
    private Mutex? _mutex;
    private System.Windows.Forms.NotifyIcon? _tray;

    internal static AppConfig Config { get; private set; } = new();
    internal MainWindow? Panel { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ShutAfter.SingleInstance.v1", out var created);
        if (!created)
        {
            MessageBox.Show("「ShutAfter」已在运行，请在系统托盘中查看。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Config = AppConfig.Load();

        Panel = new MainWindow();
        BuildTray();
        Panel.Show();
    }

    private void BuildTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = IconFactory.CreateTray() ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone(),
            Visible = true,
            Text = "ShutAfter"
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, _) => ShowMain());
        menu.Items.Add("开始监控", null, (_, _) =>
        {
            ShowMain();
            Panel?.StartMonitoring();
        });
        menu.Items.Add("停止监控", null, (_, _) => Panel?.StopMonitoring());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    internal void Notify(string title, string text, bool warning = false)
    {
        try
        {
            _tray?.ShowBalloonTip(6000, title, text,
                warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 通知失败不影响主流程
        }
    }

    internal static void NotifyPublic(string title, string text, bool warning = false)
        => (Current as App)?.Notify(title, text, warning);

    internal void ShowMain()
    {
        if (Panel == null) return;
        Panel.Show();
        if (Panel.WindowState == WindowState.Minimized) Panel.WindowState = WindowState.Normal;
        Panel.Activate();
    }

    internal void ExitApp()
    {
        try
        {
            Panel?.ShutdownEngine();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
        catch
        {
            // 退出清理失败不阻塞
        }

        Shutdown();
    }
}
