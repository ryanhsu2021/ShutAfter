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
    public string Detail { get; init; } = "";
    public string Reason { get; init; } = "";
    public double Progress { get; init; }
    public string Accent { get; init; } = "#4C8DFF";
    public string TotalSize { get; init; } = "-";
    public int IdleSeconds { get; init; }
}

/// <summary>
/// 下载完成判定引擎。两种模式共用同一套"静默 -> 倒计时 -> 执行"状态机。
///
/// 文件夹模式：不问"网卡还剩多少流量"，只问"磁盘上还有没有人在写"。
///   1. 下载器临时文件（.crdownload/.part/...）—— 下载器自己标记的"未完成"
///   2. 文件体积/数量相对上一次采样变化
///   3. 目标文件仍被其他进程独占持有句柄（FileShare.None 试探失败）
///   4. 指定下载进程的累计磁盘写入量仍在增长（可选）
///
/// Steam 模式：直接读 steamapps/appmanifest_&lt;appid&gt;.acf 的 StateFlags / Bytes*，
///   再叠加 downloading 暂存目录是否还有文件，覆盖"下载完但还在安装/校验"的阶段。
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

    private readonly AppConfig _cfg;
    private readonly Dictionary<string, long> _prevSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ulong> _procWriteBaseline = new();

    private System.Threading.Timer? _timer;
    private int _ticking;
    private DateTime _lastActivityUtc;
    private bool _hasSeenActivity;
    private bool _completed;
    private bool _running;
    private string _reason = "等待下载开始";
    private string _processHint = "";

    private List<string> _steamLibs = new();
    private DateTime _steamLibsAt = DateTime.MinValue;

    /// <summary>每个 appid 上一次的进度快照，用于判断"是否还有增长"。</summary>
    private readonly Dictionary<string, (long Downloaded, long Staged)> _steamPrev = new(StringComparer.OrdinalIgnoreCase);
    private long _lastTotalBytes = -1;

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
        _prevSizes.Clear();
        _procWriteBaseline.Clear();
        _steamLibs.Clear();
        _steamLibsAt = DateTime.MinValue;
        _steamPrev.Clear();
        _lastTotalBytes = -1;

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

    // ---------- 文件夹模式 ----------

    private void TickFolder(DateTime now)
    {
        if (string.IsNullOrWhiteSpace(_cfg.WatchPath) || !Directory.Exists(_cfg.WatchPath))
        {
            Publish(MonitorState.Waiting, "监控目录不可用", "请重新选择一个有效的下载目录", 0);
            return;
        }

        var files = Snapshot(_cfg.WatchPath, _cfg.Recursive);
        var totalBytes = files.Sum(f => f.Size);
        var busy = false;
        var why = "";

        // 信号 1：下载器临时文件 —— 最强的"未完成"标记
        if (_cfg.WatchTempSuffix)
        {
            var temp = files.FirstOrDefault(f => IsTempFile(f.Name));
            if (!string.IsNullOrEmpty(temp.Name))
            {
                busy = true;
                why = "临时文件仍在：" + temp.Name;
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
        if (!busy && _cfg.ProbeHandle)
        {
            foreach (var f in files.OrderByDescending(x => x.LastWriteUtc).Take(60))
            {
                if (IsLocked(f.Path))
                {
                    busy = true;
                    why = "文件仍被进程占用：" + f.Name;
                    break;
                }
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

        var speed = _lastTotalBytes >= 0 ? Math.Max(0, totalBytes - _lastTotalBytes) : 0;
        _lastTotalBytes = totalBytes;

        var detail = speed > 0 ? $"↓ {FormatSize(speed)}/s" : "";

        Advance(busy, why, now, detail, totalBytes);
    }

    // ---------- Steam 模式 ----------

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

        SteamApp? app;
        if (_cfg.SteamAppId.Length > 0)
        {
            app = apps.FirstOrDefault(a => a.AppId == _cfg.SteamAppId);
            if (app == null)
            {
                Publish(MonitorState.Waiting, "任务已不在 Steam 队列中", "可能已被移除或卸载，请重新选择", 0);
                return;
            }
        }
        else
        {
            // 优先"确实在收数据"的，其次"还有暂存数据"的
            app = apps.FirstOrDefault(a => !a.FullyInstalled && liveSpeed > 0)
                  ?? apps.FirstOrDefault(a => !a.FullyInstalled && a.HasStaging)
                  ?? apps.FirstOrDefault(a => !a.FullyInstalled);
        }

        if (app == null)
        {
            Advance(false, "Steam 队列中没有未完成的任务", now, "当前没有进行中的下载任务", 0);
            return;
        }

        app.SpeedBytes = liveSpeed;

        // manifest 只在检查点刷新，但只要数值有增长，就说明还在干活（下载中 或 安装/校验中）
        _steamPrev.TryGetValue(app.AppId, out var prev);
        var bdGrew = app.BytesDownloaded > prev.Downloaded;
        var stagedGrew = app.BytesStaged > prev.Staged;
        _steamPrev[app.AppId] = (app.BytesDownloaded, app.BytesStaged);

        var steamAlive = IsSteamRunning();

        // 关键：绝不能用"暂存目录有数据"或"staged < stage"判断忙 —— 暂停时这两个条件同样成立，
        // 会导致永远 busy、永不关机（实测 Steam 被挂起 / Suspended 就是这个现象）。
        // 只认三种真实活动：Steam 回报的速率、已下载字节增长、已暂存字节增长。
        var busy = steamAlive && (liveSpeed > 50 * 1024 || bdGrew || stagedGrew);

        var why = busy
            ? (liveSpeed > 50 * 1024 ? "Steam 正在下载：" + app.Name : "Steam 正在安装/校验：" + app.Name)
            : !steamAlive ? "Steam 已退出，下载已停止"
            : app.FullyInstalled ? "Steam 已标记为完全安装"
            : logFresh ? "Steam 报告速率 0：已暂停或已完成"
            : "超过 2 分钟无速率回报，判定已停止";

        // 暂存目录已有数据 = 这个任务确实下载过，允许"一开始就处于暂停态"也能进入完成判定，
        // 否则 _hasSeenActivity 永远为 false，暂停后再也不会关机。
        if (app.CachedBytes > 0 || app.BytesDownloaded > 0)
            _hasSeenActivity = true;

        Advance(busy, why, now, DescribeApp(app, liveSpeed), 0);
    }

    private static bool IsSteamRunning()
    {
        try
        {
            return Process.GetProcessesByName("steam").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static string DescribeApp(SteamApp app, long speed)
    {
        var parts = new List<string> { app.Name, "↓ " + FormatSize(speed) + "/s" };

        if (app.BytesDownloaded > 0 && app.BytesToDownload > 0)
            parts.Add($"{FormatSize(app.BytesDownloaded)} / {FormatSize(app.BytesToDownload)}（{app.Progress:P0}）");
        else if (app.CachedBytes > 0)
            parts.Add("已缓存 " + FormatSize(app.CachedBytes) + "（Steam 暂未回报进度）");
        else if (app.BytesToDownload > 0)
            parts.Add("目标 " + FormatSize(app.BytesToDownload));

        return string.Join("　·　", parts);
    }

    // ---------- 程序模式 ----------
    /// <summary>
    /// 选定一个软件，判定"它还在不在干活"：
    ///   进程消失 = 任务结束（最确定）；进程磁盘读写持续静止 = 下载已停。
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

        foreach (var dead in _procWriteBaseline.Keys.Where(k => !alive.Contains(k)).ToList())
            _procWriteBaseline.Remove(dead);

        if (count == 0)
        {
            // 进程不在了，是最确定的"任务结束"信号
            Advance(false, "程序已退出", now, target + " 已结束", 0);
            return;
        }

        var busy = delta > 128 * 1024;
        var detail = busy
            ? $"{target}　·　磁盘读写 {FormatSize((long)delta)}/s"
            : $"{target}　·　I/O 静止";

        Advance(busy, busy ? "程序仍在读写磁盘" : "程序 I/O 已静止", now, detail, 0);
    }

    private static bool IsRelated(string procName, string target) =>
        ProgramScanner.IsRelated(procName, target);

    // ---------- 共用状态机 ----------

    private void Advance(bool busy, string why, DateTime now, string detail, long totalBytes)
    {
        if (busy)
        {
            _hasSeenActivity = true;
            _lastActivityUtc = now;
        }

        // 不忙时也刷新原因，界面才能显示"为什么判定已停止"；空串不覆盖（文件夹模式静默时无原因）
        if (why.Length > 0) _reason = why;

        var idle = (now - _lastActivityUtc).TotalSeconds;
        var total = _cfg.QuietSeconds + _cfg.GraceSeconds;
        var suffix = detail.Length > 0 ? "　·　" + detail : "";

        if (!_hasSeenActivity)
        {
            Publish(MonitorState.Waiting, "等待下载开始",
                detail.Length > 0 ? detail : "尚未观测到任何写入活动", 0, totalBytes);
            return;
        }

        if (idle < _cfg.QuietSeconds)
        {
            Publish(MonitorState.Monitoring, "监控中",
                $"静默 {idle:F0}s / {_cfg.QuietSeconds}s{suffix}",
                idle / Math.Max(1, _cfg.QuietSeconds), totalBytes);
            return;
        }

        if (idle < total)
        {
            var remaining = total - idle;
            Publish(MonitorState.Countdown, "即将" + ActionVerb(),
                $"倒计时 {remaining:F0}s，期间有活动会自动取消{suffix}",
                (idle - _cfg.QuietSeconds) / Math.Max(1, _cfg.GraceSeconds), totalBytes);
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
                : "已执行：" + ActionVerb(), 1, totalBytes);

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

    private void Publish(MonitorState state, string headline, string detail, double progress, long? totalBytes = null)
    {
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
            Accent = accent,
            TotalSize = FormatSize(totalBytes ?? 0),
            IdleSeconds = (int)Math.Max(0, (DateTime.UtcNow - _lastActivityUtc).TotalSeconds)
        };

        StatusChanged?.Invoke(status);
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

    private static List<FileEntry> Snapshot(string dir, bool recursive)
    {
        var result = new List<FileEntry>(128);
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

        foreach (var dead in _procWriteBaseline.Keys.Where(k => !alive.Contains(k)).ToList())
            _procWriteBaseline.Remove(dead);

        return busy;
    }
}
