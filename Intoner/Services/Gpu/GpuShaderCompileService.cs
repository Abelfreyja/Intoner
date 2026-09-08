using SharpDX.D3DCompiler;
using System.Reflection;

namespace Intoner.Services.Gpu;

internal static class GpuShaderCompileService
{
    public static GpuShaderBytecode CreateComputeShader(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint = "CSMain")
        => new(() => CreateComputeShaderBytecode(resourceAnchorType, resourceName, shaderName, entryPoint));

    public static GpuShaderBytecode CreateVertexShader(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint = "VSMain")
        => new(() => CreateVertexShaderBytecode(resourceAnchorType, resourceName, shaderName, entryPoint));

    public static GpuShaderBytecode CreatePixelShader(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint = "PSMain")
        => new(() => CreatePixelShaderBytecode(resourceAnchorType, resourceName, shaderName, entryPoint));

    private static byte[] CreateComputeShaderBytecode(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint = "CSMain")
        => CreateShaderBytecode(resourceAnchorType, resourceName, shaderName, entryPoint, "cs_5_0");

    private static byte[] CreateVertexShaderBytecode(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint = "VSMain")
        => CreateShaderBytecode(resourceAnchorType, resourceName, shaderName, entryPoint, "vs_5_0");

    private static byte[] CreatePixelShaderBytecode(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint = "PSMain")
        => CreateShaderBytecode(resourceAnchorType, resourceName, shaderName, entryPoint, "ps_5_0");

    private static byte[] CreateShaderBytecode(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint,
        string profile)
    {
        Assembly assembly = resourceAnchorType.Assembly;
        string shaderSource = LoadShaderSource(assembly, resourceName);
        using var include = new EmbeddedResourceInclude(assembly, resourceName);
        using var compilation = ShaderBytecode.Compile(
            shaderSource,
            entryPoint,
            profile,
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None,
            defines: null,
            include: include,
            sourceFileName: resourceName,
            secondaryDataFlags: SecondaryDataFlags.None,
            secondaryData: null);

        if (compilation is null)
        {
            throw new InvalidOperationException($"failed to compile {shaderName}");
        }

        if (compilation.HasErrors)
        {
            throw new InvalidOperationException($"failed to compile {shaderName}: {compilation.Message}");
        }

        return compilation.Bytecode.Data;
    }

    private static string LoadShaderSource(Assembly assembly, string resourceName)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"missing embedded shader resource '{resourceName}'");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class EmbeddedResourceInclude : Include
    {
        private readonly Assembly _assembly;
        private readonly string _rootResourceName;
        private readonly Dictionary<Stream, string> _resourceNames = new(ReferenceEqualityComparer.Instance);

        public EmbeddedResourceInclude(Assembly assembly, string rootResourceName)
        {
            _assembly = assembly;
            _rootResourceName = rootResourceName;
        }

        public IDisposable? Shadow { get; set; }

        public Stream Open(IncludeType type, string fileName, Stream parentStream)
        {
            if (type != IncludeType.Local)
            {
                throw new NotSupportedException("system shader includes are not supported");
            }

            string parentResourceName = ResolveParentResourceName(parentStream);
            string resourceName = ResolveResourceName(parentResourceName, fileName);
            Stream stream = _assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"missing embedded shader include '{resourceName}'", fileName);
            _resourceNames.Add(stream, resourceName);
            return stream;
        }

        public void Close(Stream stream)
        {
            _resourceNames.Remove(stream);
            stream.Dispose();
        }

        public void Dispose()
        {
            foreach (Stream stream in _resourceNames.Keys)
            {
                stream.Dispose();
            }

            _resourceNames.Clear();
            Shadow?.Dispose();
            Shadow = null;
        }

        private string ResolveParentResourceName(Stream? parentStream)
        {
            if (parentStream is null)
            {
                return _rootResourceName;
            }

            return _resourceNames.TryGetValue(parentStream, out string? resourceName)
                ? resourceName
                : throw new InvalidOperationException("shader include parent is not owned by this compiler");
        }

        private static string ResolveResourceName(string parentResourceName, string includePath)
        {
            if (string.IsNullOrWhiteSpace(includePath) || Path.IsPathRooted(includePath))
            {
                throw new InvalidOperationException($"shader include path '{includePath}' must be relative");
            }

            int extensionSeparator = parentResourceName.LastIndexOf('.');
            int fileSeparator = parentResourceName.LastIndexOf('.', extensionSeparator - 1);
            if (fileSeparator < 0)
            {
                throw new InvalidOperationException($"shader resource '{parentResourceName}' is missing a file segment");
            }

            List<string> segments = [.. parentResourceName[..fileSeparator].Split('.')];
            foreach (string segment in includePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(segment, ".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    if (segments.Count == 0)
                    {
                        throw new InvalidOperationException($"shader include path '{includePath}' escapes the resource root");
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                segments.Add(segment);
            }

            return string.Join('.', segments);
        }
    }
}
