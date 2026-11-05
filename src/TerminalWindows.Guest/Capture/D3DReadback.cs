using System.Runtime.InteropServices;
using TerminalWindows.Protocol;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TerminalWindows.Guest.Capture;

/// <summary>Owns a D3D11 device and copies GPU textures into CPU-readable BGRA buffers.</summary>
internal sealed class D3DReadback : IDisposable
{
    private ID3D11Texture2D? _staging;
    private int _w, _h;

    public D3DReadback()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device device, out ID3D11DeviceContext context);
        if (result.Failure || device is null)
        {
            // VirtualBox's VBoxSVGA may not expose a hardware D3D11 adapter; WARP still works with WGC.
            result = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context);
            result.CheckError();
        }
        Device = device!;
        Context = context!;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    public BgraImage Read(ID3D11Texture2D source)
    {
        var desc = source.Description;
        int w = (int)desc.Width, h = (int)desc.Height;
        if (_staging is null || _w != w || _h != h)
        {
            _staging?.Dispose();
            _staging = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
            _w = w;
            _h = h;
        }
        Context.CopyResource(_staging, source);
        var map = Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[w * h * 4];
            int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
                Marshal.Copy(map.DataPointer + (nint)(y * (long)map.RowPitch), pixels, y * rowBytes, rowBytes);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new BgraImage(w, h, pixels);
        }
        finally
        {
            Context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        _staging?.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
