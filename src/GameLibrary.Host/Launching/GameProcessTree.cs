using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameLibrary.Host.Launching;

internal static class GameProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint HeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    public static void Discover(Dictionary<int, (Process Process, DateTime Started, Stopwatch Alive)> tracked, string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        var parents = new Dictionary<int, int>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
            if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do { parents[(int)entry.ProcessId] = (int)entry.ParentId; } while (Process32NextW(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(snapshot); }
        var discovered = true;
        while (discovered)
        {
            discovered = false;
            foreach (var (pid, parentId) in parents)
            {
                if (tracked.ContainsKey(pid) || !tracked.TryGetValue(parentId, out var parent)) continue;
                Process? child = null;
                try
                {
                    child = Process.GetProcessById(pid);
                    var started = child.StartTime.ToUniversalTime();
                    // An exited parent retains its start time; a reused parent PID must not adopt a new process.
                    if (started < parent.Started || (!parent.Process.HasExited && parent.Process.StartTime.ToUniversalTime() != parent.Started)) continue;
                    try
                    {
                        using var liveParent = Process.GetProcessById(parentId);
                        if (liveParent.StartTime.ToUniversalTime() != parent.Started) continue;
                    }
                    catch (ArgumentException) { } // Original parent has exited; the child can still be alive.
                    var path = child.MainModule?.FileName;
                    if (path is null || !GameLibrary.Infrastructure.Persistence.RuntimeStateStore.ContainsPath(directory, path)) continue;
                    if (!GameLibrary.Domain.Detection.LaunchSuggestionDetector.IsGameEntryName(Path.GetFileName(path))) continue;
                    _ = child.Handle; // Retain the process identity/exit handle across PID disappearance or reuse.
                    tracked[pid] = (child, started, Stopwatch.StartNew());
                    child = null;
                    discovered = true;
                }
                catch (ArgumentException) { } // Exited between snapshot and lookup.
                finally { child?.Dispose(); }
            }
        }
    }
}
