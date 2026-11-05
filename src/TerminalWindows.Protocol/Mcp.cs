using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerminalWindows.Protocol;

/// <summary>
/// Maps between the guest's physical screen pixels and the coordinate space of the screenshots an MCP
/// client sees. Screenshots are downscaled so the model never has to rescale them itself
/// (long edge &lt;= 1568 px and &lt;= ~1.15 MP); every coordinate in and out of the MCP tools uses the
/// screenshot space. Shared by the host MCP server and the in-guest desktop MCP server.
/// </summary>
public readonly record struct ScreenSpace(int ScreenWidth, int ScreenHeight)
{
    public const int MaxEdge = 1568;
    public const double MaxPixels = 1_150_000;

    public double Scale => Math.Min(1.0, Math.Min((double)MaxEdge / Math.Max(ScreenWidth, ScreenHeight),
        Math.Sqrt(MaxPixels / ((double)ScreenWidth * ScreenHeight))));

    public int ImageWidth => Math.Max(1, (int)Math.Round(ScreenWidth * Scale));
    public int ImageHeight => Math.Max(1, (int)Math.Round(ScreenHeight * Scale));

    public int ToScreenX(int x) => Math.Clamp((int)Math.Round(x / Scale), 0, ScreenWidth - 1);
    public int ToScreenY(int y) => Math.Clamp((int)Math.Round(y / Scale), 0, ScreenHeight - 1);
    public int FromScreen(double v) => (int)Math.Round(v * Scale);

    /// <summary>Rescales every "rect" ([x, y, w, h] in screen pixels) inside a UI Automation tree to screenshot pixels.</summary>
    public void ScaleRects(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            if (o["rect"] is JsonArray r && r.Count == 4)
            {
                var self = this;
                o["rect"] = new JsonArray(r.Select(v => (JsonNode)self.FromScreen(v!.GetValue<double>())).ToArray());
            }
            foreach (var kv in o.ToList())
                if (kv.Key != "rect") ScaleRects(kv.Value);
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr) ScaleRects(child);
        }
    }
}

/// <summary>Helpers for MCP tool results.</summary>
public static class McpResult
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject Text(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    public static JsonObject Json(JsonNode node) => Text(node.ToJsonString(Pretty));

    public static JsonObject Image(byte[] png, string caption) => new()
    {
        ["content"] = new JsonArray(
            new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
            new JsonObject { ["type"] = "text", ["text"] = caption }),
    };

    public static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            ["additionalProperties"] = false,
        },
    };
}

/// <summary>
/// Minimal stdio MCP server loop (JSON-RPC 2.0, one message per line). Only protocol messages go to
/// <paramref name="output"/>; the caller logs to stderr.
/// </summary>
public static class McpStdio
{
    public static async Task RunAsync(
        Stream input,
        Stream output,
        string serverName,
        string instructions,
        Func<JsonArray> listTools,
        Func<string, JsonObject, Task<JsonObject>> callTool,
        CancellationToken ct = default)
    {
        var reader = new StreamReader(input, new UTF8Encoding(false));
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        string? line;
        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? req;
            try { req = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException)
            {
                await writer.WriteLineAsync(Error(null, -32700, "Parse error").ToJsonString());
                continue;
            }
            if (req is null) continue;
            var id = req["id"]?.DeepClone();
            var method = req["method"]?.GetValue<string>();
            if (id is null) continue; // notification (e.g. notifications/initialized)

            JsonObject response;
            try
            {
                JsonObject? result = method switch
                {
                    "initialize" => new JsonObject
                    {
                        ["protocolVersion"] = req["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = serverName, ["version"] = typeof(McpStdio).Assembly.GetName().Version?.ToString(3) },
                        ["instructions"] = instructions,
                    },
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject { ["tools"] = listTools() },
                    "tools/call" => await callTool(
                        req["params"]?["name"]?.GetValue<string>() ?? throw new ArgumentException("Missing tool name"),
                        req["params"]?["arguments"] as JsonObject ?? new JsonObject()),
                    _ => null,
                };
                response = result is null
                    ? Error(id, -32601, $"Method not found: {method}")
                    : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (Exception ex)
            {
                response = Error(id, -32603, ex.Message);
            }
            await writer.WriteLineAsync(response.ToJsonString());
        }
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
