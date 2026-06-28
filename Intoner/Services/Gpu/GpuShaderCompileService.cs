using System.Reflection;
using System.Text;
using SharpDX.D3DCompiler;

namespace Intoner.Services.Gpu;

internal static class GpuShaderCompileService
{
    public static byte[] CreateComputeShaderBytecode(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint = "CSMain")
        => CreateShaderBytecode(resourceAnchorType, resourceName, shaderName, entryPoint, "cs_5_0");

    public static byte[] CreateVertexShaderBytecode(
        Type resourceAnchorType,
        string resourceName,
        string shaderName,
        string entryPoint = "VSMain")
        => CreateShaderBytecode(resourceAnchorType, resourceName, shaderName, entryPoint, "vs_5_0");

    public static byte[] CreatePixelShaderBytecode(
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
        var shaderSource = LoadShaderSource(resourceAnchorType, resourceName);
        using var compilation = ShaderBytecode.Compile(
            shaderSource,
            entryPoint,
            profile,
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None);

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

    public static string LoadShaderSource(Type resourceAnchorType, string resourceName)
        => LoadShaderSource(resourceAnchorType.Assembly, resourceName, []);

    private static string LoadShaderSource(Assembly assembly, string resourceName, HashSet<string> loadStack)
    {
        if (!loadStack.Add(resourceName))
        {
            throw new InvalidOperationException($"shader include cycle detected at '{resourceName}'");
        }

        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                throw new InvalidOperationException($"missing embedded shader resource '{resourceName}'");
            }

            using var reader = new StreamReader(stream);
            var shaderSource = reader.ReadToEnd();
            return ExpandIncludes(assembly, resourceName, shaderSource, loadStack);
        }
        finally
        {
            loadStack.Remove(resourceName);
        }
    }

    private static string ExpandIncludes(Assembly assembly, string resourceName, string shaderSource, HashSet<string> loadStack)
    {
        var builder = new StringBuilder(shaderSource.Length + 256);
        using var reader = new StringReader(shaderSource);
        while (reader.ReadLine() is string line)
        {
            if (TryResolveInclude(resourceName, line, out var includedResourceName))
            {
                builder.AppendLine(LoadShaderSource(assembly, includedResourceName, loadStack));
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static bool TryResolveInclude(string resourceName, string line, out string includedResourceName)
    {
        var trimmedLine = line.TrimStart();
        if (!trimmedLine.StartsWith("#include \"", StringComparison.Ordinal))
        {
            includedResourceName = string.Empty;
            return false;
        }

        var includeStart = "#include \"".Length;
        var includeEnd = trimmedLine.IndexOf('"', includeStart);
        if (includeEnd <= includeStart)
        {
            includedResourceName = string.Empty;
            return false;
        }

        var includePath = trimmedLine[includeStart..includeEnd]
            .Replace('/', '.')
            .Replace('\\', '.');
        includedResourceName = ResolveSiblingResourceName(resourceName, includePath);
        return true;
    }

    private static string ResolveSiblingResourceName(string resourceName, string includePath)
    {
        var extensionSeparator = resourceName.LastIndexOf('.');
        if (extensionSeparator < 0)
        {
            throw new InvalidOperationException($"shader resource '{resourceName}' is missing an extension");
        }

        var fileSeparator = resourceName.LastIndexOf('.', extensionSeparator - 1);
        if (fileSeparator < 0)
        {
            throw new InvalidOperationException($"shader resource '{resourceName}' is missing a file segment");
        }

        return resourceName[..(fileSeparator + 1)] + includePath;
    }
}
