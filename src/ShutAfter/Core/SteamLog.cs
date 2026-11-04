using System;
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
        bytesPerSec = (long)(mbps * 1024 * 1024 / 8);
        fresh = (now - stamp).TotalSeconds <= 120;
        return true;
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
        if (!File.Exists(path)) return null;

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

            return Encoding.UTF8.GetString(buf, 0, read);
        }
        catch
        {
            return null;
        }
    }
}
