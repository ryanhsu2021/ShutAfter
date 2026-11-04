using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ShutAfter.Core;

/// <summary>
/// 从 Steam 自己的 content_log.txt 读取真实下载速率。
///
/// 为什么不用 manifest：实测 Steam 下载期间 appmanifest 的 BytesDownloaded 恒为 0
/// （4115450 已落盘 140GB 时该字段仍是 0），且 downloading 目录内的文件是预分配的，
/// 体积与修改时间都不再变化 —— 所有基于"文件大小/时间戳"的猜测在 Steam 上全部失效。
/// 唯一可靠且实时的信号就是这行日志，Steam 每 60 秒刷一次。
/// </summary>
public static class SteamLog
{
    private static readonly Regex RateRegex = new(
        @"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s+Current download rate:\s+([\d.]+)\s+Mbps",
        RegexOptions.Compiled);

    /// <summary>分块统计行：累计字节数 / 耗时，用于 5 分钟粒度的平均速率。</summary>
    private static readonly Regex StatsRegex = new(
        @"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s+stats:\s+\(CDN[^)]*\)[^:]*:\s+(\d+)\s+Bytes,\s+(\d+)\s+sec",
        RegexOptions.Compiled);

    private const int TailBytes = 64 * 1024;

    // 日志被追加时才需要重读；Steam 每 60 秒才写一行速率，
    // 中间的上百次调用全部走缓存，省掉每秒 64KB 的文件读取 + 正则匹配。
    private static long _cachedLength = -1;
    private static DateTime _cachedStamp = DateTime.MinValue;
    private static string? _cachedText;

    /// <summary>读取 Steam 报告的瞬时速率。日志超过 120 秒未更新则视为已停止。</summary>
    public static bool TryGetRate(out long bytesPerSec, out DateTime stamp, out bool fresh)
    {
        bytesPerSec = 0;
        stamp = DateTime.MinValue;
        fresh = false;

        var text = ReadTail(out var now);
        if (text == null) return false;

        var matches = RateRegex.Matches(text);
        if (matches.Count == 0) return false;

        var m = matches[^1];
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                null, System.Globalization.DateTimeStyles.None, out stamp))
            return false;

        if (!double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mbps))
            return false;

        // Steam 的 Mbps 是 Mbit/s
        // Steam 的 Mbps 是 Mbit/s（十进制），乘 1000² 而不是 1024²，否则按日志做长区间积分会偏高约 4.8%
        bytesPerSec = (long)(mbps * 1_000_000 / 8);
        fresh = (now - stamp).TotalSeconds <= 120;
        return true;
    }

    /// <summary>
    /// 还原 [since, until] 区间内 Steam 实际传输的字节数：把日志里的速率行按时间积分。
    ///
    /// 用途：appmanifest 在下载期间可能一两个小时不写，manifest 里的字节数就是"过期"的。
    /// 拿它的最后写入时刻当起点，用这段日志把缺口补回来，进度才和 Steam 客户端的读数对得上。
    /// </summary>
    public static long BytesBetween(DateTime sinceUtc, DateTime untilUtc)
    {
        if (untilUtc <= sinceUtc) return 0;

        var root = SteamScanner.GetSteamRoot();
        if (root.Length == 0) return 0;

        var path = Path.Combine(root, "logs", "content_log.txt");

        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var len = fs.Length;
            if (len == 0) return 0;

            // 只回溯最近 512KB：足够覆盖十几小时的下载，又不至于把整个日志读进内存
            var take = (int)Math.Min(len, 512 * 1024);
            fs.Seek(len - take, SeekOrigin.Begin);

            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            if (read <= 0) return 0;

            text = Encoding.UTF8.GetString(buf, 0, read);
        }
        catch
        {
            return 0;
        }

        var matches = RateRegex.Matches(text);
        if (matches.Count < 2) return 0;

        var rows = new List<(DateTime Utc, double Mbps)>(matches.Count);
        foreach (Match m in matches)
        {
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                    null, System.Globalization.DateTimeStyles.None, out var local)) continue;

            if (!double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var mbps)) continue;

            // 日志写的是本地时间，统一换成 UTC 再和 manifest 时间戳比较
            rows.Add((DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(), mbps));
        }

        if (rows.Count < 2) return 0;
        rows.Sort((a, b) => a.Utc.CompareTo(b.Utc));

        var total = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            // 第 i 行的速率代表它到下一行之间的这段时间，最后一行补到 until 为止
            var from = rows[i].Utc;
            var to = i + 1 < rows.Count ? rows[i + 1].Utc : untilUtc;

            var a = from > sinceUtc ? from : sinceUtc;
            var b = to < untilUtc ? to : untilUtc;
            if (b <= a) continue;

            var span = (b - a).TotalSeconds;

            // 中间断档（Steam 关过）不能按最后一段速率一路乘下去
            if (span > 300) continue;

            total += rows[i].Mbps * 1_000_000 / 8 * span;
        }

        return (long)total;
    }

    /// <summary>分块统计的累计字节数，用来交叉验证速率是否真的在跑。</summary>
    public static long GetLastStatsBytes(out DateTime stamp)
    {
        stamp = DateTime.MinValue;
        var text = ReadTail(out _);
        if (text == null) return 0;

        var matches = StatsRegex.Matches(text);
        if (matches.Count == 0) return 0;

        var m = matches[^1];
        DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
            null, System.Globalization.DateTimeStyles.None, out stamp);

        return long.TryParse(m.Groups[3].Value, out var sec) && sec > 0
               && long.TryParse(m.Groups[2].Value, out var bytes) ? bytes : 0;
    }

    private static string? ReadTail(out DateTime now)
    {
        now = DateTime.Now;

        var root = SteamScanner.GetSteamRoot();
        if (root.Length == 0) return null;

        var path = Path.Combine(root, "logs", "content_log.txt");

        long length;
        DateTime stamp;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            length = fi.Length;
            stamp = fi.LastWriteTimeUtc;
        }
        catch
        {
            return null;
        }

        if (_cachedText != null && length == _cachedLength && stamp == _cachedStamp)
            return _cachedText;

        try
        {
            // Steam 正在写，必须允许共享读写
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var len = fs.Length;
            if (len == 0) return null;

            var take = (int)Math.Min(len, TailBytes);
            fs.Seek(len - take, SeekOrigin.Begin);

            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            if (read <= 0) return null;

            var text = Encoding.UTF8.GetString(buf, 0, read);
            _cachedText = text;
            _cachedLength = length;
            _cachedStamp = stamp;
            return text;
        }
        catch
        {
            return null;
        }
    }
}
