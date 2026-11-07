using System.Text.Json.Nodes;
using TerminalWindows.Protocol;

namespace TerminalWindows.Cli.Mcp;

public static class McpTools
{
    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
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

    private static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };

    public static JsonArray Definitions()
    {
        var list = Base();
        foreach (var t in DesktopTools.Definitions()) list.Add((JsonNode)t);
        list.Add((JsonNode)Tool("run_powershell", "Run a PowerShell script inside the VM (as the VM's user, elevated) and return stdout, stderr and the exit code. " +
            "Best for files, settings, installs and checks; use the GUI tools for apps.", new JsonObject
        {
            ["script"] = Str("PowerShell script"),
            ["timeout_s"] = Int("timeout in seconds (default 120, max 1800)"),
        }, "script"));
        return list;
    }

    private static JsonArray Base() => new(
        Tool("screenshot", "Capture the VM screen as PNG. Returns the image and its size; all other tools use this image's pixel coordinates.", new JsonObject()),
        Tool("click", "Click at (x, y) in screenshot pixels.", new JsonObject
        {
            ["x"] = Int("X in screenshot pixels"),
            ["y"] = Int("Y in screenshot pixels"),
            ["button"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("left", "right", "middle"), ["default"] = "left" },
            ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3, ["default"] = 1, ["description"] = "2 = double click, 3 = triple click" },
        }, "x", "y"),
        Tool("drag", "Press at the start point, move, release at the end point (screenshot pixels).", new JsonObject
        {
            ["start_x"] = Int("start X"), ["start_y"] = Int("start Y"), ["end_x"] = Int("end X"), ["end_y"] = Int("end Y"),
            ["button"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("left", "right", "middle"), ["default"] = "left" },
        }, "start_x", "start_y", "end_x", "end_y"),
        Tool("scroll", "Scroll the mouse wheel. dy > 0 scrolls down, dx > 0 scrolls right (in wheel notches). Optional x/y moves the pointer first.", new JsonObject
        {
            ["dx"] = Int("horizontal notches"), ["dy"] = Int("vertical notches"), ["x"] = Int("pointer X"), ["y"] = Int("pointer Y"),
        }),
        Tool("type", "Type text into the focused control. Japanese and other Unicode text is sent directly (no IME needed). \\n presses Enter.", new JsonObject
        {
            ["text"] = Str("text to type"),
        }, "text"),
        Tool("key", "Press key chords, e.g. 'ctrl+c', 'win+r', 'alt+f4', 'enter', 'ctrl+shift+esc'. Space-separate several chords.", new JsonObject
        {
            ["keys"] = Str("chord(s)"),
        }, "keys"),
        Tool("ui_tree", "UI Automation tree of the foreground window (or the window whose title contains `window`, or 'desktop' for top-level windows): name, controlType, rect (screenshot pixels), enabled.", new JsonObject
        {
            ["window"] = Str("title substring, window handle, or 'desktop'"),
            ["depth"] = Int("max depth (default 6)"),
            ["max_nodes"] = Int("max nodes (default 1500)"),
        }),
        Tool("wait", "Wait for `ms` milliseconds, or with until_change=true wait up to `ms` until the screen changes.", new JsonObject
        {
            ["ms"] = Int("milliseconds (max 120000)"),
            ["until_change"] = new JsonObject { ["type"] = "boolean" },
        }),
        Tool("status", "VM state, logon/lock-screen state, helper state, screen size.", new JsonObject()),
        Tool("snapshot", "Create a restore point of the whole VM (do this before destructive changes).", new JsonObject
        {
            ["label"] = Str("snapshot name (default snap-YYYYMMDD-HHMMSS)"),
        }),
        Tool("restore", "Roll the VM back to a snapshot. The current state is saved first as pre-restore-YYYYMMDD-HHMMSS.", new JsonObject
        {
            ["label"] = Str("snapshot name"),
        }, "label"),
        Tool("export_file", "Copy a file or folder from the guest to the host's Outbox folder and return the host path. Default: the whole Desktop\\Outbox. Relative paths are inside Outbox.", new JsonObject
        {
            ["path"] = Str("path relative to Desktop\\Outbox, or an absolute guest path"),
        }),
        Tool("list_inbox", "List files the user sent into the guest's Desktop\\Inbox.", new JsonObject()),
        Tool("read_notes", "Read the work notes for this VM (kept on the host, survive restores). Read them at the start of every session.", new JsonObject()),
        Tool("append_notes", "Append to the work notes: what was done, where deliverables are, next steps, open problems.", new JsonObject
        {
            ["text"] = Str("Markdown text"),
        }, "text"));
}
