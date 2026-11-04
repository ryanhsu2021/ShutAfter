using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShutAfter.Core;

public enum PowerAction
{
    Shutdown,
    Sleep,
    Hibernate,
    NotifyOnly
}

public enum WatchMode
{
    Program,
    Steam,
    Folder
}

public sealed class AppConfig
{
    public WatchMode WatchMode { get; set; } = WatchMode.Program;
    public string ProgramName { get; set; } = "";
    public string SteamAppId { get; set; } = "";
    public string WatchPath { get; set; } = "";
    public int QuietSeconds { get; set; } = 300;
    public int GraceSeconds { get; set; } = 60;
    public PowerAction Action { get; set; } = PowerAction.Shutdown;
    public bool DrillMode { get; set; } = true;
    public bool WatchTempSuffix { get; set; } = true;
    public bool ProbeHandle { get; set; } = true;
    public bool Recursive { get; set; } = false;
    public bool WatchProcessIo { get; set; } = false;
    public string ProcessFilter { get; set; } = "";

    [JsonIgnore]
    public string DefaultDownloadPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShutAfter");

    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值，不阻塞启动
        }

        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Dir);

            // 先写临时文件再替换：直接覆盖时若中途崩溃，配置会变成半截 JSON，
            // 下次启动 Load 失败就会静默回退默认值，用户的设置全部丢失。
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // 保存失败不影响运行
        }
    }

    public void Normalize()
    {
        if (WatchPath == null) WatchPath = "";
        WatchPath = WatchPath.Trim();

        if (WatchMode == WatchMode.Folder)
        {
            if (WatchPath.Length == 0 || !Directory.Exists(WatchPath))
                WatchPath = Directory.Exists(DefaultDownloadPath) ? DefaultDownloadPath : "";
        }

        QuietSeconds = Math.Clamp(QuietSeconds, 10, 1800);
        GraceSeconds = Math.Clamp(GraceSeconds, 5, 600);

        if (ProcessFilter == null) ProcessFilter = "";
        ProcessFilter = ProcessFilter.Trim();

        if (SteamAppId == null) SteamAppId = "";
        SteamAppId = SteamAppId.Trim();

        if (ProgramName == null) ProgramName = "";
        ProgramName = ProgramName.Trim();
    }
}
