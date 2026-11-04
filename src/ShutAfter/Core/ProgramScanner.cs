using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ShutAfter.Core;

public sealed class RunningApp
{
    public string Name { get; init; } = "";
    public int Pid { get; init; }
    public string Title { get; init; } = "";

    public string Display
    {
        get
        {
            if (string.IsNullOrEmpty(Title)) return Name;
            var t = Title.Length > 38 ? Title[..38] + "…" : Title;
            return Name + "　·　" + t;
        }
    }
}

public static class ProgramScanner
{
    /// <summary>常见的纯下载器：进程退出或磁盘写入静止即可认为任务结束。</summary>
    public static readonly HashSet<string> KnownDownloaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "thunder", "thunderplatform", "xunlei", "idman", "aria2c", "qbittorrent", "utorrent",
        "bittorrent", "transmission", "motrix", "fdm", "freedownloadmanager", "baidunetdisk",
        "quark", "weiyun", "115", "115browser", "steam", "steamwebhelper", "epicgameslauncher",
        "origin", "eadesktop", "battle.net", "ubisoftconnect", "goggalaxy", "wget", "curl"
    };

    /// <summary>列出可作为监控对象的进程（有主窗口的 + 已知下载器），按进程名去重。</summary>
    public static List<RunningApp> GetCandidates()
    {
        var list = new List<RunningApp>();
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            return list;
        }

        foreach (var p in all)
        {
            try
            {
                var name = p.ProcessName;
                var title = "";
                try { title = p.MainWindowTitle ?? ""; } catch { }

                var known = KnownDownloaders.Contains(name);
                if (!known && title.Length == 0) continue;

                list.Add(new RunningApp { Name = name, Pid = p.Id, Title = title });
            }
            catch
            {
                // 进程已退出或权限不足，跳过
            }
            finally
            {
                p.Dispose();
            }
        }

        var sorted = list
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.Title.Length).First())
            .OrderByDescending(a => KnownDownloaders.Contains(a.Name))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 把同族子进程合并到一个条目（steamwebhelper -> steam），避免列表重复
        var result = new List<RunningApp>();
        foreach (var app in sorted)
        {
            if (result.Any(r => IsRelated(r.Name, app.Name))) continue;
            result.Add(app);
        }

        return result;
    }

    /// <summary>同族进程判定：完全同名，或长度足够的前缀关系（thunder / ThunderPlatform）。</summary>
    public static bool IsRelated(string procName, string target)
    {
        if (procName.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;

        var min = Math.Min(procName.Length, target.Length);
        if (min < 5) return false; // 名字太短时不做前缀匹配，避免误伤

        return procName.StartsWith(target, StringComparison.OrdinalIgnoreCase)
               || target.StartsWith(procName, StringComparison.OrdinalIgnoreCase);
    }
}
