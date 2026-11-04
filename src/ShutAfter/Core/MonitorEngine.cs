using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ShutAfter.Core;

public enum MonitorState
{
    Idle,
    Waiting,
    Monitoring,
    Countdown,
    Finished
}

public sealed class EngineStatus
{
    public MonitorState State { get; init; } = MonitorState.Idle;
    public string Headline { get; init; } = "未开始";

    /// <summary>一句话说明"现在在发生什么"，界面里单独一行。</summary>
    public string Detail { get; init; } = "";

    /// <summary>判定依据：为什么认为还在下载 / 为什么认为已经停了。</summary>
    public string Reason { get; init; } = "";

    /// <summary>当前阶段的触发进度 0~1（监控中 = 静默进度，倒计时 = 宽限进度）。</summary>
    public double Progress { get; init; }

    /// <summary>下载完成度 0~1；-1 表示这种监控方式拿不到，仅作辅助信息。</summary>
    public double DownloadProgress { get; init; } = -1;

    public string Accent { get; init; } = "#4C8DFF";

    /// <summary>体积读数，例如 "1.24 GB / 5.40 GB" 或 "已缓存 3.2 GB"。</summary>
    public string Volume { get; init; } = "";

    /// <summary>实时速度（字节/秒）；-1 表示当前模式给不出速度。</summary>
    public long SpeedBytes { get; init; } = -1;

    public int IdleSeconds { get; init; }
}

/// <summary>
/// 下载完成判定引擎。三种模式共用同一套"静默 -> 倒计时 -> 执行"状态机。
///
/// 文件夹模式：不问"网卡还剩多少流量"，只问"磁盘上还有没有人在写"。
///   1. 下载器临时文件（.crdownload/.part/...）—— 下载器自己标记的"未完成"
///   2. 文件体积/数量相对上一次采样变化
///   3. 目标文件仍被其他进程独占持有句柄（FileShare.None 试探失败）
///   4. 指定下载进程的累计磁盘写入量仍在增长（可选）
///
/// Steam 模式：读 steamapps/appmanifest_&lt;appid&gt;.acf 的 StateFlags，
///   并以 content_log.txt 里的真实速率作为"还在下载"的主信号。
///
/// 全程要求"曾经观测到活动"，避免目录为空或任务为空时误关机。
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private static readonly string[] TempSuffixes =
    {
        ".crdownload", ".part", ".partial", ".download", ".downloading",
        ".td", ".xltd", ".!qb", ".!ut", ".!bt", ".aria2", ".opdownload", ".pdl", ".bc!"
    };

    private static readonly HashSet<string> KnownDownloaders = ProgramScanner.KnownDownloaders;

    /// <summary>句柄试探每次最多看几个文件：只挑最近修改过的，够用且不烧 IO。</summary>
    private const int ProbeCandidateLimit = 24;

    /// <summary>句柄试探的最小间隔：连续失败会抛异常，1 秒一次太浪费。</summary>
    private const int ProbeIntervalMs = 2000;

    private const int SteamAliveTtlSeconds = 5;

    private readonly AppConfig _cfg;
    private readonly Dictionary<string, long> _prevSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ulong> _procWriteBaseline = new();
    private readonly List<FileEntry> _snapshotBuffer = new(256);

    private System.Threading.Timer? _timer;
    private int _ticking;
    private DateTime _lastActivityUtc;
    private bool _hasSeenActivity;
    private bool _completed;
    private bool _running;
    private string _reason = "等待下载开始";
    private string _processHint = "";
    private MonitorState _state = MonitorState.Idle;
    private DateTime _probeAt = DateTime.MinValue;
    private long _lastTotal = -1;

    private List<string> _steamLibs = new();
    private DateTime _steamLibsAt = DateTime.MinValue;
    private bool _steamAlive;
    private DateTime _steamAliveAt = DateTime.MinValue;

    /// <summary>每个 appid 上一次的进度快照，用于判断"是否还有增长"。</summary>
    private readonly Dictionary<string, (long Downloaded, long Staged)> _steamPrev = new(StringComparer.OrdinalIgnoreCase);

    // Steam 只在分段完成 / 暂停 / 校验时才重写 appmanifest，实测可以一个多小时不更新，
    // 光读它会让界面上的百分比几十分钟纹丝不动。所以拿它的值当锚点，
    // 再用 content_log 里的真实速率按秒累加，manifest 一旦刷新就用真实值重新校准。
    private string _steamRatioAppId = "";
    private long _steamAnchorBytes;
    private long _steamExtraBytes;
    private DateTime _steamAccrualAt = DateTime.MinValue;

    public event Action<EngineStatus>? StatusChanged;
    public event Action<PowerAction>? Completed;

    public MonitorEngine(AppConfig cfg)
    {
        _cfg = cfg;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;

        _running = true;
        _completed = false;
        _hasSeenActivity = false;
        _lastActivityUtc = DateTime.UtcNow;
        _reason = "等待下载开始";
        _processHint = "";
        _state = MonitorState.Waiting;
        _prevSizes.Clear();
        _procWriteBaseline.Clear();
        _steamLibs.Clear();
        _steamLibsAt = DateTime.MinValue;
        _steamPrev.Clear();
        _steamAliveAt = DateTime.MinValue;
        _probeAt = DateTime.MinValue;
        _lastTotal = -1;
        _steamRatioAppId = "";
        _steamAnchorBytes = 0;
        _steamExtraBytes = 0;
        _steamAccrualAt = DateTime.MinValue;

        if (_cfg.WatchMode == WatchMode.Folder)
        {
            // 基线快照：开始监控前已存在的文件不算"活动"，避免残留文件导致误判
            foreach (var f in Snapshot(_cfg.WatchPath, _cfg.Recursive))
                _prevSizes[f.Path] = f.Size;

            Publish(MonitorState.Waiting, "等待下载开始", "目录已锁定，一旦有文件写入即开始判定", 0);
        }
        else if (_cfg.WatchMode == WatchMode.Steam)
        {
            Publish(MonitorState.Waiting, "等待下载开始", "正在读取 Steam 下载队列…", 0);
        }
        else
        {
            Publish(MonitorState.Waiting, "等待下载开始",
                _cfg.ProgramName.Length > 0 ? "已锁定程序：" + _cfg.ProgramName : "请先选择要监控的软件", 0);
        }

        _timer = new System.Threading.Timer(_ => Tick(), null, 300, 1000);
    }

    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
        Publish(MonitorState.Idle, "已停止", "监控已关闭", 0);
    }

    public void Dispose() => Stop();

    private void Tick()
    {
        if (!_running || _completed) return;

        // 防重入：Steam 扫描 + 目录枚举可能超过 1 秒，重入会并发改状态、重复触发关机
        if (System.Threading.Interlocked.Exchange(ref _ticking, 1) == 1) return;

        try
        {
            var now = DateTime.UtcNow;
            switch (_cfg.WatchMode)
            {
                case WatchMode.Steam: TickSteam(now); break;
                case WatchMode.Program: TickProgram(now); break;
                default: TickFolder(now); break;
            }

            Reschedule();
        }
        catch (Exception ex)
        {
            _reason = "采样异常：" + ex.Message;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>按当前阶段调整下一次采样间隔。</summary>
    private void Reschedule()
    {
        if (_timer == null || _completed) return;

        var interval = _state == MonitorState.Countdown
            ? 500
            : _hasSeenActivity ? 1000 : 1400;

        try
        {
            _timer.Change(interval, interval);
        }
        catch (ObjectDisposedException)
        {
            // 与 Stop/Dispose 竞争，忽略
        }
    }

    // ---------- 文件夹模式 ----------

    private void TickFolder(DateTime now)
    {
        if (string.IsNullOrWhiteSpace(_cfg.WatchPath) || !Directory.Exists(_cfg.WatchPath))
        {
            Publish(MonitorState.Waiting, "监控目录不可用", "请重新选择一个有效的下载目录", 0);
            return;
        }

        var files = Snapshot(_cfg.WatchPath, _cfg.Recursive);
        var totalBytes = 0L;
        foreach (var f in files) totalBytes += f.Size;

        var busy = false;
        var why = "";

        // 信号 1：下载器临时文件 —— 最强的"未完成"标记
        if (_cfg.WatchTempSuffix)
        {
            foreach (var f in files)
            {
                if (!IsTempFile(f.Name)) continue;
                busy = true;
                why = "临时文件仍在：" + f.Name;
                break;
            }
        }

        // 信号 2：体积 / 数量变化
        if (!busy)
        {
            foreach (var f in files)
            {
                if (!_prevSizes.TryGetValue(f.Path, out var prev))
                {
                    busy = true;
                    why = "出现新文件：" + f.Name;
                    break;
                }

                if (prev != f.Size)
                {
                    busy = true;
                    why = "体积仍在增长：" + f.Name;
                    break;
                }
            }
        }

        // 信号 3：句柄独占试探（写入方还开着文件就一定失败）
        // 只在其它信号都没命中时做，且限制频率和候选数量：一次试探就是一次系统调用，
        // 失败路径还要走异常处理，每秒 60 次的无用试探完全没必要。
        if (!busy && _cfg.ProbeHandle && (now - _probeAt).TotalMilliseconds >= ProbeIntervalMs)
        {
            _probeAt = now;

            var candidates = files.Count <= ProbeCandidateLimit
                ? files
                : files.OrderByDescending(x => x.LastWriteUtc).Take(ProbeCandidateLimit);

            foreach (var f in candidates)
            {
                if (!IsLocked(f.Path)) continue;
                busy = true;
                why = "文件仍被进程占用：" + f.Name;
                break;
            }
        }

        // 信号 4：下载进程磁盘写入（可选，默认关闭）
        if (!busy && _cfg.WatchProcessIo && IsProcessWriting())
        {
            busy = true;
            why = "下载进程仍在写入磁盘" + (_processHint.Length > 0 ? "（" + _processHint + "）" : "");
        }

        _prevSizes.Clear();
        foreach (var f in files) _prevSizes[f.Path] = f.Size;

        var speed = _lastTotal >= 0 ? Math.Max(0, totalBytes - _lastTotal) : 0;
        _lastTotal = totalBytes;

        Advance(busy, why, now, busy ? "目录仍在写入" : "目录写入已静止",
            FormatSize(totalBytes), speed);
    }

    // ---------- Steam 模式 ----------

    /// <summary>
    /// 「全部任务」是否仍在下载：只看队列里还有没有任务在收数据，
    /// 与"队列里还有几个未完成任务"无关 —— 暂停、排队都不该挡住关机。
    /// </summary>
    internal static bool SteamQueueBusy(bool steamAlive, bool downloading, bool anyAppGrew) =>
        steamAlive && (downloading || anyAppGrew);

    /// <summary>下载完成度。manifest 在下载期间很少刷新，拿不到就返回 -1，不伪造百分比。</summary>
    private void TickSteam(DateTime now)
    {
        if (_steamLibs.Count == 0 || (now - _steamLibsAt).TotalSeconds > 120)
        {
            _steamLibs = SteamScanner.GetLibraryRoots();
            _steamLibsAt = now;
        }

        if (_steamLibs.Count == 0)
        {
            Publish(MonitorState.Waiting, "未找到 Steam 库", "请确认 Steam 已安装，或改用文件夹模式", 0);
            return;
        }

        // Steam 自己写的速率是唯一骗不过的信号：manifest 的 BytesDownloaded 在下载期间恒为 0，
        // downloading 里的文件又是预分配的（体积、修改时间都不再变化），只有这行日志是真的。
        SteamLog.TryGetRate(out var logRate, out _, out var logFresh);
        var liveSpeed = logFresh ? Math.Max(0, logRate) : 0;

        var apps = SteamScanner.ScanApps(_steamLibs);
        var steamAlive = SteamAliveCached(now);
        var downloading = liveSpeed > 50 * 1024;

        var pending = new List<SteamApp>();
        foreach (var a in apps)
            if (!a.FullyInstalled) pending.Add(a);

        // 记录每个未完成任务的字节增长：manifest 只在检查点刷新，但只要有增长就说明真在干活
        var grew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in pending)
        {
            _steamPrev.TryGetValue(a.AppId, out var p);
            if (a.BytesDownloaded > p.Downloaded || a.BytesStaged > p.Staged)
                grew.Add(a.AppId);
            _steamPrev[a.AppId] = (a.BytesDownloaded, a.BytesStaged);
        }

        // 目标二选一：""=整个队列 / 其它=指定 appid
        if (_cfg.SteamAppId.Length == 0)
        {
            if (pending.Count == 0)
            {
                // 队列本来就空：没有可等的东西，明确说清楚，而不是停在「等待下载开始」
                if (_hasSeenActivity)
                    Advance(false, "Steam 队列已无未完成任务", now, "所有下载任务均已完成", "");
                else
                    Publish(MonitorState.Waiting, "队列是空的",
                        "队列里没有未完成的下载，开始一个下载后再监控", 0);

                return;
            }

            var cur = pending.FirstOrDefault(a => a.CachedBytes > 0) ?? pending[0];

            // 任一任务有暂存数据，就说明这个队列确实在干活，避免"一开始就暂停"时永不触发
            if (pending.Any(a => a.CachedBytes > 0 || a.BytesDownloaded > 0))
                _hasSeenActivity = true;

            var busyAll = SteamQueueBusy(steamAlive, downloading, pending.Any(a => grew.Contains(a.AppId)));
            var whyAll = busyAll
                ? $"Steam 正在下载（队列还有 {pending.Count} 个未完成）：{cur.Name}"
                : !steamAlive ? "Steam 已退出，下载已停止"
                : logFresh ? $"队列已无下载活动（{pending.Count} 个任务处于暂停/排队）"
                : "超过 2 分钟无速率回报，判定已停止";

            var curBytes = SteamLiveBytes(cur, now, busyAll ? liveSpeed : 0);

            Advance(busyAll, whyAll, now,
                busyAll ? "正在下载 " + cur.Name : "队列已无下载活动",
                SteamVolume(cur, curBytes), liveSpeed,
                busyAll ? SteamRatio(curBytes, cur.BytesToDownload) : -1);
            return;
        }

        var app = apps.FirstOrDefault(a => a.AppId == _cfg.SteamAppId);
        if (app == null)
        {
            Publish(MonitorState.Waiting, "任务已不在队列中", "可能已被移除或卸载，请重新选择", 0, "");
            return;
        }

        app.SpeedBytes = liveSpeed;

        // 关键：绝不能用"暂存目录有数据"或"staged < stage"判断忙 —— 暂停时这两个条件同样成立，
        // 会导致永远 busy、永不关机（实测 Steam 被挂起 / Suspended 就是这个现象）。
        // 只认三种真实活动：Steam 回报的速率、已下载字节增长、已暂存字节增长。
        var busy = steamAlive && (downloading || grew.Contains(app.AppId));

        var why = busy
            ? (downloading ? "Steam 正在下载：" + app.Name : "Steam 正在安装/校验：" + app.Name)
            : !steamAlive ? "Steam 已退出，下载已停止"
            : app.FullyInstalled ? "Steam 已标记为完全安装"
            : logFresh ? "Steam 报告速率 0：已暂停或已完成"
            : "超过 2 分钟无速率回报，判定已停止";

        if (app.CachedBytes > 0 || app.BytesDownloaded > 0)
            _hasSeenActivity = true;

        var appBytes = SteamLiveBytes(app, now, busy ? liveSpeed : 0);

        Advance(busy, why, now,
            busy ? (downloading ? "正在下载 " + app.Name : "正在安装/校验 " + app.Name)
                 : (app.HasStaging ? "下载已暂停：" + app.Name : "下载已结束：" + app.Name),
            SteamVolume(app, appBytes), liveSpeed,
            busy ? SteamRatio(appBytes, app.BytesToDownload) : -1);
    }

    private static string SteamVolume(SteamApp app, long downloaded)
    {
        if (app.BytesToDownload > 0 && downloaded > 0)
            return FormatSize(downloaded) + " / " + FormatSize(app.BytesToDownload);

        if (app.CachedBytes > 0) return "已缓存 " + FormatSize(app.CachedBytes);
        if (app.BytesToDownload > 0) return "目标 " + FormatSize(app.BytesToDownload);
        return "";
    }

    /// <summary>
    /// 推算当前已下载字节。manifest 的 BytesDownloaded 在下载期间可能一两个小时不刷新，
    /// 直接用它，界面百分比就会长时间停住；这里改成「manifest 当锚点 + 速率按秒累加」，
    /// Steam 一旦写入新值就立刻用真实值重新校准，误差不会一直累积。
    /// </summary>
    private long SteamLiveBytes(SteamApp app, DateTime now, long speedBytes)
    {
        if (!string.Equals(_steamRatioAppId, app.AppId, StringComparison.OrdinalIgnoreCase))
        {
            // 换了监控目标：锚点重来，上一个任务的累加量不能带过来
            _steamRatioAppId = app.AppId;
            _steamAnchorBytes = app.BytesDownloaded;

            // manifest 里的字节数可能已经是几十甚至上百分之前的（Steam 下载中几乎不写它）。
            // 光从这一刻开始累加，起点就比 Steam 客户端低一大截，所以先用日志把它写入之后的量补回来。
            _steamExtraBytes = app.ManifestWriteUtc == DateTime.MinValue
                ? 0
                : SteamLog.BytesBetween(app.ManifestWriteUtc, now);

            _steamAccrualAt = now;
        }
        else if (app.BytesDownloaded > _steamAnchorBytes)
        {
            // Steam 写了新值，以它为准，丢掉推算的部分
            _steamAnchorBytes = app.BytesDownloaded;
            _steamExtraBytes = 0;
        }

        if (speedBytes > 0 && _steamAccrualAt != DateTime.MinValue)
        {
            var dt = (now - _steamAccrualAt).TotalSeconds;

            // 只在正常采样间隔内累加：机器休眠回来后的巨大间隔不能一口气补进去
            if (dt > 0 && dt < 10) _steamExtraBytes += (long)(speedBytes * dt);
        }

        _steamAccrualAt = now;

        var total = _steamAnchorBytes + _steamExtraBytes;
        return app.BytesToDownload > 0 ? Math.Min(total, app.BytesToDownload) : total;
    }

    private static double SteamRatio(long bytes, long toDownload)
    {
        if (toDownload <= 0 || bytes <= 0) return -1;
        return Math.Clamp((double)bytes / toDownload, 0, 1);
    }

    /// <summary>Steam 进程存活检查有 5 秒缓存：这个信息不需要秒级实时，枚举进程却要几百微秒。</summary>
    private bool SteamAliveCached(DateTime now)
    {
        if ((now - _steamAliveAt).TotalSeconds < SteamAliveTtlSeconds) return _steamAlive;

        _steamAliveAt = now;
        _steamAlive = false;

        try
        {
            var procs = Process.GetProcessesByName("steam");
            _steamAlive = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
        }
        catch
        {
            // 取不到就按"未运行"处理，但下面的速率信号仍然生效
        }

        return _steamAlive;
    }

    // ---------- 程序模式 ----------
    /// <summary>
    /// 选定一个软件，判定"它还在不在干活"：
    ///   进程消失 = 任务结束（最确定）；磁盘读写持续静止 = 下载已停。
    /// 同时把同族的子进程算进来（thunder -> ThunderPlatform、steam -> steamwebhelper）。
    /// </summary>
    private void TickProgram(DateTime now)
    {
        var target = _cfg.ProgramName;
        if (target.Length == 0)
        {
            Publish(MonitorState.Waiting, "未选择软件", "请在上方列表中选择要监控的程序", 0);
            return;
        }

        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            all = Array.Empty<Process>();
        }

        ulong delta = 0;
        var alive = new HashSet<int>();
        var count = 0;

        foreach (var p in all)
        {
            try
            {
                if (!IsRelated(p.ProcessName, target)) continue;
                count++;
                alive.Add(p.Id);

                if (NativeMethods.GetIo(p.Id, out var read, out var write))
                {
                    var totalIo = read + write;
                    if (_procWriteBaseline.TryGetValue(p.Id, out var prev) && totalIo > prev)
                        delta += totalIo - prev;
                    _procWriteBaseline[p.Id] = totalIo;
                }
            }
            catch
            {
                // 采样期间进程退出
            }
            finally
            {
                p.Dispose();
            }
        }

        PruneQueuedProcesses(alive);

        if (count == 0)
        {
            // 进程不在了，是最确定的"任务结束"信号
            Advance(false, "程序已退出", now, target + " 已退出", "");
            return;
        }

        var busy = delta > 128 * 1024;
        Advance(busy, busy ? "程序仍在读写磁盘" : "程序 I/O 已静止", now,
            busy ? target + " 仍在读写磁盘" : target + " 磁盘读写已静止", "", (long)delta);
    }

    private static bool IsRelated(string procName, string target) =>
        ProgramScanner.IsRelated(procName, target);

    private void PruneQueuedProcesses(HashSet<int> alive)
    {
        var dead = new List<int>();
        foreach (var key in _procWriteBaseline.Keys)
            if (!alive.Contains(key)) dead.Add(key);

        foreach (var key in dead) _procWriteBaseline.Remove(key);
    }

    // ---------- 共用状态机 ----------

    private void Advance(bool busy, string why, DateTime now, string detail,
        string volume = "", long speed = -1, double downloadProgress = -1)
    {
        if (busy)
        {
            _hasSeenActivity = true;
            _lastActivityUtc = now;
        }

        // 不忙时也刷新原因，界面才能显示"为什么判定已停止"
        if (why.Length > 0) _reason = why;

        var idle = (now - _lastActivityUtc).TotalSeconds;
        var total = _cfg.QuietSeconds + _cfg.GraceSeconds;

        if (!_hasSeenActivity)
        {
            Publish(MonitorState.Waiting, "等待下载开始",
                detail.Length > 0 ? detail : "尚未观测到任何写入活动", 0, volume, speed, downloadProgress);
            return;
        }

        if (idle < _cfg.QuietSeconds)
        {
            Publish(MonitorState.Monitoring, "监控中", detail,
                idle / Math.Max(1, _cfg.QuietSeconds), volume, speed, downloadProgress);
            return;
        }

        if (idle < total)
        {
            var remaining = total - idle;
            Publish(MonitorState.Countdown, "即将" + ActionVerb(),
                $"剩余 {remaining:F0} 秒，期间恢复下载会自动取消",
                (idle - _cfg.QuietSeconds) / Math.Max(1, _cfg.GraceSeconds), volume, speed);
            return;
        }

        // 判定完成
        _completed = true;
        _running = false;
        _timer?.Dispose();
        _timer = null;

        Publish(MonitorState.Finished, "下载已完成",
            _cfg.DrillMode ? "演练模式：未真正执行动作"
            : _cfg.Action == PowerAction.Shutdown
                ? "已执行：关机（" + PowerController.ShutdownDelaySeconds + " 秒内可 shutdown /a 撤销）"
                : "已执行：" + ActionVerb(), 1, volume);

        try
        {
            Completed?.Invoke(_cfg.Action);
        }
        catch
        {
            // 订阅方异常不能让定时器线程带着未处理异常退出（会直接终止进程）
        }
    }

    private string ActionVerb() => _cfg.Action switch
    {
        PowerAction.Shutdown => "关机",
        PowerAction.Sleep => "睡眠",
        PowerAction.Hibernate => "休眠",
        _ => "提醒"
    };

    private void Publish(MonitorState state, string headline, string detail, double progress,
        string volume = "", long speed = -1, double downloadProgress = -1)
    {
        _state = state;

        var accent = state switch
        {
            MonitorState.Monitoring => "#4C8DFF",
            MonitorState.Countdown => "#EF9F27",
            MonitorState.Finished => "#3DDC97",
            _ => "#8A8A99"
        };

        var status = new EngineStatus
        {
            State = state,
            Headline = headline,
            Detail = detail,
            Reason = _reason,
            Progress = Math.Clamp(progress, 0, 1),
            DownloadProgress = downloadProgress < 0 ? -1 : Math.Clamp(downloadProgress, 0, 1),
            Accent = accent,
            Volume = volume ?? "",
            SpeedBytes = speed,
            IdleSeconds = (int)Math.Max(0, (DateTime.UtcNow - _lastActivityUtc).TotalSeconds)
        };

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
            // 界面订阅方异常不能把定时器线程带走
        }
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return v.ToString(i == 0 ? "0" : "0.0") + " " + units[i];
    }

    private static bool IsTempFile(string name)
    {
        foreach (var s in TempSuffixes)
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>尝试以独占方式打开：成功=无人在写；失败=仍被占用。</summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct FileEntry(string Path, string Name, long Size, DateTime LastWriteUtc);

    /// <summary>目录快照。复用缓冲区，避免每秒给 GC 制造上千个 FileInfo。</summary>
    private List<FileEntry> Snapshot(string dir, bool recursive)
    {
        var result = _snapshotBuffer;
        result.Clear();
        if (!Directory.Exists(dir)) return result;

        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var p in Directory.EnumerateFiles(dir, "*", opt))
            {
                try
                {
                    var fi = new FileInfo(p);
                    result.Add(new FileEntry(p, fi.Name, fi.Length, fi.LastWriteTimeUtc));
                }
                catch
                {
                    // 单个文件读取失败忽略
                }

                if (result.Count >= 3000) break;
            }
        }
        catch
        {
            // 目录枚举失败（权限/拔盘）时返回已收集部分
        }

        return result;
    }

    private bool IsProcessWriting()
    {
        var busy = false;
        _processHint = "";
        var alive = new HashSet<int>();

        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        foreach (var p in all)
        {
            try
            {
                var name = p.ProcessName;
                var match = _cfg.ProcessFilter.Length > 0
                    ? name.Contains(_cfg.ProcessFilter, StringComparison.OrdinalIgnoreCase)
                    : KnownDownloaders.Contains(name);

                if (!match) continue;

                var write = NativeMethods.GetWriteBytes(p.Id);
                if (write == 0) continue;

                alive.Add(p.Id);
                if (_procWriteBaseline.TryGetValue(p.Id, out var prev) && write > prev && write - prev > 256 * 1024)
                {
                    busy = true;
                    _processHint = name;
                }

                _procWriteBaseline[p.Id] = write;
            }
            catch
            {
                // 进程已退出等情况忽略
            }
            finally
            {
                p.Dispose();
            }
        }

        PruneQueuedProcesses(alive);
        return busy;
    }
}
