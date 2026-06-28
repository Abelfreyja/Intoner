using System.Globalization;
using System.Reflection;

namespace Intoner.Services;

internal sealed class IntonerBuildInfoService
{
    private const string WindowId = "###Intoner";

    public IntonerBuildInfoService()
    {
        DisplayVersion = CreateDisplayVersion();
        TitleBarText = IsDevelopmentBuild
            ? $"Intoner Dev Build ({DisplayVersion})"
            : $"Intoner {DisplayVersion}";
        WindowName = WindowId;
        SplashScreenVersion = DisplayVersion;
    }

    public bool IsDevelopmentBuild { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    public string DisplayVersion { get; }

    public string TitleBarText { get; }

    public string WindowName { get; }

    public string SplashScreenVersion { get; }

    private string CreateDisplayVersion()
        => IsDevelopmentBuild
            ? CreateDevelopmentVersion()
            : CreateReleaseVersion();

    private static string CreateDevelopmentVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return FormatDevelopmentVersion(informationalVersion);
        }

        return CreateReleaseVersion();
    }

    private static string CreateReleaseVersion()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    private static string FormatDevelopmentVersion(string informationalVersion)
    {
        const string DirtySuffix = "-dirty";

        bool isDirty = informationalVersion.EndsWith(DirtySuffix, StringComparison.Ordinal);
        if (isDirty)
        {
            informationalVersion = informationalVersion[..^DirtySuffix.Length];
        }

        int metadataIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        string displayVersion = metadataIndex >= 0 && metadataIndex < informationalVersion.Length - 1
            ? FormatSourceRevision(informationalVersion[..metadataIndex], informationalVersion[(metadataIndex + 1)..])
            : informationalVersion;

        return isDirty ? displayVersion + "*" : displayVersion;
    }

    private static string FormatSourceRevision(string version, string sourceRevision)
    {
        int hashIndex = sourceRevision.LastIndexOf("-g", StringComparison.Ordinal);
        int countIndex = hashIndex > 0 ? sourceRevision.LastIndexOf('-', hashIndex - 1) : -1;
        return countIndex >= 0
            && int.TryParse(sourceRevision[(countIndex + 1)..hashIndex], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? $"{version}-{sourceRevision[(countIndex + 1)..]}"
            : $"{version}+{sourceRevision}";
    }
}
