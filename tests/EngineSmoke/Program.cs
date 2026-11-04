using System;
using System.IO;
using System.Linq;
using System.Threading;
using ShutAfter.Core;

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "dw_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            CaseA_DownloadCompletes(root);
            CaseB_EmptyDirNeverFires(root);
            CaseC_ResumeCancelsCountdown(root);
            CaseD_SteamScan();
            CaseE_SteamIdleNeverFires();
            CaseF_ProgramScan();
            CaseG_SteamLogRate();
            CaseH_SteamLiveRate();
            CaseI_SteamStalledFires();
            CaseJ_SteamScanPerf();
            CaseK_PowerSafety();
            CaseL_SteamQueueBusy();
            CaseM_ManifestCache();
            CaseN_SteamProgressAccrual();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(_failures == 0 ? "ALL PASS" : "FAILURES: " + _failures);
        return _failures == 0 ? 0 : 1;
    }

    private static AppConfig Cfg(string dir, int quiet, int grace) => new()
    {
        WatchMode = WatchMode.Folder,
        WatchPath = dir,
        QuietSeconds = quiet,
        GraceSeconds = grace,
        WatchTempSuffix = true,
        ProbeHandle = true,
        WatchProcessIo = false,
        Recursive = false,
        DrillMode = true,
        Action = PowerAction.NotifyOnly
    };

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "  " + detail);
        if (!ok) _failures++;
    }

    private static void CaseA_DownloadCompletes(string root)
    {
        Console.WriteLine("[A] 完整下载流程：写入 -> 改名 -> 应判定完成");
        var dir = Path.Combine(root, "a");
        Directory.CreateDirectory(dir);

        var eng = new MonitorEngine(Cfg(dir, 2, 2));
        var done = false;
        DateTime doneAt = default;
        eng.Completed += _ => { done = true; doneAt = DateTime.UtcNow; };
        eng.Start();

        var final = Path.Combine(dir, "movie.mp4");
        var tmp = final + ".crdownload";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            for (var i = 0; i < 10; i++)
            {
                fs.Write(new byte[64 * 1024], 0, 64 * 1024);
                fs.Flush();
                Thread.Sleep(300);
            }
        }

        File.Move(tmp, final);
        var finishedAt = DateTime.UtcNow;

        for (var i = 0; i < 150 && !done; i++) Thread.Sleep(100);
        eng.Dispose();

        var elapsed = done ? (doneAt - finishedAt).TotalSeconds : -1;
        Check("A1 触发完成", done, "done=" + done);
        Check("A2 延迟落在 (3s, 9s)", done && elapsed > 3 && elapsed < 9, "elapsed=" + elapsed.ToString("F1") + "s");
    }

    private static void CaseB_EmptyDirNeverFires(string root)
    {
        Console.WriteLine("[B] 空目录静置：不得触发（防止「没下载就关机」）");
        var dir = Path.Combine(root, "b");
        Directory.CreateDirectory(dir);

        var eng = new MonitorEngine(Cfg(dir, 1, 1));
        var done = false;
        eng.Completed += _ => done = true;
        eng.Start();

        Thread.Sleep(5000);
        eng.Dispose();

        Check("B1 未误触发", !done, "done=" + done);
    }

    private static void CaseC_ResumeCancelsCountdown(string root)
    {
        Console.WriteLine("[C] 倒计时中恢复写入：应取消并重新计时");
        var dir = Path.Combine(root, "c");
        Directory.CreateDirectory(dir);

        var eng = new MonitorEngine(Cfg(dir, 2, 3));
        var fireCount = 0;
        DateTime firedAt = default;
        eng.Completed += _ => { fireCount++; firedAt = DateTime.UtcNow; };
        eng.Start();

        var file = Path.Combine(dir, "big.iso");

        // 第一段写入，随后静默 3s -> 应进入倒计时（2..5s 区间）
        WriteChunk(file, 4);
        Thread.Sleep(3000);

        // 恢复写入，倒计时必须被打断
        var resumeAt = DateTime.UtcNow;
        WriteChunk(file, 4);
        Thread.Sleep(3000);

        Check("C1 恢复写入后仍未触发", fireCount == 0, "fired=" + fireCount);

        // 之后静置，应当触发；触发点至少要比 resumeAt 晚 4s
        for (var i = 0; i < 150 && fireCount == 0; i++) Thread.Sleep(100);
        eng.Dispose();

        var since = fireCount > 0 ? (firedAt - resumeAt).TotalSeconds : -1;
        Check("C2 最终触发", fireCount == 1, "fired=" + fireCount);
        Check("C3 从恢复写入算起 >= 4s", since > 4, "since=" + since.ToString("F1") + "s");
    }

    private static void CaseD_SteamScan()
    {
        Console.WriteLine("[D] Steam 库与清单解析");
        var libs = SteamScanner.GetLibraryRoots();
        Check("D1 识别到 Steam 库", libs.Count > 0, "libs=" + libs.Count + " [" + string.Join(", ", libs) + "]");

        var apps = SteamScanner.ScanApps(libs);
        Check("D2 解析到 appmanifest", apps.Count > 0, "apps=" + apps.Count);

        var named = apps.Count(a => a.Name.Length > 0 && a.AppId.Length > 0);
        Check("D3 名称与 appid 完整", named == apps.Count, "named=" + named + "/" + apps.Count);

        foreach (var a in apps.Take(4))
            Console.WriteLine($"        {a.AppId}  {a.Name}  flags={a.StateFlags}  " +
                              $"{a.BytesDownloaded}/{a.BytesToDownload}  installed={a.FullyInstalled}");
    }

    private static void CaseE_SteamIdleNeverFires()
    {
        Console.WriteLine("[E] Steam 模式：指定的任务不存在时不得触发");
        var cfg = new AppConfig
        {
            WatchMode = WatchMode.Steam,
            SteamAppId = "9999999",
            QuietSeconds = 1,
            GraceSeconds = 1,
            DrillMode = true,
            Action = PowerAction.NotifyOnly
        };

        var eng = new MonitorEngine(cfg);
        var fired = 0;
        eng.Completed += _ => fired++;
        eng.Start();

        Thread.Sleep(4500);
        eng.Dispose();

        Check("E1 未误触发", fired == 0, "fired=" + fired);
    }

    private static void CaseF_ProgramScan()
    {
        Console.WriteLine("[F] 程序模式：候选列表");
        var apps = ProgramScanner.GetCandidates();
        Check("F1 候选非空", apps.Count > 0, "count=" + apps.Count);
        Check("F2 名称均有效", apps.All(a => a.Name.Length > 0), "bad=" + apps.Count(a => a.Name.Length == 0));
        Check("F3 按名称去重", apps.Select(a => a.Name.ToLowerInvariant()).Distinct().Count() == apps.Count,
            "uniq=" + apps.Select(a => a.Name.ToLowerInvariant()).Distinct().Count() + "/" + apps.Count);

        foreach (var a in apps.Take(5)) Console.WriteLine("        " + a.Display);
    }

    private static void CaseG_SteamLogRate()
    {
        Console.WriteLine("[G] Steam 日志：真实下载速率（manifest 不更新时唯一可靠信号）");
        var ok = SteamLog.TryGetRate(out var rate, out var stamp, out var fresh);
        Check("G1 能解析 content_log 速率行", ok, "ok=" + ok);

        if (!ok) return;

        Console.WriteLine($"        {stamp:HH:mm:ss}  {rate / 1024.0 / 1024.0:F2} MB/s  新鲜={fresh}");
        Check("G2 速率非负", rate >= 0, "rate=" + rate);
        Check("G3 时间戳可解析", stamp != default, "stamp=" + stamp.ToString("HH:mm:ss"));
    }

    private static void CaseH_SteamLiveRate()
    {
        Console.WriteLine("[H] Steam 下载中：状态文本必须带真实速率（不能是 0 B/s）");
        SteamLog.TryGetRate(out var rate, out var stamp, out var fresh);

        if (!fresh || rate < 100 * 1024)
        {
            Console.WriteLine($"  SKIP  当前没有 Steam 下载（rate={rate / 1024.0 / 1024.0:F2} MB/s, fresh={fresh}）");
            return;
        }

        var cfg = new AppConfig
        {
            WatchMode = WatchMode.Steam,
            SteamAppId = "",
            QuietSeconds = 90,
            GraceSeconds = 60,
            DrillMode = true,
            Action = PowerAction.NotifyOnly
        };

        var eng = new MonitorEngine(cfg);
        var seen = new System.Collections.Generic.List<string>();
        var progress = -1.0;
        var speed = -1L;
        var volume = "";
        eng.StatusChanged += s =>
        {
            if (s.Detail.Length > 0) seen.Add(s.Detail);
            if (s.DownloadProgress >= 0) progress = s.DownloadProgress;
            if (s.SpeedBytes > 0) speed = s.SpeedBytes;
            if (s.Volume.Length > 0) volume = s.Volume;
        };
        eng.Start();

        Thread.Sleep(2500);
        eng.Dispose();

        // Dispose 会再推一条"监控已关闭"，取之前那条真实状态
        var last = seen.LastOrDefault(x => !x.Contains("监控已关闭") && !x.Contains("监控已停止")) ?? "";

        Console.WriteLine($"        日志速率 {stamp:HH:mm:ss} {rate / 1024.0 / 1024.0:F2} MB/s");
        Console.WriteLine("        状态：" + last);

        Check("H1 拿到实时速率字段", speed > 0, $"speed={speed / 1024.0 / 1024.0:F2} MB/s");
        Check("H2 体积读数非空", volume.Length > 0, "volume=" + volume);
        Check("H3 进度环拿到真实下载进度", progress > 0 && progress <= 1,
            progress < 0 ? "DownloadProgress=-1（进度环只能显示静默进度）" : $"progress={progress:P0}");
    }

    private static void CaseI_SteamStalledFires()
    {
        Console.WriteLine("[I] Steam 下载停滞（暂停/Suspended）：应判定停止并触发");

        SteamLog.TryGetRate(out var rate, out _, out var fresh);
        if (fresh && rate > 50 * 1024)
        {
            Console.WriteLine("  SKIP  Steam 正在下载，无法构造停滞场景");
            return;
        }

        var apps = SteamScanner.ScanApps(SteamScanner.GetLibraryRoots());
        var stalled = apps.FirstOrDefault(a => !a.FullyInstalled && a.CachedBytes > 0);

        if (stalled == null)
        {
            Console.WriteLine("  SKIP  当前没有停滞中的 Steam 任务");
            return;
        }

        Console.WriteLine($"        任务 {stalled.Name}  缓存 {MonitorEngine.FormatSize(stalled.CachedBytes)}  flags={stalled.StateFlags}");

        var cfg = new AppConfig
        {
            WatchMode = WatchMode.Steam,
            SteamAppId = stalled.AppId,
            QuietSeconds = 2,
            GraceSeconds = 2,
            DrillMode = true,
            Action = PowerAction.NotifyOnly
        };

        var eng = new MonitorEngine(cfg);
        var fired = 0;
        var reason = "";
        eng.Completed += _ => fired++;
        eng.StatusChanged += s => { if (s.Reason.Length > 0) reason = s.Reason; };
        eng.Start();

        for (var i = 0; i < 120 && fired == 0; i++) Thread.Sleep(100);
        eng.Dispose();

        Console.WriteLine("        判定依据：" + reason);
        Check("I1 停滞任务能触发完成", fired > 0, "fired=" + fired);
    }

    private static void CaseJ_SteamScanPerf()
    {
        Console.WriteLine("[J] Steam 模式单次采样耗时（定时器周期 1000ms）");

        var libs = SteamScanner.GetLibraryRoots();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) SteamScanner.ScanApps(libs);
        sw.Stop();
        var avg = sw.ElapsedMilliseconds / 5.0;
        Console.WriteLine($"        ScanApps 平均 {avg:F0} ms");
        Check("J1 单次扫描 < 400ms（不拖累定时器）", avg < 400, avg.ToString("F0") + "ms");

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        SteamLog.TryGetRate(out _, out _, out _);
        sw2.Stop();
        Console.WriteLine($"        日志读取 {sw2.ElapsedMilliseconds} ms");
        Check("J2 日志读取 < 100ms", sw2.ElapsedMilliseconds < 100, sw2.ElapsedMilliseconds + "ms");
    }

    private static void CaseK_PowerSafety()
    {
        Console.WriteLine("[K] 电源动作：演练模式与仅提醒必须不执行");

        var ok = PowerController.Execute(PowerAction.Shutdown, true, out var msg);
        Check("K1 演练模式不关机", ok && msg.Contains("演练"), msg);

        Thread.Sleep(400);

        var ok2 = PowerController.Execute(PowerAction.NotifyOnly, false, out var msg2);
        Check("K2 仅提醒保持运行", ok2 && msg2.Contains("保持运行"), msg2);
    }

    private static void CaseL_SteamQueueBusy()
    {
        Console.WriteLine("[L] 「全部任务」：暂停/排队的任务不得挡住判定");

        // 队列里还有未完成任务，但谁都没在收数据 —— 这正是"用户暂停后挂机"的场景
        Check("L1 有未完成任务但无活动 → 不忙（会触发）",
            !MonitorEngine.SteamQueueBusy(true, false, false), "busy=false");

        Check("L2 Steam 报速率 → 忙",
            MonitorEngine.SteamQueueBusy(true, true, false), "busy=true");

        Check("L3 字节增长 → 忙",
            MonitorEngine.SteamQueueBusy(true, false, true), "busy=true");

        Check("L4 Steam 已退出 → 不忙",
            !MonitorEngine.SteamQueueBusy(false, true, true), "busy=false");
    }

    private static void CaseN_SteamProgressAccrual()
    {
        Console.WriteLine("[N] Steam 进度推算：manifest 不刷新时也要继续上涨，且不回退、不超 100%");

        SteamLog.TryGetRate(out var rate, out var stamp, out var fresh);
        if (!fresh || rate < 100 * 1024)
        {
            Console.WriteLine("  SKIP  当前没有 Steam 下载");
            return;
        }

        var cfg = new AppConfig
        {
            WatchMode = WatchMode.Steam,
            SteamAppId = "",
            QuietSeconds = 90,
            GraceSeconds = 60,
            DrillMode = true,
            Action = PowerAction.NotifyOnly
        };

        var eng = new MonitorEngine(cfg);
        var samples = new System.Collections.Generic.List<double>();
        eng.StatusChanged += s =>
        {
            if (s.DownloadProgress >= 0) samples.Add(s.DownloadProgress);
        };
        eng.Start();

        // 12 秒 ≈ 12 个采样；这段区间 manifest 通常一动不动，正是要验证的场景
        Thread.Sleep(12000);
        eng.Dispose();

        if (samples.Count < 5)
        {
            Console.WriteLine($"  SKIP  采样太少（{samples.Count} 条）");
            return;
        }

        var back = 0;
        for (var i = 1; i < samples.Count; i++)
            if (samples[i] < samples[i - 1] - 1e-9) back++;

        var first = samples.First();
        var last = samples.Last();

        Console.WriteLine($"        {samples.Count} 个采样，{first:P3} → {last:P3}（日志速率行 {stamp:HH:mm:ss}）");

        Check("N1 进度不回退", back == 0, $"回退 {back} 次");
        Check("N2 manifest 未刷新时进度仍在上涨", last > first, $"{first:P3} → {last:P3}");
        Check("N3 不越过 100%", last <= 1.0, last.ToString("P2"));
    }

    private static void CaseM_ManifestCache()
    {
        Console.WriteLine("[M] manifest 缓存：内容不变时不重复解析、结果仍是独立实例");

        var libs = SteamScanner.GetLibraryRoots();
        if (libs.Count == 0)
        {
            Console.WriteLine("  SKIP  没有 Steam 库");
            return;
        }

        var first = SteamScanner.ScanApps(libs);
        if (first.Count == 0)
        {
            Console.WriteLine("  SKIP  没有 appmanifest");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) SteamScanner.ScanApps(libs);
        sw.Stop();
        var perScan = sw.Elapsed.TotalMilliseconds / 10;

        var second = SteamScanner.ScanApps(libs);

        Check("M1 缓存命中后结果一致", first.Count == second.Count,
            first.Count + " vs " + second.Count);
        Check("M2 返回独立实例（缓存模板不被改写）",
            !ReferenceEquals(first[0], second[0]) &&
            first[0].AppId == second[0].AppId &&
            first[0].Name == second[0].Name,
            first[0].AppId + " " + first[0].Name);
        Check("M3 缓存扫描 < 60ms", perScan < 60, perScan.ToString("F1") + "ms");
    }

    private static void WriteChunk(string path, int rounds)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        for (var i = 0; i < rounds; i++)
        {
            fs.Write(new byte[64 * 1024], 0, 64 * 1024);
            fs.Flush();
            Thread.Sleep(300);
        }
    }
}