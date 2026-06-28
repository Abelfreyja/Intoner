using System.Runtime.InteropServices;
using System.Text;
using static Intoner.Utils.PtrGuardMemory;

namespace Intoner.Utils
{
    public static partial class PtrGuard
    {
        private static readonly nuint _hardMinWindows =
            (nuint)(IntPtr.Size == 8 ? 0x0000000100000000UL : 0x0000000000010000UL);
        private static readonly nuint _hardMaxWindows =
            (nuint)(IntPtr.Size == 8 ? 0x00007FFFFFFFFFFFUL : 0x7FFFFFFFUL);
        private const nuint _alignmentPtr = 0x7;

        private static readonly (nuint min, nuint max) _sysRange = GetSysRange();
        private static volatile bool _lowAddressDetected;

        private static (nuint min, nuint max) GetSysRange()
        {
            GetSystemInfo(out var si);
            return ((nuint)si.lpMinimumApplicationAddress, (nuint)si.lpMaximumApplicationAddress);
        }

        /// <summary>
        /// Out of the box, Windows uses high-entropy, bottom-up ASLR, which means addresses
        /// *should* land above our min 4GiB. But some users have ASLR disabled. This method
        /// is called on client login, detects "low" addresses, and makes the minimum
        /// fall back to the system minimum (likely 0x10000) in that case. This is much less
        /// effective at finding bad pointers, obviously, but it does keep the plugin working.
        /// </summary>
        public static void CalibrateFromPlayerAddress(nint playerAddress, bool isWine)
        {
            if (isWine || _lowAddressDetected)
                return;

            if (playerAddress != 0 && (nuint)playerAddress < _hardMinWindows)
            {
                _lowAddressDetected = true;
            }
        }

        private static nuint GetMinAppAddr(bool isWine) =>
            isWine || _lowAddressDetected ? _sysRange.min : _hardMinWindows;
        private static nuint GetMaxAppAddr(bool isWine) =>
            isWine || _lowAddressDetected ? _sysRange.max : _hardMaxWindows;

        public static Dictionary<string, object> GetDiagnosticInfo(bool isWine)
        {
            return new Dictionary<string, object>
            {
                ["effectiveMinAddress"] = $"0x{GetMinAppAddr(isWine):X}",
                ["effectiveMaxAddress"] = $"0x{GetMaxAppAddr(isWine):X}",
                ["lowAddressDetected"] = _lowAddressDetected,
            };
        }

        public static bool LooksLikePtr(nint p, bool isWine = false)
        {
            if (p == 0) return false;
            nuint u = (nuint)p;

            if (u < GetMinAppAddr(isWine)) return false;
            if (u > GetMaxAppAddr(isWine)) return false;
            if ((u & _alignmentPtr) != 0) return false;
            if ((uint)u == 0x12345679u) return false;

            return true;
        }

        public static bool TryReadIntPtr(nint addr, bool isWine, out nint value)
        {
            value = 0;

            if (!LooksLikePtr(addr, isWine))
                return false;

            return ReadProcessMemory(GetCurrentProcess(), addr, out value, (nuint)IntPtr.Size, out nuint bytesRead)
                   && bytesRead == (nuint)IntPtr.Size;
        }

        public static bool IsReadable(nint addr, nuint size)
            => TryGetReadableRegionSize(addr, size, out nuint readableSize)
            && readableSize >= size;

        public static bool TryGetReadableRegionSize(nint addr, nuint maxSize, out nuint readableSize)
        {
            readableSize = 0;
            if (addr == 0 || maxSize == 0)
                return false;

            if (VirtualQuery(addr, out var mbi, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                return false;

            const uint Commit = 0x1000;
            const uint NoAccess = 0x01;
            const uint PageGuard = 0x100;

            if (mbi.State != Commit) return false;
            if ((mbi.Protect & PageGuard) != 0) return false;
            if ((mbi.Protect & NoAccess) != 0) return false;

            nuint start = (nuint)addr;
            nuint regionBase = (nuint)mbi.BaseAddress;
            if (mbi.RegionSize == 0 || start < regionBase)
                return false;

            nuint offset = start - regionBase;
            if (offset >= mbi.RegionSize)
                return false;

            readableSize = mbi.RegionSize - offset;
            if (readableSize > maxSize)
                readableSize = maxSize;

            return readableSize > 0;
        }

        public static bool TryReadBytes(nint addr, bool isWine, Span<byte> destination)
        {
            if (destination.Length == 0) return false;
            if (!LooksLikePtr(addr, isWine)) return false;

            return TryReadProcessBytes(addr, destination);
        }

        public static bool TryReadUnalignedBytes(nint addr, Span<byte> destination)
        {
            if (addr == 0 || destination.Length == 0)
                return false;

            return TryReadProcessBytes(addr, destination);
        }

        public static bool TryReadUnaligned<T>(nint addr, out T value) where T : unmanaged
        {
            value = default;
            Span<byte> buf = stackalloc byte[Marshal.SizeOf<T>()];
            if (!TryReadUnalignedBytes(addr, buf))
                return false;

            value = MemoryMarshal.Read<T>(buf);
            return true;
        }

        private static unsafe bool TryReadProcessBytes(nint addr, Span<byte> destination)
        {
            fixed (byte* pDest = destination)
            {
                return ReadProcessMemory(
                           GetCurrentProcess(),
                           addr,
                           (nint)pDest,
                           (nuint)destination.Length,
                           out nuint bytesRead)
                       && bytesRead == (nuint)destination.Length;
            }
        }

        public static bool TryRead<T>(nint addr, bool isWine, out T value) where T : unmanaged
        {
            value = default;
            if (!LooksLikePtr(addr, isWine)) return false;

            return TryReadUnaligned(addr, out value);
        }

        public static string Utf8Z(ReadOnlySpan<byte> bytes)
        {
            int len = bytes.IndexOf((byte)0);
            if (len < 0) len = bytes.Length;
            return len == 0 ? string.Empty : Encoding.UTF8.GetString(bytes[..len]);
        }
    }
}
