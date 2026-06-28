using Dalamud.Plugin;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Intoner.Services;

internal sealed class IntonerHostEnvironment : IHostEnvironment, IDisposable
{
    private readonly PhysicalFileProvider _fileProvider;

    private IntonerHostEnvironment(string applicationName, string contentRootPath)
    {
        Directory.CreateDirectory(contentRootPath);
        ApplicationName = applicationName;
        ContentRootPath = contentRootPath;
        _fileProvider = new PhysicalFileProvider(contentRootPath);
        ContentRootFileProvider = _fileProvider;
    }

    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; }
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }

    public static IntonerHostEnvironment FromPluginInterface(IDalamudPluginInterface pluginInterface)
        => new("Intoner", pluginInterface.ConfigDirectory.FullName);

    public void Dispose()
        => _fileProvider.Dispose();
}
