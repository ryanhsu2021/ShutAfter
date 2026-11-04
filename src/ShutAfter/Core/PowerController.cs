using System;
using System.Diagnostics;

namespace ShutAfter.Core;

public static class PowerController
{
    /// <summary>关机指令自带的延迟秒数，留出 shutdown /a 的撤销窗口。</summary>
    public const int ShutdownDelaySeconds = 30;

    public static string Label(PowerAction action) => action switch
    {
        PowerAction.Shutdown => "关机",
        PowerAction.Sleep => "睡眠",
        PowerAction.Hibernate => "休眠",
        _ => "仅提醒"
    };

    /// <summary>执行电源动作。drill=true 时只返回描述不真正执行。</summary>
    public static bool Execute(PowerAction action, bool drill, out string message)
    {
        var label = Label(action);

        if (drill)
        {
            message = "演练模式：本应执行「" + label + "」，实际未操作";
            return true;
        }

        try
        {
            switch (action)
            {
                case PowerAction.Shutdown:
                    // 不用 /t 0：留 30 秒系统级撤销窗口，期间 shutdown /a 可中止
                    return RunChecked("shutdown.exe", "/s /t " + ShutdownDelaySeconds, "关机", out message,
                        "（" + ShutdownDelaySeconds + " 秒内可执行 shutdown /a 撤销）");

                case PowerAction.Hibernate:
                    return RunChecked("shutdown.exe", "/h", "休眠", out message);

                case PowerAction.Sleep:
                    if (!NativeMethods.SetSuspendState(false, false, false))
                    {
                        message = "睡眠调用被系统拒绝（可能未启用睡眠或需管理员权限）";
                        return false;
                    }
                    message = "已进入睡眠";
                    return true;

                default:
                    message = "仅提醒模式，系统保持运行";
                    return true;
            }
        }
        catch (Exception ex)
        {
            message = "执行失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 执行 shutdown.exe 并检查退出码。
    /// 必须检查：休眠未启用时 shutdown /h 会返回非 0，只看"进程已启动"会误报成功，
    /// 结果用户以为计算机会休眠，实际整晚开着。
    /// </summary>
    private static bool RunChecked(string file, string args, string what, out string message, string note = "")
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var p = Process.Start(psi);
            if (p == null)
            {
                message = what + "指令未能启动";
                return false;
            }

            if (!p.WaitForExit(5000))
            {
                message = "已发出" + what + "指令" + note + "（系统仍在处理）";
                return true;
            }

            if (p.ExitCode == 0)
            {
                message = "已发出" + what + "指令" + note;
                return true;
            }

            message = what + "失败，系统返回 " + p.ExitCode + "。" +
                      (file.EndsWith("shutdown.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("/h")
                          ? "休眠需要先在「控制面板 → 电源选项 → 选择电源按钮的功能」中启用。"
                          : "可能被组策略或权限限制。");
            return false;
        }
        catch (Exception ex)
        {
            message = what + "失败：" + ex.Message;
            return false;
        }
    }
}
