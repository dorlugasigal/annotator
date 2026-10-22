// Precise idle accounting: process and per-thread CPU cycles, and each thread's start routine resolved to
// module+offset. Loaded with Add-Type by live-check.ps1 for the idle-work check (WIN-SYS-07, WIN-PERF-03).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class IdleProbe
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    const uint THREAD_QUERY_INFORMATION = 0x0040;

    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] static extern bool QueryProcessCycleTime(IntPtr h, out ulong cycles);
    [DllImport("kernel32")] static extern bool QueryThreadCycleTime(IntPtr h, out ulong cycles);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern int GetThreadDescription(IntPtr h, out IntPtr description);
    [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("ntdll")] static extern int NtQueryInformationThread(IntPtr h, int infoClass, out IntPtr info, int length, IntPtr returnLength);
    [DllImport("psapi", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] modules, int cb, out int needed, uint filter);
    [DllImport("psapi", CharSet = CharSet.Unicode)] static extern uint GetModuleBaseName(IntPtr h, IntPtr module, StringBuilder name, int size);
    [DllImport("psapi")] static extern bool GetModuleInformation(IntPtr h, IntPtr module, out MODULEINFO info, int size);
    [StructLayout(LayoutKind.Sequential)] struct MODULEINFO { public IntPtr Base; public uint Size; public IntPtr Entry; }

    public static ulong ProcessCycles(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) throw new Win32Exception();
        try { ulong c; QueryProcessCycleTime(h, out c); return c; } finally { CloseHandle(h); }
    }

    public static Dictionary<int, ulong> ThreadCycles(int pid)
    {
        var result = new Dictionary<int, ulong>();
        foreach (ProcessThread t in Process.GetProcessById(pid).Threads)
        {
            IntPtr h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, t.Id);
            if (h == IntPtr.Zero) continue;
            try { ulong c; if (QueryThreadCycleTime(h, out c)) result[t.Id] = c; } finally { CloseHandle(h); }
        }
        return result;
    }

    /// <summary>Thread start routine as module+offset, and its description if one was set.</summary>
    public static string DescribeThread(int pid, int tid)
    {
        IntPtr h = OpenThread(THREAD_QUERY_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, false, tid);
        if (h == IntPtr.Zero) return "(cannot open)";
        try
        {
            IntPtr start;
            NtQueryInformationThread(h, 9 /* ThreadQuerySetWin32StartAddress */, out start, IntPtr.Size, IntPtr.Zero);
            string name = "";
            IntPtr desc;
            if (GetThreadDescription(h, out desc) >= 0 && desc != IntPtr.Zero) { name = Marshal.PtrToStringUni(desc) ?? ""; LocalFree(desc); }
            return ResolveAddress(pid, start) + (name.Length > 0 ? " \"" + name + "\"" : "");
        }
        finally { CloseHandle(h); }
    }

    static string ResolveAddress(int pid, IntPtr address)
    {
        IntPtr hp = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (hp == IntPtr.Zero) return "0x" + address.ToInt64().ToString("X");
        try
        {
            var modules = new IntPtr[1024];
            int needed;
            if (!EnumProcessModulesEx(hp, modules, modules.Length * IntPtr.Size, out needed, 0x03)) return "0x" + address.ToInt64().ToString("X");
            int count = Math.Min(modules.Length, needed / IntPtr.Size);
            long a = address.ToInt64();
            for (int i = 0; i < count; i++)
            {
                MODULEINFO info;
                if (!GetModuleInformation(hp, modules[i], out info, Marshal.SizeOf(typeof(MODULEINFO)))) continue;
                long b = info.Base.ToInt64();
                if (a >= b && a < b + info.Size)
                {
                    var name = new StringBuilder(260);
                    GetModuleBaseName(hp, modules[i], name, name.Capacity);
                    return name + "+0x" + (a - b).ToString("X");
                }
            }
            return "0x" + a.ToString("X");
        }
        finally { CloseHandle(hp); }
    }
}
