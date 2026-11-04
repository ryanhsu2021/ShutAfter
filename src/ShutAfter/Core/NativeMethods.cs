using System;
using System.Runtime.InteropServices;

namespace ShutAfter.Core;

[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

internal static class NativeMethods
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);

    [DllImport("powrprof.dll", SetLastError = true)]
    internal static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>读取指定进程累计磁盘写入字节数，失败返回 0。</summary>
    internal static ulong GetWriteBytes(int pid)
    {
        GetIo(pid, out _, out var write);
        return write;
    }

    /// <summary>读取指定进程累计磁盘读/写字节数，失败返回 false。</summary>
    internal static bool GetIo(int pid, out ulong read, out ulong write)
    {
        read = 0;
        write = 0;

        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return false;

        try
        {
            if (!GetProcessIoCounters(h, out var c)) return false;
            read = c.ReadTransferCount;
            write = c.WriteTransferCount;
            return true;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
