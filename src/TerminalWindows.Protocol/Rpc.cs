using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TerminalWindows.Protocol;

public sealed class RpcException : Exception
{
    public RpcException(string message) : base(message) { }
}

/// <summary>Result of a handler: a JSON result plus optional binary attachments.</summary>
public sealed record Reply(JsonNode? Result, IReadOnlyList<ReadOnlyMemory<byte>>? Attachments = null)
{
    public static Reply Of(JsonNode? result) => new(result);
}

public delegate Task<Reply> RequestHandler(Message request, RpcServerSession session, CancellationToken ct);

/// <summary>
/// Host side of the control channel. Matches responses to requests by id, enforces timeouts,
/// and forwards unsolicited events (e.g. screen frames) to <see cref="EventReceived"/>.
/// </summary>
public sealed class RpcClient : IAsyncDisposable
{
    private readonly FrameConnection _conn;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<Message>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private long _nextId;
    private volatile RpcException? _closed;

    public RpcClient(Stream stream)
    {
        _conn = new FrameConnection(stream);
    }

    public event Action<Message>? EventReceived;
    public event Action<Exception?>? Disconnected;
    public JsonObject? HelloInfo { get; private set; }

    public static async Task<RpcClient> ConnectAsync(string host, int port, string token, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
            try
            {
                await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
        var client = new RpcClient(tcp.GetStream());
        client.Start();
        try
        {
            var hello = await client.RequestAsync("hello", new JsonObject
            {
                ["token"] = token,
                ["version"] = ProtocolConstants.Version,
            }, timeout: ProtocolConstants.HandshakeTimeout, ct: ct).ConfigureAwait(false);
            client.HelloInfo = hello.Result as JsonObject;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return client;
    }

    public void Start()
    {
        _readLoop ??= Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await _conn.ReadAsync(_cts.Token).ConfigureAwait(false);
                if (msg is null) break;
                if (msg.IsResponse)
                {
                    // Unknown ids are ignored: a late response after a timeout must not be mistaken for another request.
                    if (_pending.TryRemove(msg.Id, out var tcs)) tcs.TrySetResult(msg);
                }
                else if (msg.Event is not null)
                {
                    EventReceived?.Invoke(msg);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            var failure = new RpcException(error is null ? "Connection closed" : "Connection failed: " + error.Message);
            _closed = failure;
            foreach (var kv in _pending)
            {
                if (_pending.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(failure);
            }
            Disconnected?.Invoke(error);
        }
    }

    public async Task<Message> RequestAsync(string op, JsonObject? args = null, IReadOnlyList<ReadOnlyMemory<byte>>? attachments = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        // If the read loop already ended (and drained _pending), fail now instead of waiting for the timeout.
        if (_closed is { } closed)
        {
            _pending.TryRemove(id, out _);
            throw closed;
        }
        var json = new JsonObject { ["id"] = id, ["op"] = op, ["args"] = args ?? new JsonObject() };
        try
        {
            await _conn.WriteAsync(json, attachments, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? ProtocolConstants.DefaultRequestTimeout);
            await using (cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Request '{op}' timed out"))))
            {
                var msg = await tcs.Task.ConfigureAwait(false);
                if (!msg.Ok) throw new RpcException(msg.Error ?? $"Request '{op}' failed");
                return msg;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // idempotent
        _cts.Cancel();
        await _conn.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}

/// <summary>
/// Guest side of one connection. The first message must be a valid <c>hello</c> with the shared token,
/// received within <see cref="ProtocolConstants.HandshakeTimeout"/>; otherwise the connection is dropped immediately.
/// </summary>
public sealed class RpcServerSession
{
    private readonly FrameConnection _conn;
    private readonly byte[] _expectedToken;
    private readonly Func<JsonObject> _helloInfo;
    private readonly TimeSpan _handshakeTimeout;

    public RpcServerSession(Stream stream, string expectedToken, Func<JsonObject> helloInfo, TimeSpan? handshakeTimeout = null)
    {
        _conn = new FrameConnection(stream);
        _expectedToken = Encoding.UTF8.GetBytes(expectedToken);
        _helloInfo = helloInfo;
        _handshakeTimeout = handshakeTimeout ?? ProtocolConstants.HandshakeTimeout;
    }

    public bool Authenticated { get; private set; }

    public Task SendEventAsync(string name, JsonObject data, IReadOnlyList<ReadOnlyMemory<byte>>? attachments = null, CancellationToken ct = default)
    {
        var json = new JsonObject { ["id"] = 0L, ["event"] = name, ["data"] = data };
        return _conn.WriteAsync(json, attachments, ct);
    }

    public static bool TokenMatches(byte[] expected, string? presented)
    {
        if (presented is null) return false;
        var bytes = Encoding.UTF8.GetBytes(presented);
        return bytes.Length == expected.Length && CryptographicOperations.FixedTimeEquals(bytes, expected);
    }

    /// <summary>Runs until the peer disconnects, the token fails, or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(RequestHandler handler, CancellationToken ct)
    {
        await using var _ = _conn;

        // Handshake
        Message? hello;
        using (var hs = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            hs.CancelAfter(_handshakeTimeout);
            try
            {
                hello = await _conn.ReadAsync(hs.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // handshake timeout: drop silently
            }
            catch (ProtocolException)
            {
                return;
            }
        }
        if (hello is null || hello.Op != "hello" || !TokenMatches(_expectedToken, hello.Args["token"]?.GetValue<string>()))
        {
            return; // token mismatch: disconnect immediately without a reply
        }
        Authenticated = true;
        await _conn.WriteAsync(new JsonObject { ["id"] = hello.Id, ["ok"] = true, ["result"] = _helloInfo() }, null, ct).ConfigureAwait(false);

        var inflight = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Message? req;
                try
                {
                    req = await _conn.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ProtocolException or IOException or OperationCanceledException)
                {
                    break;
                }
                if (req is null) break;
                inflight.RemoveAll(t => t.IsCompleted);
                inflight.Add(HandleOneAsync(handler, req, ct));
            }
        }
        finally
        {
            try { await Task.WhenAll(inflight).ConfigureAwait(false); } catch { }
        }
    }

    private async Task HandleOneAsync(RequestHandler handler, Message req, CancellationToken ct)
    {
        JsonObject response;
        IReadOnlyList<ReadOnlyMemory<byte>>? atts = null;
        try
        {
            if (req.Op is null) throw new RpcException("Missing op");
            var reply = await handler(req, this, ct).ConfigureAwait(false);
            response = new JsonObject { ["id"] = req.Id, ["ok"] = true, ["result"] = reply.Result };
            atts = reply.Attachments;
        }
        catch (Exception ex)
        {
            response = new JsonObject { ["id"] = req.Id, ["ok"] = false, ["error"] = ex.Message };
        }
        try
        {
            await _conn.WriteAsync(response, atts, ct).ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            // Reply too large: tell the client instead of letting it wait for its timeout.
            try
            {
                await _conn.WriteAsync(new JsonObject { ["id"] = req.Id, ["ok"] = false, ["error"] = "Reply not sent: " + ex.Message }, null, ct).ConfigureAwait(false);
            }
            catch { /* peer gone */ }
        }
        catch { /* peer gone */ }
    }
}
