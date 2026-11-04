using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ShutAfter.Core;
using ShutAfter.Ui;

namespace ShutAfter;

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly SelectableRowList _programRows;
    private readonly SelectableRowList _steamRows;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.OrdinalIgnoreCase);

    private List<string> _steamLibs = new();
    private MonitorEngine? _engine;
    private bool _allowClose;
    private bool _steamUnavailable;
    private static bool _trayHintShown;
    private MonitorState _lastState = MonitorState.Idle;
    private string _lastSignature = "";

    public MainWindow()
    {
        InitializeComponent();

        _programRows = new SelectableRowList(ProgramRows, OnProgramRowSelected);
        _steamRows = new SelectableRowList(SteamRows, OnSteamRowSelected);

        _cfg = App.Config;
        ApplyConfig(_cfg);

        var icon = IconFactory.CreateWindowIcon(32);
        if (icon != null) Icon = icon;

        // 高 DPI 缩放后工作区可能只有 720 DIP：窗口跟着工作区收口，超出部分交给滚动区，
        // 保证底部的「开始监控」永远不会被推到屏幕外。
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 20);

        Loaded += (_, _) =>
        {
            var brand = IconFactory.CreateWindowIcon(28);
            if (brand != null) BrandIcon.Source = brand;
            UpdatePlan();
        };
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
        UpdatePlan();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 解析期间控件尚未全部创建
        if (ProgramPanel == null || SteamPanel == null || FolderPanel == null) return;
        ApplyMode();
        UpdatePlan();
    }

    private void Act_Checked(object sender, RoutedEventArgs e)
    {
        if (PlanText == null) return;
        UpdatePlan();
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

        RescanBtn.Visibility = !program && !steam ? Visibility.Collapsed : Visibility.Visible;
        TargetMeta.Text = "";

        if (program) RefreshPrograms();
        else if (steam) RefreshSteam();
        else
        {
            if (string.IsNullOrWhiteSpace(PathBox.Text))
                PathBox.Text = _cfg.DefaultDownloadPath;
            TargetMeta.Text = "";
        }

        UpdateAdvSummary();
    }

    private bool ReadConfig(out string error)
    {
        error = "";

        if (ModeSteam.IsChecked == true)
        {
            if (_steamUnavailable)
            {
                error = "没有找到 Steam 安装目录。装了 Steam 的话请先启动一次；否则请改用「文件夹」模式。";
                return false;
            }

            if (_steamRows.Count == 0)
            {
                error = "Steam 队列里没有可监控的任务，请先开始一个下载后点「刷新」。";
                return false;
            }

            _cfg.SteamAppId = _steamRows.SelectedId;
            _cfg.WatchMode = WatchMode.Steam;
        }
        else if (ModeProgram.IsChecked == true)
        {
            if (_programRows.SelectedIndex < 0)
            {
                error = "请先在列表中选择要监控的软件。";
                return false;
            }

            _cfg.ProgramName = _programRows.SelectedId;
            _cfg.WatchMode = WatchMode.Program;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(PathBox.Text) || !Directory.Exists(PathBox.Text))
            {
                error = "请先选择一个存在的下载目录。";
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

    // ---------- 列表 ----------

    private void RefreshPrograms()
    {
        var want = _cfg.ProgramName;
        var apps = ProgramScanner.GetCandidates();

        _programRows.Clear();

        foreach (var a in apps)
            _programRows.Add(a.Name, a.Name, Trim(a.Title, 42));

        if (_programRows.Count == 0)
        {
            _programRows.AddHint("没有找到可监控的程序。先运行下载软件，再点右上角「刷新」。");
            TargetMeta.Text = "0 个候选";
            return;
        }

        TargetMeta.Text = apps.Count + " 个候选";
        _programRows.Select(IndexOf(_programRows, want));
    }

    private static string Trim(string text, int max) =>
        string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text[..max] + "…");

    private static int IndexOf(SelectableRowList rows, string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;

        for (var i = 0; i < rows.Count; i++)
            if (string.Equals(rows.IdAt(i), id, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }

    private void RefreshSteam()
    {
        _steamLibs = SteamScanner.GetLibraryRoots();
        _steamUnavailable = _steamLibs.Count == 0;
        _steamRows.Clear();

        if (_steamUnavailable)
        {
            _steamRows.AddHint("没有找到 Steam 库。请确认 Steam 已安装并至少启动过一次。");
            TargetMeta.Text = "未检测到";
            SteamHint.Text = "找不到 steamapps 目录";
            return;
        }

        var drives = new List<string>();
        foreach (var p in _steamLibs)
        {
            var root = Path.GetPathRoot(p)?.TrimEnd('\\') ?? p;
            if (!drives.Any(d => string.Equals(d, root, StringComparison.OrdinalIgnoreCase)))
                drives.Add(root);
        }

        SteamLog.TryGetRate(out var rate, out _, out var fresh);
        var speed = fresh ? Math.Max(0, rate) : 0;

        var active = SteamScanner.ScanApps(_steamLibs)
            .Where(a => !a.FullyInstalled)
            .OrderBy(a => a.HasStaging ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 两层目标：整个队列 / 某个具体任务
        _steamRows.Add("全部任务", "", "队列里没有任务在收数据时触发");
        foreach (var a in active)
            _steamRows.Add(a.Name, a.AppId, SteamRowMeta(a, speed));

        TargetMeta.Text = $"{_steamLibs.Count} 库 · {drives.Count} 盘 · {active.Count} 未完成";

        if (active.Count == 0)
            _steamRows.AddHint("队列里暂时没有未完成的下载。开始下载后点「刷新」。");

        // 选中项只有一个来源：配置里的目标；目标已不在列表里就落回「全部任务」
        var index = IndexOf(_steamRows, _cfg.SteamAppId);
        _steamRows.Select(index);

        var resolved = _steamRows.SelectedId;
        if (!string.Equals(resolved, _cfg.SteamAppId, StringComparison.OrdinalIgnoreCase))
        {
            _cfg.SteamAppId = resolved;
            _cfg.Save();
        }

        UpdateSteamHint();
    }

    private void UpdateSteamHint()
    {
        SteamHint.Text = _cfg.SteamAppId.Length == 0
            ? "全部任务：队列里所有下载都不再收数据时才触发，暂停/排队的任务不会卡住判定。"
            : "只等这一个任务：其他任务下完不会触发。";
    }

    /// <summary>Steam 行的副标题：只放真实有用的三个信息（速率 / 体积 / 是否暂停）。</summary>
    private static string SteamRowMeta(SteamApp a, long speed)
    {
        var parts = new List<string>();

        if (speed > 0 && a.HasStaging)
            parts.Add("↓ " + MonitorEngine.FormatSize(speed) + "/s");

        if (a.BytesToDownload > 0 && a.BytesDownloaded > 0)
            parts.Add(MonitorEngine.FormatSize(a.BytesDownloaded) + " / " + MonitorEngine.FormatSize(a.BytesToDownload));
        else if (a.CachedBytes > 0)
            parts.Add("已缓存 " + MonitorEngine.FormatSize(a.CachedBytes));

        if (a.HasStaging && speed <= 0)
            parts.Add("已暂停");

        return parts.Count == 0 ? "等待 Steam 回报状态" : string.Join("  ·  ", parts);
    }

    private void OnSteamRowSelected(int index)
    {
        _cfg.SteamAppId = _steamRows.IdAt(index);
        _cfg.Save();

        UpdateSteamHint();
        UpdatePlan();
    }

    private void OnProgramRowSelected(int index)
    {
        _cfg.ProgramName = _programRows.IdAt(index);
        _cfg.Save();
        UpdatePlan();
    }

    // ---------- 计划摘要 ----------

    private string ActionLabel() =>
        ActSleep.IsChecked == true ? "睡眠"
        : ActHibernate.IsChecked == true ? "休眠"
        : ActNotify.IsChecked == true ? "仅提醒"
        : "关机";

    private string TargetLabel()
    {
        if (ModeSteam.IsChecked == true)
        {
            if (_steamRows.SelectedIndex < 0) return "Steam";
            return _steamRows.SelectedIndex == 0
                ? "Steam 全部任务"
                : "Steam · " + _steamRows.TitleAt(_steamRows.SelectedIndex);
        }

        if (ModeProgram.IsChecked == true)
            return _programRows.SelectedIndex < 0
                ? "未选择软件"
                : _programRows.TitleAt(_programRows.SelectedIndex);

        var path = (PathBox.Text ?? "").TrimEnd('\\');
        if (path.Length == 0) return "未选择目录";
        try
        {
            var name = Path.GetFileName(path);
            return name.Length > 0 ? name : path;
        }
        catch
        {
            return path;
        }
    }

    private static string HumanSeconds(int seconds) =>
        seconds % 60 == 0 ? (seconds / 60) + " 分钟" : seconds + " 秒";

    private void UpdatePlan()
    {
        if (PlanText == null) return;

        var quiet = ParseInt(QuietBox.Text, _cfg.QuietSeconds, 10, 1800);
        var grace = ParseInt(GraceBox.Text, _cfg.GraceSeconds, 5, 600);
        var drill = CkDrill.IsChecked == true;
        var action = ActionLabel();

        PlanText.Text = drill
            ? $"{TargetLabel()}　·　静默 {HumanSeconds(quiet)}　·　演练：不会真正{action}"
            : $"{TargetLabel()}　·　静默 {HumanSeconds(quiet)}　·　宽限 {grace}s　·　完成后{action}";

        if (drill)
        {
            PlanBadge.Background = MakeBrush("#2A2314");
            PlanBadge.BorderBrush = MakeBrush("#4C3B16");
            PlanBadgeText.Foreground = MakeBrush("#E3B65C");
            PlanBadgeText.Text = "演练";
        }
        else if (action == "仅提醒")
        {
            PlanBadge.Background = MakeBrush("#16233D");
            PlanBadge.BorderBrush = MakeBrush("#2B4C86");
            PlanBadgeText.Foreground = MakeBrush("#9CC0FF");
            PlanBadgeText.Text = "仅提醒";
        }
        else
        {
            PlanBadge.Background = MakeBrush("#152A22");
            PlanBadge.BorderBrush = MakeBrush("#22503F");
            PlanBadgeText.Foreground = MakeBrush("#63D3A5");
            PlanBadgeText.Text = "真实执行";
        }

        UpdateAdvSummary();
    }

    private void UpdateAdvSummary()
    {
        if (AdvToggle == null || QuietBox == null) return;

        var quiet = ParseInt(QuietBox.Text, _cfg.QuietSeconds, 10, 1800);
        var grace = ParseInt(GraceBox.Text, _cfg.GraceSeconds, 5, 600);
        var signals = (CkTemp?.IsChecked == true ? 1 : 0) + (CkHandle?.IsChecked == true ? 1 : 0)
                      + (CkRecursive?.IsChecked == true ? 1 : 0) + (CkProcIo?.IsChecked == true ? 1 : 0);

        var folder = ModeFolder.IsChecked == true;
        AdvToggle.Tag = folder
            ? $"静默 {HumanSeconds(quiet)} · 宽限 {grace}s · {signals} 项信号"
            : $"静默 {HumanSeconds(quiet)} · 宽限 {grace}s";
    }

    // ---------- 监控控制 ----------

    public void StartMonitoring()
    {
        // 开始前刷新一次，确保选中的软件/任务仍然有效
        if (ModeProgram.IsChecked == true) RefreshPrograms();
        if (ModeSteam.IsChecked == true) RefreshSteam();

        if (!ReadConfig(out var err))
        {
            MessageBox.Show(this, err, "还不能开始", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _engine ??= new MonitorEngine(_cfg);
        _engine.StatusChanged -= OnStatus;
        _engine.Completed -= OnCompleted;
        _engine.StatusChanged += OnStatus;
        _engine.Completed += OnCompleted;

        _lastSignature = "";
        _lastState = MonitorState.Waiting;
        _engine.Start();

        SetRunningUi(true);
        UpdatePlan();
    }

    public void StopMonitoring()
    {
        _engine?.Stop();
        SetRunningUi(false);
        UpdatePlan();
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
        RescanBtn.IsEnabled = !running;

        ProgramPanel.IsEnabled = !running;
        FolderPanel.IsEnabled = !running;

        // Steam 列表保持可滚动，但选中项锁定在正在监控的那一个
        _steamRows.Locked = running;
        _programRows.Locked = running;

        ThresholdPanel.IsEnabled = !running;
        SignalPanel.IsEnabled = !running;
        CkDrill.IsEnabled = !running;

        ActShutdown.IsEnabled = !running;
        ActSleep.IsEnabled = !running;
        ActHibernate.IsEnabled = !running;
        ActNotify.IsEnabled = !running;

        if (running) SteamHint.Text = "监控中：目标已锁定，需要更换请先点「停止」。";
        else UpdateSteamHint();
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
                App.NotifyPublic("下载已完成", "即将" + PowerController.Label(_cfg.Action) + "，点「停止」可取消。");
            }

            if (s.State == MonitorState.Finished || s.State == MonitorState.Idle)
                SetRunningUi(false);

            _lastState = s.State;
            ApplyStatus(s);
        }));
    }

    private void ApplyStatus(EngineStatus s)
    {
        // 同一秒内没变化就不重排界面，减少无谓的布局开销
        // 等待阶段的秒数不展示，也不该触发重绘
        var idleKey = s.State is MonitorState.Waiting or MonitorState.Idle ? 0 : s.IdleSeconds;
        var signature = string.Join("|", s.State, s.Headline, s.Detail, s.Reason, s.Volume,
            s.SpeedBytes, (int)(s.Progress * 400), (int)(s.DownloadProgress * 400), idleKey);
        if (signature == _lastSignature) return;
        _lastSignature = signature;

        StateText.Text = s.Headline;
        StateDetail.Text = string.IsNullOrWhiteSpace(s.Detail) ? "—" : s.Detail;

        // 圆环显示什么：拿得到真实下载进度就显示下载完成度（下载中静默恒为 0，
        // 若继续显示静默进度，整个下载过程环都停在 0%，看起来像没生效）。
        // 拿不到（文件夹 / 软件模式）才退回静默计时进度。
        var hasDownload = s.DownloadProgress >= 0 && s.State == MonitorState.Monitoring;
        var ringValue = hasDownload ? s.DownloadProgress : s.Progress;

        var brush = MakeBrush(s.Accent);
        StateDot.Background = brush;
        Ring.AccentBrush = brush;
        AnimateRing(ringValue);

        RingValue.Text = s.State switch
        {
            MonitorState.Waiting or MonitorState.Idle => "—",
            _ => ((int)Math.Round(Math.Clamp(ringValue, 0, 1) * 100)) + "%"
        };

        RingCaption.Text = s.State switch
        {
            MonitorState.Waiting => "待开始",
            MonitorState.Monitoring => hasDownload ? "下载" : "静默",
            MonitorState.Countdown => "倒计时",
            MonitorState.Finished => "完成",
            _ => "待机"
        };

        MetricSpeed.Text = s.SpeedBytes < 0 ? "—"
            : s.SpeedBytes == 0 ? "静止"
            : MonitorEngine.FormatSize(s.SpeedBytes) + "/s";

        MetricVolume.Text = string.IsNullOrWhiteSpace(s.Volume) ? "—" : s.Volume;

        MetricIdle.Text = s.State == MonitorState.Idle || s.State == MonitorState.Waiting
            ? "—"
            : FormatClock(s.IdleSeconds);

        ReasonText.Text = string.IsNullOrWhiteSpace(s.Reason) ? "—" : s.Reason;
    }

    private void AnimateRing(double target)
    {
        target = Math.Clamp(target, 0, 1);

        // 校对模式直接落到终值，否则截图抓到的永远是动画第一帧（看起来像环没生效）
        if (App.ShotMode)
        {
            Ring.BeginAnimation(ProgressRing.ValueProperty, null);
            Ring.Value = target;
            return;
        }

        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        Ring.BeginAnimation(ProgressRing.ValueProperty, anim);
    }

    private static string FormatClock(int seconds)
    {
        if (seconds < 0) return "—";
        if (seconds < 60) return seconds + "s";
        if (seconds < 3600) return (seconds / 60) + "m" + (seconds % 60).ToString("00") + "s";
        return (seconds / 3600) + "h" + (seconds % 3600 / 60).ToString("00") + "m";
    }

    private void OnCompleted(PowerAction action)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var ok = PowerController.Execute(action, _cfg.DrillMode, out var message);
            SetRunningUi(false);
            UpdatePlan();

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

    private SolidColorBrush MakeBrush(string hex)
    {
        if (_brushes.TryGetValue(hex, out var cached)) return cached;

        SolidColorBrush brush;
        try
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            brush = new SolidColorBrush(Colors.Gray);
        }

        brush.Freeze();
        _brushes[hex] = brush;
        return brush;
    }

    // ---------- 窗口交互 ----------

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch
        {
            // 鼠标在拖动过程中释放会抛异常，忽略即可
        }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => Hide();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();

        // 只在本次运行第一次关闭时提示，之后不再打扰
        if (_trayHintShown) return;
        _trayHintShown = true;
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
        {
            PathBox.Text = dlg.SelectedPath;
            UpdatePlan();
        }
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (ModeProgram.IsChecked == true) RefreshPrograms();
        else if (ModeSteam.IsChecked == true) RefreshSteam();
        UpdatePlan();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartMonitoring();

    private void Stop_Click(object sender, RoutedEventArgs e) => StopMonitoring();

    private void QuietBox_LostFocus(object sender, RoutedEventArgs e)
    {
        QuietBox.Text = ParseInt(QuietBox.Text, _cfg.QuietSeconds, 10, 1800).ToString();
        UpdatePlan();
    }

    private void GraceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        GraceBox.Text = ParseInt(GraceBox.Text, _cfg.GraceSeconds, 5, 600).ToString();
        UpdatePlan();
    }

    /// <summary>数值框里按回车即时生效（不等焦点离开）。</summary>
    private void NumBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Keyboard.ClearFocus();
    }

    private void Drill_Checked(object sender, RoutedEventArgs e)
    {
        if (PlanText == null) return;
        UpdatePlan();
    }

    private void Adv_Toggled(object sender, RoutedEventArgs e)
    {
        if (AdvPanel == null) return;

        var open = AdvToggle.IsChecked == true;
        AdvPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (open)
        {
            AdvPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }
    }

    // ---------- 设计校对用（--shot） ----------

    /// <summary>把状态卡填充成指定场景，仅用于界面截图校对，不参与正常运行。</summary>
    public void PreviewForShot(string kind)
    {
        if (kind == "advanced")
        {
            AdvToggle.IsChecked = true;
            Adv_Toggled(AdvToggle, new RoutedEventArgs());
        }

        if (kind == "folder")
        {
            ModeFolder.IsChecked = true;
            AdvToggle.IsChecked = true;
            Adv_Toggled(AdvToggle, new RoutedEventArgs());
            return;
        }

        if (kind == "program")
        {
            ModeProgram.IsChecked = true;
            return;
        }

        var state = kind switch
        {
            "monitoring" => MonitorState.Monitoring,
            "countdown" => MonitorState.Countdown,
            "finished" => MonitorState.Finished,
            "waiting" => MonitorState.Waiting,
            "advanced" => MonitorState.Monitoring,
            _ => MonitorState.Idle
        };

        if (state == MonitorState.Idle)
        {
            UpdatePlan();
            return;
        }

        var status = state switch
        {
            MonitorState.Monitoring => new EngineStatus
            {
                State = state, Headline = "监控中",
                Detail = "Steam 正在下载《Subnautica》",
                Reason = "Steam 回报 12.4 MB/s，队列还有 2 个未完成",
                Progress = 0.24, DownloadProgress = 0.46, Accent = "#4C8DFF",
                SpeedBytes = (long)(12.4 * 1024 * 1024), Volume = "3.21 GB / 6.94 GB",
                IdleSeconds = 72
            },
            MonitorState.Countdown => new EngineStatus
            {
                State = state, Headline = "即将关机",
                Detail = "剩余 41 秒，期间恢复下载会自动取消",
                Reason = "目录已静止 331 秒，超过静默阈值",
                Progress = 0.32, Accent = "#EF9F27",
                SpeedBytes = 0, Volume = "12.8 GB", IdleSeconds = 331
            },
            MonitorState.Finished => new EngineStatus
            {
                State = state, Headline = "下载已完成",
                Detail = "已执行：关机（30 秒内可 shutdown /a 撤销）",
                Reason = "Steam 已标记为完全安装",
                Progress = 1, Accent = "#3DDC97",
                SpeedBytes = 0, Volume = "6.94 GB", IdleSeconds = 368
            },
            _ => new EngineStatus
            {
                State = state, Headline = "等待下载开始",
                Detail = "已锁定 Steam 队列，一旦有任务收数据即开始判定",
                Reason = "尚未观测到任何写入活动",
                Progress = 0, Accent = "#8A8A99", SpeedBytes = -1,
                Volume = "—", IdleSeconds = 0
            }
        };

        ApplyStatus(status);
    }
}
