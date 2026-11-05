using System.Runtime.InteropServices;
using TerminalWindows.Protocol;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace TerminalWindows.Guest.Capture;

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

/// <summary>
/// Windows.Graphics.Capture of the primary monitor. Frames arrive only when the screen changes;
/// each arrival is copied to a GPU texture we own, and <see cref="Capture"/> reads that texture back.
/// </summary>
public sealed class WgcCapture : IScreenCapture
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly object _gpuLock = new();
    private readonly D3DReadback _d3d = new();
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private ID3D11Texture2D? _latest;
    private bool _disposed;
    private long _frameSeq;          // incremented for every frame WGC delivers
    private long _cachedSeq = -1;
    private BgraImage? _cached;      // CPU copy of the frame with sequence _cachedSeq
    private Windows.Graphics.SizeInt32 _poolSize;

    public WgcCapture()
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows.Graphics.Capture is not supported");

        using (var dxgiDevice = _d3d.Device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var raw));
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(raw);
            Marshal.Release(raw);
        }

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var hmon = Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
        var iid = GraphicsCaptureItemIid;
        var itemPtr = interop.CreateForMonitor(hmon, ref iid);
        _item = GraphicsCaptureItem.FromAbi(itemPtr);
        Marshal.Release(itemPtr);

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = true; } catch { }
        try { _session.IsBorderRequired = false; } catch { /* older builds always draw the yellow border */ }
        _session.StartCapture();
    }

    public string Name => "wgc";

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var texIid = typeof(ID3D11Texture2D).GUID;
            using var tex = new ID3D11Texture2D(access.GetInterface(ref texIid));
            lock (_gpuLock)
            {
                if (_disposed) return;
                var desc = tex.Description;
                var latestDesc = _latest?.Description;
                if (_latest is null || latestDesc!.Value.Width != desc.Width || latestDesc.Value.Height != desc.Height)
                {
                    _latest?.Dispose();
                    desc.Usage = ResourceUsage.Default;
                    desc.BindFlags = BindFlags.None;
                    desc.CPUAccessFlags = CpuAccessFlags.None;
                    desc.MiscFlags = ResourceOptionFlags.None;
                    _latest = _d3d.Device.CreateTexture2D(desc);
                }
                _d3d.Context.CopyResource(_latest, tex);
            }
            if (_disposed) return;
            Interlocked.Increment(ref _frameSeq);
            _firstFrame.Set();

            if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
            {
                _poolSize = frame.ContentSize;
                sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            }
        }
        catch (Exception ex)
        {
            Log.Write("wgc frame error: " + ex.Message);
        }
    }

    public BgraImage Capture()
    {
        if (!_firstFrame.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("WGC delivered no frame");
        lock (_gpuLock)
        {
            if (_disposed || _latest is null) throw new ObjectDisposedException(nameof(WgcCapture));
            // WGC only delivers frames when the screen changes: if nothing new arrived, reuse the last
            // CPU copy instead of reading the whole texture back from the GPU again.
            long seq = Interlocked.Read(ref _frameSeq);
            if (_cached is not null && seq == _cachedSeq) return _cached;
            _cached = _d3d.Read(_latest);
            _cachedSeq = seq;
            return _cached;
        }
    }

    public void Dispose()
    {
        try { _pool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        // Under the lock, so an in-flight FrameArrived callback cannot touch disposed D3D objects.
        lock (_gpuLock)
        {
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
            _d3d.Dispose();
        }
        _firstFrame.Dispose();
    }
}
