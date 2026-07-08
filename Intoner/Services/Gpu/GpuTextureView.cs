using SharpDX.Direct3D11;
using SharpDX.DXGI;
using DataRectangle = SharpDX.DataRectangle;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

internal sealed class GpuTextureView(Texture2D texture, ShaderResourceView view) : IDisposable
{
    private readonly Texture2D _texture = texture;

    public ShaderResourceView View { get; } = view;

    public static GpuTextureView CreateRgba(Device device, int width, int height, byte[] rgbaPixels)
    {
        unsafe
        {
            fixed (byte* pixelData = rgbaPixels)
            {
                Texture2D texture = new(
                    device,
                    CreateTextureDescription(width, height),
                    new DataRectangle((IntPtr)pixelData, width * 4));
                return new GpuTextureView(texture, new ShaderResourceView(device, texture));
            }
        }
    }

    public static GpuTextureView CreateSolidRgba(Device device, byte red, byte green, byte blue, byte alpha)
        => CreateRgba(device, 1, 1, [red, green, blue, alpha]);

    public void Dispose()
    {
        View.Dispose();
        _texture.Dispose();
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
