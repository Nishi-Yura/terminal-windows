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

    /// <summary>A JSON number whether it was parsed from text (JsonElement) or built in memory (int, double).</summary>
    private static double Num(JsonNode v) => double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Rescales every "rect" ([x, y, w, h] in screen pixels) inside a UI Automation tree to screenshot pixels.</summary>
    public void ScaleRects(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            var self = this;
            if (o["rect"] is JsonArray r && r.Count == 4)
                o["rect"] = new JsonArray(r.Select(v => (JsonNode)self.FromScreen(Num(v!))).ToArray());
            if (o["center"] is JsonArray c && c.Count == 2)
                o["center"] = new JsonArray(c.Select(v => (JsonNode)self.FromScreen(Num(v!))).ToArray());
            foreach (var kv in o.ToList())
                if (kv.Key is not ("rect" or "center")) ScaleRects(kv.Value);
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

/// <summary>
/// Element-level tools shared by the host MCP server and the in-guest desktop MCP server: find and act on
/// controls by name (UI Automation) instead of pixels, read a window's text, windows, clipboard, opening apps.
/// </summary>
public static class DesktopTools
{
    private static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    private static JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };

    private static JsonObject Query(JsonObject? extra = null)
    {
        var o = new JsonObject
        {
            ["name"] = Str("visible name / label of the control (case-insensitive; partial match unless exact=true; accelerators like '(S)' and '...' are ignored, so '保存' finds '保存(S)')"),
            ["control_type"] = Str("Button, Edit, MenuItem, ListItem, TabItem, Hyperlink, CheckBox, ComboBox, TreeItem, Document, ..."),
            ["automation_id"] = Str("AutomationId (from ui_tree / find_element)"),
            ["window"] = Str("title substring or hwnd of the window to search (default: the foreground window; 'desktop' = everything)"),
            ["exact"] = Bool("match the name exactly"),
        };
        if (extra is not null) foreach (var kv in extra.ToList()) { extra.Remove(kv.Key); o[kv.Key] = kv.Value; }
        return o;
    }

    public static readonly string[] Names =
        { "find_element", "click_element", "set_text", "wait_for", "read_text", "list_windows", "focus_window", "close_window", "clipboard_get", "clipboard_set", "open" };

    public static IEnumerable<JsonObject> Definitions() => new[]
    {
        McpResult.Tool("find_element", "Find controls by name / type / AutomationId with UI Automation. Returns name, type, rect and center (screenshot pixels), enabled, value. Visible matches come first.",
            Query(new JsonObject { ["max"] = Int("max matches (default 20)") })),
        McpResult.Tool("click_element", "Find a control and act on it in one step. Default action 'click' clicks its center with the real pointer (falls back to Invoke when it is off screen). " +
            "Other actions: double_click, right_click, invoke, toggle, select, expand, collapse, focus. More reliable than guessing pixels.",
            Query(new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("click", "double_click", "right_click", "invoke", "toggle", "select", "expand", "collapse", "focus"), ["default"] = "click" },
                ["index"] = Int("which match to use when several match (0 = best, default)"),
            })),
        McpResult.Tool("set_text", "Put text into an input control (replaces its content): clicks it, selects all and types real keystrokes, so dialogs such as Save As see the text; returns the value read back. Refuses password fields.",
            Query(new JsonObject { ["value"] = Str("the text"), ["index"] = Int("which match (default 0)") }), "value"),
        McpResult.Tool("wait_for", "Wait until a control appears (or with gone=true disappears), e.g. a dialog, a 'Done' label or a button becoming available. Polls up to timeout_ms.",
            Query(new JsonObject { ["gone"] = Bool("wait for it to disappear"), ["timeout_ms"] = Int("max wait (default 30000, max 300000)") })),
        McpResult.Tool("read_text", "Read the text of a window (labels, values, document text) without a screenshot. Cheaper and exact; use it to read results, error messages and forms.",
            new JsonObject { ["window"] = Str("title substring or hwnd (default: foreground window)"), ["max_chars"] = Int("default 20000") }),
        McpResult.Tool("list_windows", "List the top-level windows: title, process, hwnd, rect, minimized/maximized, which is in front.", new JsonObject()),
        McpResult.Tool("focus_window", "Bring a window to the front (restores it if minimized).",
            new JsonObject { ["window"] = Str("title substring or hwnd") }, "window"),
        McpResult.Tool("close_window", "Close a window like its X button (safer than alt+f4, which hits whatever is in front). " +
            "If the app asks to save, the result says so; answer that prompt. Refuses the AI agents' own terminal windows unless force=true.",
            new JsonObject { ["window"] = Str("title substring or hwnd"), ["force"] = Bool("allow closing an AI agent's terminal window (only when the person asked)") }, "window"),
        McpResult.Tool("clipboard_get", "Read the VM's clipboard text (the VM's own clipboard; the host's is never touched).", new JsonObject()),
        McpResult.Tool("clipboard_set", "Set the VM's clipboard text (then paste with key 'ctrl+v'). Good for long or multi-line text.",
            new JsonObject { ["text"] = Str("text") }, "text"),
        McpResult.Tool("open", "Open an app, file, folder or URL as the Run box would: 'notepad', 'ms-settings:display', 'https://...', 'C:\\Users\\...\\file.xlsx', '%USERPROFILE%\\Desktop'.",
            new JsonObject { ["target"] = Str("program, path or URL"), ["args"] = Str("optional arguments") }, "target"),
    };
}
