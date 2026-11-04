using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShutAfter.Core;

namespace ShutAfter;

public partial class App : Application
{
    private const string MutexName = "ShutAfter.SingleInstance.v1";
    private const string ShowEventName = "ShutAfter.Show.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private System.Windows.Forms.NotifyIcon? _tray;

    internal static AppConfig Config { get; private set; } = new();
    internal MainWindow? Panel { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 界面校对模式：把窗口渲染成 PNG 后退出（仅开发时使用，不启动托盘）
        if (e.Args.Length >= 2 && e.Args[0] == "--shot")
        {
            CaptureShot(e.Args[1], e.Args.Length > 2 ? e.Args[2] : "");
            return;
        }

        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            // 已经在跑了：把现有窗口叫到前台，而不是再弹一个"已在运行"的对话框
            ActivateExisting();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Config = AppConfig.Load();

        Panel = new MainWindow();
        BuildTray();
        StartShowListener();
        Panel.Show();
    }

    /// <summary>第二个实例把事件置位，由第一个实例负责显示窗口。</summary>
    private void StartShowListener()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            var thread = new Thread(() =>
            {
                while (true)
                {
                    if (!_showEvent.WaitOne()) break;
                    Dispatcher.BeginInvoke(new Action(ShowMain));
                }
            })
            {
                IsBackground = true,
                Name = "ShutAfter.ShowListener"
            };

            thread.Start();
        }
        catch
        {
            // 事件创建失败只影响"再次双击唤起窗口"，不影响正常使用
        }
    }

    private static void ActivateExisting()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowEventName);
            handle.Set();
        }
        catch
        {
            MessageBox.Show("「ShutAfter」已在运行，请在系统托盘里查看（双击图标可打开面板）。", "ShutAfter",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
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
            _showEvent?.Set();

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

    // ---------- 界面校对（--shot）----------

    private void CaptureShot(string path, string kind)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Config = AppConfig.Load();

        Panel = new MainWindow();
        Panel.Show();
        Panel.UpdateLayout();

        if (kind.Length > 0) Panel.PreviewForShot(kind);
        Panel.UpdateLayout();

        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var root = (FrameworkElement)Panel!.Content;
                const double scale = 2.0;

                var w = Math.Max(1, (int)Math.Round(root.ActualWidth * scale));
                var h = Math.Max(1, (int)Math.Round(root.ActualHeight * scale));

                var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using var fs = File.Create(path);
                encoder.Save(fs);

                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
                File.WriteAllText(path + ".txt",
                    $"root={root.ActualWidth:F1}x{root.ActualHeight:F1} dpiScale={dpi.DpiScaleX:F3} " +
                    $"px={w}x{h} workarea={SystemParameters.WorkArea.Width:F0}x{SystemParameters.WorkArea.Height:F0}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            finally
            {
                Shutdown();
            }
        }, DispatcherPriority.ApplicationIdle);
    }
}
