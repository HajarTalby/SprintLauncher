using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SprintLauncher.Runners;

// Ensures all child processes die if the launcher exits unexpectedly (window close,
// taskkill, crash). Windows kills every process assigned to the job when the last
// handle to that job closes — which happens automatically when the launcher exits.
internal sealed class WindowsJobObject : IDisposable
{
    private readonly nint _handle;
    private bool _disposed;

    public static WindowsJobObject? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return new WindowsJobObject(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] WindowsJobObject init failed ({ex.Message}); orphan protection unavailable.");
            return null;
        }
    }

    private WindowsJobObject()
    {
        _handle = CreateJobObject(nint.Zero, null);
        if (_handle == nint.Zero)
            throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastWin32Error()}");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        if (!SetInformationJobObject(_handle, 9, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new InvalidOperationException($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
    }

    public void AssignProcess(Process process)
    {
        if (_disposed || !OperatingSystem.IsWindows()) return;
        if (!AssignProcessToJobObject(_handle, process.SafeHandle.DangerousGetHandle()))
            Console.Error.WriteLine($"[warn] AssignProcessToJobObject failed for PID {process.Id}: {Marshal.GetLastWin32Error()}");
    }

    public void Dispose()
    {
        if (!_disposed) { CloseHandle(_handle); _disposed = true; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int infoType,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // Explicit offsets required: SIZE_T fields are 8 bytes on x64 but DWORD fields are 4.
    // Using LayoutKind.Sequential with uint would produce a 48-byte struct instead of the
    // correct 64-byte struct, causing SetInformationJobObject to fail with ERROR_BAD_LENGTH.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        [FieldOffset(0)]  public long  PerProcessUserTimeLimit;
        [FieldOffset(8)]  public long  PerJobUserTimeLimit;
        [FieldOffset(16)] public uint  LimitFlags;
        // [20-23]: padding (SIZE_T alignment)
        [FieldOffset(24)] public nuint MinimumWorkingSetSize;
        [FieldOffset(32)] public nuint MaximumWorkingSetSize;
        [FieldOffset(40)] public uint  ActiveProcessLimit;
        // [44-47]: padding (ULONG_PTR alignment)
        [FieldOffset(48)] public nuint Affinity;
        [FieldOffset(56)] public uint  PriorityClass;
        [FieldOffset(60)] public uint  SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        [FieldOffset(0)]   public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        [FieldOffset(64)]  public IO_COUNTERS IoInfo;
        [FieldOffset(112)] public nuint ProcessMemoryLimit;
        [FieldOffset(120)] public nuint JobMemoryLimit;
        [FieldOffset(128)] public nuint PeakProcessMemoryUsed;
        [FieldOffset(136)] public nuint PeakJobMemoryUsed;
    }
}
