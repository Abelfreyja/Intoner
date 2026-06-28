using System.Runtime.InteropServices;

namespace Intoner.Utils
{
    internal static class PtrGuardMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryBasicInformation
        {
            public nint BaseAddress;
            public nint AllocationBase;
            public uint AllocationProtect;
            public nuint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nuint VirtualQuery(
            nint lpAddress,
            out MemoryBasicInformation lpBuffer,
            nuint dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadProcessMemory(
            nint hProcess,
            nint lpBaseAddress,
            out nint lpBuffer,
            nuint nSize,
            out nuint lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(
        nint hProcess,
        nint lpBaseAddress,
        nint lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll")]
        internal static extern void GetSystemInfo(out SystemInfo lpSystemInfo);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SystemInfo
        {
            public ushort wProcessorArchitecture;
            public ushort wReserved;
            public uint dwPageSize;
            public nint lpMinimumApplicationAddress;
            public nint lpMaximumApplicationAddress;
            public nint dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }
    }
}
