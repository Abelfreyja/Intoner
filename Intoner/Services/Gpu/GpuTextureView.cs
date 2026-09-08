using SharpDX.Direct3D11;
using SharpDX.DXGI;
using DataRectangle = SharpDX.DataRectangle;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

internal sealed class GpuTextureView(Texture2D texture, ShaderResourceView view) : IDisposable
{
    public Texture2D Texture { get; } = texture;
    public ShaderResourceView View { get; } = view;

    public static GpuTextureView Create(Device device, Texture2DDescription description)
    {
        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(device, description);
            var view = new ShaderResourceView(device, texture);
            var result = new GpuTextureView(texture, view);
            texture = null;
            return result;
        }
        finally
        {
            texture?.Dispose();
        }
    }

    public static GpuTextureView CreateRgba(Device device, int width, int height, byte[] rgbaPixels)
    {
        unsafe
        {
            fixed (byte* pixelData = rgbaPixels)
            {
                Texture2D? texture = null;
                try
                {
                    texture = new Texture2D(
                        device,
                        CreateTextureDescription(width, height),
                        new DataRectangle((IntPtr)pixelData, width * 4));
                    ShaderResourceView view = new(device, texture);
                    GpuTextureView result = new(texture, view);
                    texture = null;
                    return result;
                }
                finally
                {
                    texture?.Dispose();
                }
            }
        }
    }

    public static GpuTextureView CreateSolidRgba(Device device, byte red, byte green, byte blue, byte alpha)
        => CreateRgba(device, 1, 1, [red, green, blue, alpha]);

    public void Dispose()
    {
        try
        {
            View.Dispose();
        }
        finally
        {
            Texture.Dispose();
        }
    }

    private static Texture2DDescription CreateTextureDescription(int width, int height)
        => new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        };
}
