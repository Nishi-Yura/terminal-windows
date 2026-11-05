using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerminalWindows.Protocol;

/// <summary>
/// Wire format (all integers little endian):
/// <code>
///   frame  := u32 length | u8 kind | payload[length - 1]
///   kind 1 := JSON control message (UTF-8)
///   kind 2 := binary attachment: i64 messageId | bytes
/// </code>
/// A JSON message may declare <c>"att": n</c>; exactly n binary frames tagged with the same id follow it.
/// Neither side ever executes data received from the other side.
/// </summary>
public enum FrameKind : byte
{
    Json = 1,
    Binary = 2,
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}

/// <summary>A JSON control message plus its binary attachments.</summary>
public sealed class Message
{
    public Message(JsonObject json, IReadOnlyList<byte[]>? attachments = null)
    {
        Json = json;
        Attachments = attachments ?? Array.Empty<byte[]>();
    }

    public JsonObject Json { get; }
    public IReadOnlyList<byte[]> Attachments { get; }

    public long Id => Json["id"]?.GetValue<long>() ?? 0;
    public string? Op => Json["op"]?.GetValue<string>();
    public string? Event => Json["event"]?.GetValue<string>();
    public bool IsResponse => Json.ContainsKey("ok");
    public bool Ok => Json["ok"]?.GetValue<bool>() ?? false;
    public string? Error => Json["error"]?.GetValue<string>();
    public JsonObject Args => Json["args"] as JsonObject ?? new JsonObject();
    public JsonNode? Result => Json["result"];
}

public sealed class FrameConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _maxJson;
    private readonly int _maxBinary;

    public FrameConnection(Stream stream, int maxJsonBytes = ProtocolConstants.MaxJsonBytes, int maxBinaryBytes = ProtocolConstants.MaxBinaryBytes)
    {
        _stream = stream;
        _maxJson = maxJsonBytes;
        _maxBinary = maxBinaryBytes;
    }

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public async Task WriteAsync(JsonObject json, IReadOnlyList<ReadOnlyMemory<byte>>? attachments = null, CancellationToken ct = default)
    {
        attachments ??= Array.Empty<ReadOnlyMemory<byte>>();
        if (attachments.Count > 0) json["att"] = attachments.Count;
        else json.Remove("att");

        byte[] body = Encoding.UTF8.GetBytes(json.ToJsonString(JsonOptions));
        if (body.Length > _maxJson) throw new ProtocolException($"JSON message too large ({body.Length} bytes)");
        long id = json["id"]?.GetValue<long>() ?? 0;

        foreach (var att in attachments)
            if (att.Length + 8 > _maxBinary) throw new ProtocolException($"Binary frame too large ({att.Length} bytes)");

        // Cancellation is honoured only while waiting for the lock. Once a message has started, it is
        // always written completely: a half-written frame would desynchronise the stream for everyone.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(FrameKind.Json, body, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
            foreach (var att in attachments)
            {
                var prefix = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(prefix, id);
                await WriteFrameAsync(FrameKind.Binary, prefix, att, CancellationToken.None).ConfigureAwait(false);
            }
            await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteFrameAsync(FrameKind kind, ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b, CancellationToken ct)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(1 + a.Length + b.Length));
        header[4] = (byte)kind;
        await _stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!a.IsEmpty) await _stream.WriteAsync(a, ct).ConfigureAwait(false);
        if (!b.IsEmpty) await _stream.WriteAsync(b, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the next message. Returns null on clean end of stream.</summary>
    public async Task<Message?> ReadAsync(CancellationToken ct = default)
    {
        var first = await ReadFrameAsync(ct).ConfigureAwait(false);
        if (first is null) return null;
        var (kind, payload) = first.Value;
        if (kind != FrameKind.Json) throw new ProtocolException("Expected JSON frame");

        JsonObject json;
        try
        {
            json = JsonNode.Parse(payload) as JsonObject ?? throw new ProtocolException("JSON message is not an object");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("Malformed JSON: " + ex.Message);
        }

        long id = json["id"]?.GetValue<long>() ?? 0;
        int att = json["att"]?.GetValue<int>() ?? 0;
        if (att < 0 || att > 4096) throw new ProtocolException("Invalid attachment count");

        var list = new List<byte[]>(att);
        for (int i = 0; i < att; i++)
        {
            var frame = await ReadFrameAsync(ct).ConfigureAwait(false) ?? throw new ProtocolException("Stream ended inside attachments");
            if (frame.Kind != FrameKind.Binary) throw new ProtocolException("Expected binary frame");
            if (frame.Payload.Length < 8) throw new ProtocolException("Binary frame too short");
            long bid = BinaryPrimitives.ReadInt64LittleEndian(frame.Payload);
            if (bid != id) throw new ProtocolException($"Attachment id mismatch ({bid} != {id})");
            list.Add(frame.Payload[8..]);
        }
        return new Message(json, list);
    }

    private async Task<(FrameKind Kind, byte[] Payload)?> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[5];
        int got = await ReadFullyAsync(header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < header.Length) throw new ProtocolException("Truncated frame header");

        uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var kind = (FrameKind)header[4];
        if (len < 1) throw new ProtocolException("Invalid frame length");
        long payloadLen = len - 1L;
        long limit = kind switch
        {
            FrameKind.Json => _maxJson,
            FrameKind.Binary => _maxBinary,
            _ => throw new ProtocolException($"Unknown frame kind {(byte)kind}"),
        };
        if (payloadLen > limit) throw new ProtocolException($"Frame exceeds size limit ({payloadLen} > {limit})");

        var payload = new byte[payloadLen];
        if (payloadLen > 0 && await ReadFullyAsync(payload, ct).ConfigureAwait(false) < payloadLen)
            throw new ProtocolException("Truncated frame payload");
        return (kind, payload);
    }

    private async Task<int> ReadFullyAsync(byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
