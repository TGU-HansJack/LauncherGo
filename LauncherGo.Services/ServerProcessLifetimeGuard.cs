using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LauncherGo.Services;

internal sealed class ServerProcessLifetimeGuard : IDisposable
{
    private readonly SafeFileHandle? _jobHandle;
    private bool _normalShutdownCompleted;

    public ServerProcessLifetimeGuard(bool includeCurrentProcess = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the ServerHost job object.");
        }

        var limits = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose
            }
        };
        var size = Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!NativeMethods.SetInformationJobObject(
                    _jobHandle,
                    NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                    buffer,
                    (uint)size))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to enable kill-on-close for the ServerHost job object.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (includeCurrentProcess)
        {
            using var currentProcess = Process.GetCurrentProcess();
            Add(currentProcess);
        }
    }

    public void Add(Process process)
    {
        if (_jobHandle is null)
        {
            return;
        }

        if (NativeMethods.IsProcessInJob(process.SafeHandle, _jobHandle, out var alreadyAssigned) &&
            alreadyAssigned)
        {
            return;
        }

        if (!NativeMethods.AssignProcessToJobObject(_jobHandle, process.SafeHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to bind server process {process.Id} to the ServerHost job object.");
        }
    }

    public void Dispose()
    {
        if (_normalShutdownCompleted &&
            _jobHandle is not null &&
            !_jobHandle.IsInvalid &&
            !_jobHandle.IsClosed)
        {
            TrySetKillOnClose(enabled: false);
        }

        _jobHandle?.Dispose();
    }

    internal void CompleteNormalShutdown()
    {
        _normalShutdownCompleted = true;
    }

    internal void TerminateForTest()
    {
        if (_jobHandle is null ||
            !NativeMethods.TerminateJobObject(_jobHandle, unchecked((uint)-1)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to terminate the ServerHost job object.");
        }
    }

    private void TrySetKillOnClose(bool enabled)
    {
        if (_jobHandle is null)
            return;

        var limits = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = enabled ? NativeMethods.JobObjectLimitKillOnJobClose : 0
            }
        };
        var size = Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            NativeMethods.SetInformationJobObject(
                _jobHandle,
                NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                buffer,
                (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class NativeMethods
    {
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

        internal enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize;
            internal UIntPtr MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit;
            internal UIntPtr JobMemoryLimit;
            internal UIntPtr PeakProcessMemoryUsed;
            internal UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle job,
            JobObjectInfoType infoType,
            IntPtr jobObjectInfo,
            uint jobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(
            SafeProcessHandle process,
            SafeFileHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
