using System.Diagnostics;
using System.Security.Cryptography;
using TerminalWindows.Protocol;

namespace TerminalWindows.Guest;

/// <summary>
/// Receives a new helper build from the host. Because the helper runs elevated, the new binary is
/// only installed if its SHA-256 equals the value the host announced up front; anything else is discarded.
/// </summary>
public sealed class SelfUpdater
{
    private readonly object _gate = new();
    private string? _expectedSha;
    private long _expectedSize;
    private string StagingPath => Path.Combine(GuestPaths.DataDir, "update", "TerminalWindowsGuest.new.exe");
    public static string PendingMarker => Path.Combine(GuestPaths.DataDir, "update", "pending");

    /// <summary>True while an update swap is in progress (marker younger than 2 minutes).</summary>
    public static bool UpdateInProgress() =>
        File.Exists(PendingMarker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(PendingMarker) < TimeSpan.FromMinutes(2);

    public void Begin(long size, string sha256)
    {
        if (size <= 0 || size > 512L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(size));
        if (sha256.Length != 64) throw new ArgumentException("sha256 must be 64 hex chars");
        lock (_gate)
        {
            _expectedSha = sha256.ToLowerInvariant();
            _expectedSize = size;
            Directory.CreateDirectory(Path.GetDirectoryName(StagingPath)!);
            File.WriteAllBytes(StagingPath, Array.Empty<byte>());
        }
    }

    public void Chunk(long offset, byte[] data)
    {
        lock (_gate)
        {
            if (_expectedSha is null) throw new InvalidOperationException("update_begin was not called");
            using var fs = new FileStream(StagingPath, FileMode.Open, FileAccess.Write);
            if (fs.Length != offset || offset + data.Length > _expectedSize) throw new IOException("Out-of-order update chunk");
            fs.Position = offset;
            fs.Write(data);
        }
    }

    /// <summary>Verifies the staged binary and schedules the swap + restart. Returns false on mismatch.</summary>
    public bool Commit()
    {
        lock (_gate)
        {
            if (_expectedSha is null) throw new InvalidOperationException("update_begin was not called");
            var actual = SafePath.Sha256File(StagingPath);
            var size = new FileInfo(StagingPath).Length;
            if (size != _expectedSize || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual), Convert.FromHexString(_expectedSha)))
            {
                File.Delete(StagingPath);
                _expectedSha = null;
                Log.Write("update rejected: SHA-256 mismatch");
                return false;
            }

            var target = GuestPaths.InstalledExe;
            // While this marker exists, 'run' exits immediately, so the 5-minute trigger cannot relaunch
            // the old exe (and lock it) during the swap.
            File.WriteAllText(PendingMarker, DateTime.UtcNow.ToString("o"));
            // The swap runs after this process exits; attrib -r because a CD-copied exe may be read-only.
            // Other instances (e.g. 'mcp' servers started by an agent inside the VM) lock the exe: stop them first.
            var log = Path.Combine(GuestPaths.LogDir, "update.log");
            var script = $"/c ping -n 3 127.0.0.1 >nul & taskkill /f /im TerminalWindowsGuest.exe >nul 2>&1 & attrib -r \"{target}\" & " +
                         $"(move /y \"{StagingPath}\" \"{target}\" && echo %date% %time% swapped>>\"{log}\" || echo %date% %time% MOVE FAILED>>\"{log}\") & " +
                         $"del \"{PendingMarker}\" & schtasks /run /tn \"{ProtocolConstants.ScheduledTaskName}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false });
            Log.Write("update accepted; restarting");
            _ = Task.Delay(500).ContinueWith(_ => Environment.Exit(0));
            return true;
        }
    }
}
