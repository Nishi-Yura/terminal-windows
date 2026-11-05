using TerminalWindows.Protocol;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TerminalWindows.Guest.Capture;

/// <summary>DXGI Desktop Duplication of the primary output. Keeps the last frame because
/// AcquireNextFrame times out when nothing on screen changed.</summary>
public sealed class DxgiCapture : IScreenCapture
{
    private readonly D3DReadback _d3d = new();
    private readonly IDXGIOutputDuplication _dup;
    private BgraImage? _last;

    public DxgiCapture()
    {
        using var dxgiDevice = _d3d.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs(0, out IDXGIOutput? output).CheckError();
        using (output)
        using (var output1 = output!.QueryInterface<IDXGIOutput1>())
        {
            _dup = output1.DuplicateOutput(_d3d.Device);
        }
    }

    public string Name => "dxgi";

    public BgraImage Capture()
    {
        // Drain to the newest frame; a short timeout keeps the call snappy when the screen is static.
        for (int i = 0; i < 4; i++)
        {
            var r = _dup.AcquireNextFrame(_last is null ? 500u : 30u, out var info, out IDXGIResource? resource);
            if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) break;
            r.CheckError();
            try
            {
                if (info.LastPresentTime != 0 || _last is null)
                {
                    using var tex = resource!.QueryInterface<ID3D11Texture2D>();
                    _last = _d3d.Read(tex);
                }
            }
            finally
            {
                resource?.Dispose();
                _dup.ReleaseFrame();
            }
        }
        if (_last is null) throw new InvalidOperationException("No frame from desktop duplication");
        // Return a copy with the cursor drawn in; _last stays cursor-free for the next call.
        var frame = new BgraImage(_last.Width, _last.Height, (byte[])_last.Pixels.Clone());
        CursorOverlay.Draw(frame);
        return frame;
    }

    public void Dispose()
    {
        _dup.Dispose();
        _d3d.Dispose();
    }
}
