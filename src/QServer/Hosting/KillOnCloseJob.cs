using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QServer.Hosting;

/// <summary>
/// A Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Every process assigned to it —
/// and, on Windows 8+, every descendant those processes spawn afterwards — is terminated by the KERNEL when
/// the last handle to the job closes, i.e. when this process exits for ANY reason (window X, crash,
/// Task Manager, RDP logoff). This is the guarantee layer; cooperative shutdown stays the polite path.
/// </summary>
public sealed class KillOnCloseJob : IDisposable
{
    readonly SafeFileHandle _handle;

    public KillOnCloseJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception();

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr mem = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, mem, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, mem, (uint)len))
            {
                // Leaving the job un-limited would silently degrade the guarantee to nothing, so fail
                // loudly here and close the handle we would otherwise leak.
                var failure = new Win32Exception();
                _handle.Dispose();
                throw failure;
            }
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    /// <summary>Puts <paramref name="process"/> (and its future children) under the job. Throws Win32Exception on failure.</summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception();
    }

    /// <summary>Closing the last job handle terminates every member process (kernel-enforced).</summary>
    public void Dispose() => _handle.Dispose();

    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                     ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
