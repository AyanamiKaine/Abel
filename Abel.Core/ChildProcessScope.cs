using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;

namespace Abel.Core;

/// <summary>
/// Tracks spawned child processes in a Windows Job object configured with
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so descendants are terminated when Abel exits.
/// No-op on non-Windows platforms.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "This type has deterministic disposal in AbelRunner and process-exit handle cleanup as fallback.")]
internal sealed class ChildProcessScope : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private static readonly uint ExtendedLimitInfoSize = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
    private const int SigTerm = 15;
    private const int SigKill = 9;

    private nint _jobHandle;
    private bool _disposed;
    private readonly HashSet<int> _unixProcessGroups = [];
    private readonly Lock _unixGroupsGate = new();

    public ChildProcessScope()
    {
        if (!OperatingSystem.IsWindows())
            return;

        _jobHandle = CreateJobObject(nint.Zero, null);
        if (_jobHandle == nint.Zero)
            return;

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        if (SetInformationJobObject(
                _jobHandle,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                ref limits,
                ExtendedLimitInfoSize))
            return;

        if (_jobHandle == nint.Zero)
            return;

        CloseHandle(_jobHandle);
        _jobHandle = nint.Zero;
    }

    public void TryAttach(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (process.HasExited)
            return;

        if (OperatingSystem.IsWindows())
        {
            TryAttachWindows(process);
            return;
        }

        TryAttachUnix(process);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (OperatingSystem.IsWindows())
        {
            DisposeWindows();
            return;
        }

        DisposeUnix();
    }

    private void TryAttachWindows(Process process)
    {
        if (_jobHandle == nint.Zero)
            return;

        // If assignment fails because the process is already part of a non-nestable job,
        // we cannot safely recover here. Best-effort fallback is to continue.
        AssignProcessToJobObject(_jobHandle, process.Handle);
    }

    private void DisposeWindows()
    {
        if (_jobHandle == nint.Zero)
            return;

        CloseHandle(_jobHandle);
        _jobHandle = nint.Zero;
    }

    private void TryAttachUnix(Process process)
    {
        var pid = process.Id;

        // Move the spawned child into its own process group so teardown can target only this subtree.
        if (SetProcessGroup(pid, pid) != 0)
            return;

        var processGroupId = GetProcessGroupId(pid);
        if (processGroupId != pid)
            return;

        lock (_unixGroupsGate)
            _unixProcessGroups.Add(processGroupId);
    }

    private void DisposeUnix()
    {
        int[] processGroups;
        lock (_unixGroupsGate)
            processGroups = [.. _unixProcessGroups];

        foreach (var processGroupId in processGroups)
            SendSignalToProcessGroup(processGroupId, SigTerm);

        Thread.Sleep(250);

        foreach (var processGroupId in processGroups)
            SendSignalToProcessGroup(processGroupId, SigKill);
    }

    private static void SendSignalToProcessGroup(int processGroupId, int signal)
    {
        if (processGroupId <= 0)
            return;

        // Negative PID targets the process group.
        _ = SendSignal(-processGroupId, signal);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint hJob,
        JOBOBJECTINFOCLASS jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    private static extern int SetProcessGroup(int pid, int pgid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroupId(int pid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int sig);

    private enum JOBOBJECTINFOCLASS
    {
        JobObjectExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
