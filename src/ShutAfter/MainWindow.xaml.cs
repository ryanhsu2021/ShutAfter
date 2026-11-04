using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ShutAfter.Core;

namespace ShutAfter;

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly List<string> _programNames = new();
    private readonly List<string> _steamAppIds = new();
    private List<string> _steamLibs = new();

    private MonitorEngine? _engine;
    private bool _allowClose;
    private double _lastProgress;
    private MonitorState _lastState = MonitorState.Idle;

    public MainWindow()
    {
        InitializeComponent();

        _cfg = App.Config;
        ApplyConfig(_cfg);

        Icon = IconFactory.CreateWindowIcon() ?? Icon;

        ProgressTrack.SizeChanged += (_, _) => ApplyProgress(_lastProgress);
    }

    // ---------- 配置 <-> 界面 ----------

    private void ApplyConfig(AppConfig c)
    {
        PathBox.Text = c.WatchPath;
        QuietBox.Text = c.QuietSeconds.ToString();
        GraceBox.Text = c.GraceSeconds.ToString();
        CkTemp.IsChecked = c.WatchTempSuffix;
        CkHandle.IsChecked = c.ProbeHandle;
        CkRecursive.IsChecked = c.Recursive;
        CkProcIo.IsChecked = c.WatchProcessIo;
        CkDrill.IsChecked = c.DrillMode;

        ActShutdown.IsChecked = c.Action == PowerAction.Shutdown;
        ActSleep.IsChecked = c.Action == PowerAction.Sleep;
        ActHibernate.IsChecked = c.Action == PowerAction.Hibernate;
        ActNotify.IsChecked = c.Action == PowerAction.NotifyOnly;

        ModeProgram.IsChecked = c.WatchMode == WatchMode.Program;
        ModeSteam.IsChecked = c.WatchMode == WatchMode.Steam;
        ModeFolder.IsChecked = c.WatchMode == WatchMode.Folder;

        ApplyMode();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 解析期间控件尚未全部创建
        if (ProgramPanel == null || SteamPanel == null || FolderPanel == null) return;
        ApplyMode();
    }

    private void ApplyMode()
    {
        var program = ModeProgram.IsChecked == true;
        var steam = ModeSteam.IsChecked == true;

        ProgramPanel.Visibility = program ? Visibility.Visible : Visibility.Collapsed;
        SteamPanel.Visibility = steam ? Visibility.Visible : Visibility.Collapsed;
        FolderPanel.Visibility = !program && !steam ? Visibility.Visible : Visibility.Collapsed;

        // 细粒度判定信号只对文件夹模式有效
        SignalPanel.Visibility = !program && !steam ? Visibility.Visible : Visibility.Collapsed;

        if (program) RefreshPrograms();
        if (steam) RefreshSteam();
    }

    private bool ReadConfig(out string error)
    {
        error = "";

        if (ModeSteam.IsChecked == true)
        {
            if (_steamAppIds.Count == 0)
            {
                error = "没有检测到可监控的 Steam 任务，请先打开 Steam 并开始下载";
                return false;
            }

            var idx = SteamList.SelectedIndex;
            _cfg.SteamAppId = idx >= 0 && idx < _steamAppIds.Count ? _steamAppIds[idx] : "";
            _cfg.WatchMode = WatchMode.Steam;
        }
        else if (ModeProgram.IsChecked == true)
        {
            var idx = ProgramList.SelectedIndex;
            if (idx < 0 || idx >= _programNames.Count)
            {
                error = "请先在上方列表中选择要监控的软件";
                return false;
            }

            _cfg.ProgramName = _programNames[idx];
            _cfg.WatchMode = WatchMode.Program;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(PathBox.Text) || !Directory.Exists(PathBox.Text))
            {
                error = "请先选择一个存在的下载目录";
                return false;
            }

            _cfg.WatchPath = PathBox.Text.Trim();
            _cfg.WatchMode = WatchMode.Folder;
        }

        _cfg.QuietSeconds = ParseInt(QuietBox.Text, _cfg.QuietSeconds, 10, 1800);
        _cfg.GraceSeconds = ParseInt(GraceBox.Text, _cfg.GraceSeconds, 5, 600);
        _cfg.WatchTempSuffix = CkTemp.IsChecked == true;
        _cfg.ProbeHandle = CkHandle.IsChecked == true;
        _cfg.Recursive = CkRecursive.IsChecked == true;
        _cfg.WatchProcessIo = CkProcIo.IsChecked == true;
        _cfg.DrillMode = CkDrill.IsChecked == true;

        _cfg.Action = ActSleep.IsChecked == true ? PowerAction.Sleep
            : ActHibernate.IsChecked == true ? PowerAction.Hibernate
            : ActNotify.IsChecked == true ? PowerAction.NotifyOnly
            : PowerAction.Shutdown;

        QuietBox.Text = _cfg.QuietSeconds.ToString();
        GraceBox.Text = _cfg.GraceSeconds.ToString();
        _cfg.Save();
        return true;
    }

    private static int ParseInt(string raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw?.Trim(), out var v)) return fallback;
        return Math.Clamp(v, min, max);
    }

    // ---------- 列表刷新 ----------

    private void RefreshPrograms()
    {
        var want = _cfg.ProgramName;
        var apps = ProgramScanner.GetCandidates();

        ProgramList.Items.Clear();
        _programNames.Clear();

        foreach (var a in apps)
        {
            ProgramList.Items.Add(a.Display);
            _programNames.Add(a.Name);
        }

        if (_programNames.Count == 0)
        {
            ProgramHint.Text = "没有找到可监控的程序，请先运行下载软件后点刷新";
            return;
        }

        ProgramHint.Text = $"共 {_programNames.Count} 个候选程序";

        var prefer = _programNames.FindIndex(n => string.Equals(n, want, StringComparison.OrdinalIgnoreCase));
        ProgramList.SelectedIndex = prefer >= 0 ? prefer : 0;
    }

    private void RefreshSteam()
    {
        _steamLibs = SteamScanner.GetLibraryRoots();

        SteamList.Items.Clear();
        _steamAppIds.Clear();

        if (_steamLibs.Count == 0)
        {
            SteamLibText.Text = "未检测到 Steam 库";
            SteamList.Items.Add("未找到 Steam 安装目录");
            _steamAppIds.Add("");
            return;
        }

        var drives = string.Join(" / ", _steamLibs
            .Select(p => Path.GetPathRoot(p)?.TrimEnd('\\') ?? p)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        SteamLibText.Text = $"已识别 {_steamLibs.Count} 个库（{drives}）";

        // 只列未完成的任务，已安装完的不占位置
        SteamLog.TryGetRate(out var rate, out _, out var fresh);
        var speed = fresh ? Math.Max(0, rate) : 0;

        var active = SteamScanner.ScanApps(_steamLibs)
            .Where(a => !a.FullyInstalled)
            .OrderBy(a => a.HasStaging ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SteamList.Items.Add("任意任务　·　任一游戏下载完成即触发");
        _steamAppIds.Add("");

        foreach (var a in active)
        {
            SteamList.Items.Add(MonitorEngine.DescribeApp(a, speed));
            _steamAppIds.Add(a.AppId);
        }

        if (active.Count == 0)
        {
            SteamList.Items.Add("（暂无进行中的下载，开始下载后点「重新扫描」）");
            _steamAppIds.Add("");
        }

        var want = _cfg.SteamAppId;
        var prefer = string.IsNullOrEmpty(want) ? 0 : _steamAppIds.IndexOf(want);
        SteamList.SelectedIndex = prefer >= 0 ? prefer : 0;
    }

    // ---------- 监控控制 ----------

    public void StartMonitoring()
    {
        // 开始前刷新一次，确保选中的软件/任务仍然有效
        if (ModeProgram.IsChecked == true) RefreshPrograms();
        if (ModeSteam.IsChecked == true) RefreshSteam();

        if (!ReadConfig(out var err))
        {
            MessageBox.Show(this, err, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _engine ??= new MonitorEngine(_cfg);
        _engine.StatusChanged -= OnStatus;
        _engine.Completed -= OnCompleted;
        _engine.StatusChanged += OnStatus;
        _engine.Completed += OnCompleted;
        _engine.Start();

        SetRunningUi(true);
        _lastState = MonitorState.Waiting;
    }

    public void StopMonitoring()
    {
        _engine?.Stop();
        SetRunningUi(false);
    }

    public void ShutdownEngine()
    {
        _allowClose = true;
        _engine?.Dispose();
        _engine = null;
    }

    private void SetRunningUi(bool running)
    {
        StartBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
        StartBtn.Content = running ? "监控中…" : "开始监控";

        ModeProgram.IsEnabled = !running;
        ModeSteam.IsEnabled = !running;
        ModeFolder.IsEnabled = !running;

        ProgramPanel.IsEnabled = !running;
        SteamPanel.IsEnabled = !running;
        FolderPanel.IsEnabled = !running;
        SignalPanel.IsEnabled = !running;

        QuietBox.IsEnabled = !running;
        GraceBox.IsEnabled = !running;
        CkDrill.IsEnabled = !running;

        ActShutdown.IsEnabled = !running;
        ActSleep.IsEnabled = !running;
        ActHibernate.IsEnabled = !running;
        ActNotify.IsEnabled = !running;

        HeaderHint.Text = running ? "· 运行中" : "· 未运行";
    }

    // ---------- 引擎回调 ----------

    private void OnStatus(EngineStatus s)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (s.State == MonitorState.Countdown && _lastState != MonitorState.Countdown)
            {
                // 进入倒计时立刻把面板顶到前面，给用户留出取消的机会
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                App.NotifyPublic("下载已完成", "即将" + PowerController.Label(_cfg.Action) + "，可点击「停止」取消。");
            }

            if (s.State == MonitorState.Finished || s.State == MonitorState.Idle)
                SetRunningUi(false);

            _lastState = s.State;
            ApplyStatus(s);
        }));
    }

    private void ApplyStatus(EngineStatus s)
    {
        StateText.Text = s.Headline;
        StateDetail.Text = s.Detail;
        ReasonText.Text = "判定依据：" + (string.IsNullOrEmpty(s.Reason) ? "—" : s.Reason);
        StateSize.Text = s.TotalSize == "0 B" ? "" : "目录 " + s.TotalSize;

        var brush = MakeBrush(s.Accent);
        StateDot.Background = brush;
        ProgressFill.Background = brush;

        ApplyProgress(s.Progress);
    }

    private void ApplyProgress(double progress)
    {
        _lastProgress = Math.Clamp(progress, 0, 1);
        var w = ProgressTrack.ActualWidth * _lastProgress;
        ProgressFill.Width = w <= 0 ? 0 : w;
    }

    private void OnCompleted(PowerAction action)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var ok = PowerController.Execute(action, _cfg.DrillMode, out var message);
            SetRunningUi(false);

            if (!_cfg.DrillMode && _cfg.Action != PowerAction.NotifyOnly)
            {
                // 真执行时来不及细看，通知留痕
                App.NotifyPublic("已执行「" + PowerController.Label(action) + "」", message);
            }
            else
            {
                MessageBox.Show(this, message, "任务完成", MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }

            Show();
            Activate();
        }));
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch
        {
            return new SolidColorBrush(Colors.Gray);
        }
    }

    // ---------- 窗口交互 ----------

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => Hide();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        App.NotifyPublic("仍在后台运行", "程序已最小化到系统托盘，双击图标可重新打开。");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择下载目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(PathBox.Text) ? PathBox.Text : _cfg.DefaultDownloadPath
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            PathBox.Text = dlg.SelectedPath;
    }

    private void RescanPrograms_Click(object sender, RoutedEventArgs e) => RefreshPrograms();

    private void RescanSteam_Click(object sender, RoutedEventArgs e) => RefreshSteam();

    private void Start_Click(object sender, RoutedEventArgs e) => StartMonitoring();

    private void Stop_Click(object sender, RoutedEventArgs e) => StopMonitoring();

    private void QuietBox_LostFocus(object sender, RoutedEventArgs e)
        => QuietBox.Text = ParseInt(QuietBox.Text, 90, 10, 1800).ToString();

    private void GraceBox_LostFocus(object sender, RoutedEventArgs e)
        => GraceBox.Text = ParseInt(GraceBox.Text, 60, 5, 600).ToString();
}
