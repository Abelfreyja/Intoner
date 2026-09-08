using System.Runtime.InteropServices;

namespace Intoner.Services.Interop
{
    /// <summary> translates host paths using the current wine prefix's drive mappings </summary>
    internal static unsafe partial class WinePathInterop
    {
        private const uint UnixCodePage = 65010;

        public static bool IsUnixAbsolutePath(string path)
            => path.Length > 1 && path[0] == '/' && path[1] != '/' && path[1] != '\\';

        public static bool TryGetWindowsPath(string path, out string windowsPath)
        {
            windowsPath = string.Empty;
            if (!IsUnixAbsolutePath(path) || path.Contains('\0') || !RuntimePlatform.IsWine
             || !NativeLibrary.TryLoad("kernel32.dll", out nint library))
            {
                return false;
            }

            nint result = nint.Zero;
            try
            {
                if (!NativeLibrary.TryGetExport(library, "wine_get_dos_file_name", out nint address))
                {
                    return false;
                }

                int size = WideCharToMultiByte(UnixCodePage, 0, path, -1, null, 0, null, null);
                if (size <= 0)
                {
                    return false;
                }

                byte[] bytes = new byte[size];
                fixed (byte* destination = bytes)
                {
                    if (WideCharToMultiByte(UnixCodePage, 0, path, -1, destination, size, null, null) != size)
                    {
                        return false;
                    }

                    result = ((delegate* unmanaged[Cdecl]<byte*, nint>)address)(destination);
                }

                windowsPath = Marshal.PtrToStringUni(result) ?? string.Empty;
                return windowsPath.Length > 0;
            }
            finally
            {
                // wine returns a process heap allocation, not a COM string
                if (result != nint.Zero)
                {
                    _ = HeapFree(GetProcessHeap(), 0, result);
                }

                NativeLibrary.Free(library);
            }
        }

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int WideCharToMultiByte(
            uint codePage, uint flags, string source, int sourceLength,
            byte* destination, int destinationLength, byte* defaultCharacter, int* usedDefaultCharacter);

        [LibraryImport("kernel32.dll")]
        private static partial nint GetProcessHeap();

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool HeapFree(nint heap, uint flags, nint memory);
    }
}
