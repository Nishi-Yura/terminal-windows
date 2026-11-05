using TerminalWindows.Protocol;

namespace TerminalWindows.Guest.Capture;

public interface IScreenCapture : IDisposable
{
    string Name { get; }
    /// <summary>Returns the latest full-resolution frame of the primary monitor (physical pixels).</summary>
    BgraImage Capture();
}

/// <summary>
/// Chooses a backend: Windows.Graphics.Capture first, DXGI Desktop Duplication second, GDI BitBlt last.
/// If the active backend throws (driver reset, resolution change, secure desktop), the next one is tried.
/// </summary>
public sealed class CaptureManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Func<IScreenCapture>[] _factories;
    private IScreenCapture? _current;
    private int _index;
    private DateTime _demotedAt = DateTime.MinValue;

    public CaptureManager(string? preferred = null)
    {
        var all = new (string Name, Func<IScreenCapture> Make)[]
        {
            ("wgc", () => new WgcCapture()),
            ("dxgi", () => new DxgiCapture()),
            ("gdi", () => new GdiCapture()),
        };
        if (preferred is not null)
            all = all.OrderBy(a => a.Name == preferred ? 0 : 1).ToArray();
        _factories = all.Select(a => a.Make).ToArray();
    }

    public string Backend
    {
        get { lock (_lock) return _current?.Name ?? "none"; }
    }

    public BgraImage Capture()
    {
        lock (_lock)
        {
            // A demotion (e.g. WGC timing out right after logon) is not permanent: retry the preferred
            // backend every 5 minutes.
            if (_index > 0 && DateTime.UtcNow - _demotedAt > TimeSpan.FromMinutes(5))
            {
                _current?.Dispose();
                _current = null;
                _index = 0;
            }
            Exception? last = null;
            for (int attempt = 0; attempt < _factories.Length * 2; attempt++)
            {
                try
                {
                    _current ??= _factories[_index]();
                    return _current.Capture();
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Write($"capture backend {_current?.Name ?? _index.ToString()} failed: {ex.Message}");
                    _current?.Dispose();
                    _current = null;
                    // Retry the same backend once (e.g. after a mode change), then move on.
                    if (attempt % 2 == 1)
                    {
                        _index = Math.Min(_index + 1, _factories.Length - 1);
                        _demotedAt = DateTime.UtcNow;
                    }
                }
            }
            throw new InvalidOperationException("All capture backends failed: " + last?.Message, last);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _current?.Dispose();
            _current = null;
        }
    }
}
