using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShutAfter.Core;

public sealed class SteamApp
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public int StateFlags { get; init; }
    public long BytesToDownload { get; init; }
    public long BytesDownloaded { get; init; }
    public long BytesToStage { get; init; }
    public long BytesStaged { get; init; }
    public string LibraryRoot { get; set; } = "";
    public string ManifestPath { get; init; } = "";

    /// <summary>
    /// appmanifest 的最后写入时刻。Steam 下载中几乎不写这个文件，实测可以一个多小时不动，
    /// 这个时间戳就是 manifest 里字节数的"保质期起点"，用来把之后的下载量补回来。
    /// </summary>
    public DateTime ManifestWriteUtc { get; init; }

    /// <summary>本次采样周期内的下载增量，用于显示实时速度；由引擎写入。</summary>
    public long SpeedBytes { get; set; }

    /// <summary>
    /// steamapps\downloading\&lt;appid&gt; 的实际体积。
    /// 注意：这些文件是 Steam 预分配的，下载中体积不变，只能作为"是否还有暂存数据"的判断。
    /// </summary>
    public long CachedBytes { get; set; }

    /// <summary>暂存目录里还有数据 = 下载/安装/校验尚未结束。</summary>
    public bool HasStaging => CachedBytes > 0;

    /// <summary>
    /// StateFlags 的 bit 2 (值 4) = "完全安装"。
    /// 不再要求 BytesDownloaded >= BytesToDownload：该字段在下载期间长时间不刷新（实测恒为 0），
    /// 一旦拿它做与条件，Steam 标记完成后仍会被判成未完成，导致永不关机。
    /// </summary>
    public bool FullyInstalled => (StateFlags & 4) != 0;

    public double Progress =>
        BytesToDownload <= 0 ? (FullyInstalled ? 1 : 0)
                             : Math.Clamp((double)BytesDownloaded / BytesToDownload, 0, 1);

    /// <summary>浅拷贝：缓存里的实例是共享模板，调用方拿到的是自己的副本，避免互相污染。</summary>
    public SteamApp Clone() => new()
    {
        AppId = AppId,
        Name = Name,
        StateFlags = StateFlags,
        BytesToDownload = BytesToDownload,
        BytesDownloaded = BytesDownloaded,
        BytesToStage = BytesToStage,
        BytesStaged = BytesStaged,
        LibraryRoot = LibraryRoot,
        ManifestPath = ManifestPath,
        ManifestWriteUtc = ManifestWriteUtc
    };
}

/// <summary>
/// 通过 Steam 自己的清单文件判断下载状态，而不是猜磁盘活动。
/// 库列表来自注册表 SteamPath + steamapps/libraryfolders.vdf。
///
/// 资源策略：manifest 只在"文件大小或修改时间变了"时才重新解析。
/// 一台装了 200 个游戏的机器，如果每秒把 200 个 acf 全读一遍 + 跑 8 次正则，
/// 光是这一个动作就能吃掉几十毫秒 CPU 和持续的磁盘 IO，而这个值一分钟也变不了几次。
/// </summary>
public static class SteamScanner
{
    private static readonly Regex PathRegex =
        new("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    private static readonly Regex AppIdRegex =
        new("\"appid\"\\s+\"([^\"]*)\"", RegexOptions.Compiled);

    private static readonly Regex NameRegex =
        new("\"name\"\\s+\"([^\"]*)\"", RegexOptions.Compiled);

    private sealed class ManifestCacheEntry
    {
        public long Length;
        public DateTime Stamp;
        public SteamApp Value = null!;
    }

    private static readonly Dictionary<string, ManifestCacheEntry> ManifestCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (DateTime At, long Bytes)> StagingCache =
        new(StringComparer.OrdinalIgnoreCase);

    private const int StagingCacheMs = 8000;

    /// <summary>Steam 安装根目录（日志、库都在它下面）。找不到返回空串。</summary>
    public static string GetSteamRoot()
    {
        string? root = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            root = key?.GetValue("SteamPath") as string;
        }
        catch
        {
            // 注册表不可读时走常见路径兜底
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            foreach (var guess in new[]
                     {
                         @"C:\Program Files (x86)\Steam",
                         @"C:\Program Files\Steam",
                         @"D:\Steam",
                         @"D:\SteamLibrary"
                     })
            {
                if (Directory.Exists(Path.Combine(guess, "steamapps")))
                {
                    root = guess;
                    break;
                }
            }
        }

        return string.IsNullOrWhiteSpace(root) ? "" : root.Replace('/', '\\');
    }

    public static List<string> GetLibraryRoots()
    {
        var roots = new List<string>();

        var steamRoot = GetSteamRoot();
        if (steamRoot.Length > 0)
            AddUnique(roots, steamRoot);

        foreach (var root in roots.ToList())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            try
            {
                var text = File.ReadAllText(vdf);
                foreach (Match m in PathRegex.Matches(text))
                    AddUnique(roots, m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch
            {
                // vdf 读取失败不影响已识别的库
            }
        }

        return roots.Where(r => Directory.Exists(Path.Combine(r, "steamapps"))).ToList();
    }

    public static List<SteamApp> ScanApps(IEnumerable<string> roots)
    {
        var list = new List<SteamApp>();

        foreach (var root in roots)
        {
            var steamApps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(steamApps, "appmanifest_*.acf");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var app = ReadManifest(file);
                if (app == null) continue;

                app.LibraryRoot = root;

                // 已完全安装的不用扫暂存目录，省掉绝大多数开销
                if (!app.FullyInstalled)
                    app.CachedBytes = GetStagingBytesCached(root, app.AppId);

                list.Add(app);
            }
        }

        if (ManifestCache.Count > list.Count + 64) PruneCache(list);

        return list;
    }

    /// <summary>带缓存的 manifest 读取：文件没变就直接用上次的解析结果。</summary>
    private static SteamApp? ReadManifest(string path)
    {
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

        if (ManifestCache.TryGetValue(path, out var cached)
            && cached.Length == length && cached.Stamp == stamp)
            return cached.Value.Clone();

        var parsed = ParseManifest(path);
        if (parsed == null) return null;

        ManifestCache[path] = new ManifestCacheEntry { Length = length, Stamp = stamp, Value = parsed };
        return parsed.Clone();
    }

    /// <summary>清掉已经不存在的 manifest，避免长期运行后缓存无限增长。</summary>
    private static void PruneCache(List<SteamApp> current)
    {
        var alive = new HashSet<string>(current.Select(a => a.ManifestPath), StringComparer.OrdinalIgnoreCase);
        var dead = ManifestCache.Keys.Where(k => !alive.Contains(k)).ToList();
        foreach (var key in dead) ManifestCache.Remove(key);
    }

    /// <summary>
    /// downloading\&lt;appid&gt; 的实际体积。文件数上限 8000，避免超大库拖慢采样。
    /// 8 秒缓存：这个值只用来判断"是否还有暂存数据"，不需要秒级精度，
    /// 而它每次都要深度遍历整个目录 —— 是单次采样里最贵的一项。
    /// </summary>
    public static long GetStagingBytesCached(string libraryRoot, string appId)
    {
        if (string.IsNullOrEmpty(appId)) return 0;

        var key = libraryRoot + "|" + appId;
        var now = DateTime.UtcNow;

        if (StagingCache.TryGetValue(key, out var hit)
            && (now - hit.At).TotalMilliseconds < StagingCacheMs)
            return hit.Bytes;

        var bytes = GetStagingBytes(libraryRoot, appId);
        StagingCache[key] = (now, bytes);
        return bytes;
    }

    public static long GetStagingBytes(string libraryRoot, string appId)
    {
        if (string.IsNullOrEmpty(appId)) return 0;

        var dir = Path.Combine(libraryRoot, "steamapps", "downloading", appId);
        if (!Directory.Exists(dir)) return 0;

        long total = 0;
        var n = 0;

        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var p in Directory.EnumerateFiles(dir, "*", opt))
            {
                try
                {
                    total += new FileInfo(p).Length;
                }
                catch
                {
                    // 写入中的文件偶发读取失败，跳过
                }

                if (++n >= 8000) break;
            }
        }
        catch
        {
            // 目录不可访问时按已统计部分返回
        }

        return total;
    }

    private static SteamApp? ParseManifest(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (text.Length == 0) return null;

            DateTime writeUtc;
            try
            {
                writeUtc = File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                writeUtc = DateTime.MinValue;
            }

            return new SteamApp
            {
                ManifestPath = path,
                AppId = Match(AppIdRegex, text),
                Name = Match(NameRegex, text),
                StateFlags = (int)Num(text, "StateFlags"),
                BytesToDownload = Num(text, "BytesToDownload"),
                BytesDownloaded = Num(text, "BytesDownloaded"),
                BytesToStage = Num(text, "BytesToStage"),
                BytesStaged = Num(text, "BytesStaged"),
                ManifestWriteUtc = writeUtc
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Match(Regex regex, string text)
    {
        var m = regex.Match(text);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static long Num(string text, string key)
    {
        var m = Regex.Match(text, "\"" + key + "\"\\s+\"(\\d+)\"");
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static void AddUnique(List<string> list, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
            list.Add(value);
    }
}
