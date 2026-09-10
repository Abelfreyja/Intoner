using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;

namespace Intoner.Services.Gpu;

[Flags]
public enum GpuJobOptions
{
    None = 0,
    TextureProcessing = 1 << 0,
    ModelProcessing = 1 << 1,
}

internal sealed class GpuJobContext : IDisposable
{
    private readonly IDisposable _operationScope;
    private bool _disposed;

    internal GpuJobContext(
        GpuProcessingService gpuProcessingService,
        ILogger logger,
        CancellationToken cancellationToken,
        GpuJobOptions options,
        Device device,
        DeviceContext context,
        GpuResourcePoolService resourcePool,
        IDisposable operationScope)
    {
        GpuProcessingService = gpuProcessingService;
        Logger = logger;
        CancellationToken = cancellationToken;
        Options = options;
        Device = device;
        Context = context;
        ResourcePool = resourcePool;
        _operationScope = operationScope;
    }

    public GpuProcessingService GpuProcessingService { get; }
    public ILogger Logger { get; }
    public CancellationToken CancellationToken { get; }
    public GpuJobOptions Options { get; }
    public Device Device { get; }
    public DeviceContext Context { get; }
    public GpuResourcePoolService ResourcePool { get; }

    public void ThrowIfCancellationRequested()
        => CancellationToken.ThrowIfCancellationRequested();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Context.Dispose();
        }
        finally
        {
            try
            {
                ResourcePool.Dispose();
            }
            finally
            {
                try
                {
                    Device.Dispose();
                }
                finally
                {
                    _operationScope.Dispose();
                }
            }
        }
    }
}

internal static class GpuJobContextFactory
{
    public static bool TryCreate(
        GpuProcessingService gpuProcessingService,
        ILogger logger,
        CancellationToken cancellationToken,
        GpuJobOptions options,
        out GpuJobContext? jobContext)
    {
        jobContext = null;
        if (cancellationToken.IsCancellationRequested || gpuProcessingService.IsDisposed)
        {
            return false;
        }

        IDisposable? operationScope = null;
        GpuResourcePoolService? resourcePool = null;
        Device? device = null;
        DeviceContext? context = null;
        nint d3d11Device = nint.Zero;
        try
        {
            if (!gpuProcessingService.TryEnterOperationScope(cancellationToken, options, out operationScope) || operationScope is null)
            {
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (!gpuProcessingService.TryCreateOperationDeviceClone(out d3d11Device) || d3d11Device == nint.Zero)
            {
                operationScope.Dispose();
                operationScope = null;
                return false;
            }

            device = new Device(d3d11Device);
            d3d11Device = nint.Zero;
            context = device.ImmediateContext;
            resourcePool = new GpuResourcePoolService();

            jobContext = new GpuJobContext(
                gpuProcessingService,
                logger,
                cancellationToken,
                options,
                device,
                context,
                resourcePool,
                operationScope);

            operationScope = null;
            resourcePool = null;
            context = null;
            device = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resourcePool?.Dispose();
            context?.Dispose();
            device?.Dispose();
            operationScope?.Dispose();

            throw;
        }
        catch (ObjectDisposedException)
        {
            resourcePool?.Dispose();
            context?.Dispose();
            device?.Dispose();
            operationScope?.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            resourcePool?.Dispose();
            context?.Dispose();
            device?.Dispose();
            operationScope?.Dispose();

            gpuProcessingService.NotifyOperationFailure(ex);
            logger.LogDebug(ex, "GPU job context initialization failed.");
            return false;
        }
        finally
        {
            if (d3d11Device != nint.Zero)
            {
                D3D11ComReference.Release(ref d3d11Device);
            }
        }
    }

    public static async Task<GpuJobContext?> TryCreateAsync(
        GpuProcessingService? gpuProcessingService,
        ILogger logger,
        CancellationToken cancellationToken,
        GpuJobOptions options)
    {
        if (gpuProcessingService is null || cancellationToken.IsCancellationRequested || gpuProcessingService.IsDisposed)
        {
            return null;
        }

        IDisposable? operationScope = null;
        GpuResourcePoolService? resourcePool = null;
        Device? device = null;
        DeviceContext? context = null;
        nint d3d11Device = nint.Zero;
        try
        {
            operationScope = await gpuProcessingService.EnterOperationScopeAsync(cancellationToken, options).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!gpuProcessingService.TryCreateOperationDeviceClone(out d3d11Device) || d3d11Device == nint.Zero)
            {
                operationScope.Dispose();
                operationScope = null;
                return null;
            }

            device = new Device(d3d11Device);
            d3d11Device = nint.Zero;
            context = device.ImmediateContext;
            resourcePool = new GpuResourcePoolService();

            var jobContext = new GpuJobContext(
                gpuProcessingService,
                logger,
                cancellationToken,
                options,
                device,
                context,
                resourcePool,
                operationScope);

            operationScope = null;
            resourcePool = null;
            context = null;
            device = null;
            return jobContext;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resourcePool?.Dispose();
            context?.Dispose();
            device?.Dispose();
            operationScope?.Dispose();

            throw;
        }
        catch (ObjectDisposedException)
        {
            resourcePool?.Dispose();
            context?.Dispose();
            device?.Dispose();
            operationScope?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            resourcePool?.Dispose();
            context?.Dispose();
            device?.Dispose();
            operationScope?.Dispose();
            logger.LogWarning(ex, "Failed to create GPU job context");
            return null;
        }
        finally
        {
            D3D11ComReference.Release(ref d3d11Device);
        }
    }
}
