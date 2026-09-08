using Dalamud.Utility;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Intoner.Services.Interop
{
    /// <summary> provides runtime and host platform information </summary>
    internal static class RuntimePlatform
    {
        /// <summary> gets the host operating system reported by Dalamud </summary>
        public static OSPlatform HostPlatform => Util.GetHostPlatform();

        /// <summary> whether the current runtime reports Windows </summary>
        [SupportedOSPlatformGuard("windows")]
        public static bool IsWindowsRuntime => OperatingSystem.IsWindows();

        /// <summary> whether the current process is running under Wine </summary>
        public static bool IsWine => IsWindowsRuntime && Util.IsWine();
    }
}
