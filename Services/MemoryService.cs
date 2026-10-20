using System.Runtime.InteropServices;
using System.Security.Principal;
using MemOpz.Native;

namespace MemOpz.Services;

/// <summary>当前内存状态。</summary>
/// <param name="LoadPercent">物理内存占用百分比（0-100）。</param>
/// <param name="TotalBytes">物理内存总量。</param>
/// <param name="AvailableBytes">当前可用物理内存。</param>
/// <param name="SystemCacheBytes">系统缓存（待机/已修改页等）大小。</param>
public sealed record MemoryStatus(uint LoadPercent, ulong TotalBytes, ulong AvailableBytes, ulong SystemCacheBytes);

/// <summary>一次清理的结果。</summary>
/// <param name="FreedBytes">新增的可用物理内存。</param>
/// <param name="AvailableBeforeBytes">清理前可用内存。</param>
/// <param name="AvailableAfterBytes">清理后可用内存。</param>
public sealed record CleanResult(ulong FreedBytes, ulong AvailableBeforeBytes, ulong AvailableAfterBytes);

/// <summary>
/// 内存监控与清理（参考 PCL 内存清理模块：分阶段清理 + 量化释放结果）。
/// 普通权限：裁剪各进程工作集；管理员权限：额外清空系统工作集、已修改页列表、待机（Standby）列表。
/// </summary>
public sealed class MemoryService
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public MemoryStatus GetStatus()
    {
        var stat = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        if (!NativeMethods.GlobalMemoryStatusEx(ref stat))
        {
            throw new InvalidOperationException("无法获取系统内存状态。");
        }

        ulong cacheBytes = 0;
        var perf = new NativeMethods.PERFORMANCE_INFORMATION
        {
            cb = (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>()
        };
        if (NativeMethods.GetPerformanceInfo(ref perf, perf.cb))
        {
            ulong pageSize = (ulong)perf.PageSize.ToInt64();
            cacheBytes = (ulong)perf.SystemCache.ToInt64() * pageSize;
        }

        return new MemoryStatus(stat.dwMemoryLoad, stat.ullTotalPhys, stat.ullAvailPhys, cacheBytes);
    }


    public CleanResult Clean(IProgress<string>? progress = null)
    {
        ulong before = GetStatus().AvailableBytes;
        bool elevated = IsAdministrator();

        if (elevated)
        {
            progress?.Report("StageSystemWorkingSets");
            SendMemoryListCommand(NativeMethods.MemoryEmptyWorkingSets);

            progress?.Report("StageModifiedList");
            SendMemoryListCommand(NativeMethods.MemoryFlushModifiedList);

            progress?.Report("StageStandbyList");
            SendMemoryListCommand(NativeMethods.MemoryPurgeStandbyList);
            SendMemoryListCommand(NativeMethods.MemoryPurgeLowPriorityStandbyList);
        }

        progress?.Report("StageProcesses");
        TrimProcessWorkingSets();

        progress?.Report("StageManaged");
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        Thread.Sleep(500);

        ulong after = GetStatus().AvailableBytes;
        ulong freed = after > before ? after - before : 0;
        return new CleanResult(freed, before, after);
    }

    /// <summary>逐个裁剪进程工作集（无需管理员，部分受保护进程会跳过）。</summary>
    private static void TrimProcessWorkingSets()
    {
        uint[] pids = new uint[8192];
        uint bytesCopied;
        if (NativeMethods.EnumProcesses(pids, (uint)(pids.Length * sizeof(uint)), out bytesCopied) == 0)
        {
            return;
        }

        int count = (int)(bytesCopied / sizeof(uint));
        const uint access = NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION;

        for (int i = 0; i < count; i++)
        {
            uint pid = pids[i];
            if (pid == 0)
            {
                continue;
            }

            IntPtr handle = NativeMethods.OpenProcess(access, false, pid);
            if (handle != IntPtr.Zero)
            {
                NativeMethods.EmptyWorkingSet(handle);
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    private static void SendMemoryListCommand(uint command)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, (int)command);
            // 非管理员会返回 STATUS_PRIVILEGE_NOT_HELD (0xC0000022)，忽略即可
            NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation,
                buffer, sizeof(uint));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
