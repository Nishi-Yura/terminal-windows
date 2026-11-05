namespace TerminalWindows.Protocol;

public static class ProtocolConstants
{
    /// <summary>Fixed TCP port the guest helper listens on inside the VM.</summary>
    public const int GuestPort = 47615;

    /// <summary>Protocol version. Bump when message shapes change incompatibly.</summary>
    public const int Version = 1;

    /// <summary>Upper bound for a single JSON control message.</summary>
    public const int MaxJsonBytes = 4 * 1024 * 1024;

    /// <summary>Upper bound for a single binary frame (screen frame or file chunk).</summary>
    public const int MaxBinaryBytes = 64 * 1024 * 1024;

    /// <summary>File transfer chunk size.</summary>
    public const int FileChunkBytes = 1024 * 1024;

    /// <summary>Default per-file transfer limit (2 GB).</summary>
    public const long DefaultMaxFileBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Default per-transfer total limit (8 GB).</summary>
    public const long DefaultMaxTotalBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>The handshake must complete within this time or the connection is dropped.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Default request timeout.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The address VirtualBox NAT port-forwarded connections are expected to come from inside the guest.</summary>
    public const string NatGatewayAddress = "10.0.2.2";

    /// <summary>
    /// What the guest accepts connections from: VirtualBox's private NAT subnet. Nothing outside the VM
    /// can reach this subnet except through the 127.0.0.1-only port forward, and the token is still required.
    /// (Wider than the single gateway address so an unexpected NAT source address cannot lock us out.)
    /// </summary>
    public const string NatSubnet = "10.0.2.0/24";

    /// <summary>True if <paramref name="address"/> is inside <paramref name="cidrOrAddress"/> ("10.0.2.0/24" or "10.0.2.2").</summary>
    public static bool AddressAllowed(System.Net.IPAddress address, string cidrOrAddress)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var parts = cidrOrAddress.Split('/');
        var network = System.Net.IPAddress.Parse(parts[0]);
        int prefix = parts.Length > 1 ? int.Parse(parts[1]) : (network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (a.Length != n.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            int bits = Math.Clamp(prefix - i * 8, 0, 8);
            int mask = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }

    public const string ScheduledTaskName = "terminal-windows-guest";
}
