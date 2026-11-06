using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace TerminalWindows.Guest;

/// <summary>
/// Higher-level desktop operations built on UI Automation, so an agent can say "click the OK button"
/// or "type into the File name box" instead of guessing pixels: find / act on elements, read a window's
/// text, list and focus windows, the clipboard, and opening apps or files. All coordinates are physical
/// screen pixels (the MCP layers rescale them). UIA work runs on an STA thread with a timeout.
/// </summary>
public static class UiaActions
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static Task<T> Sta<T>(Func<T> work, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "uia-action" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task.WaitAsync(timeout ?? Timeout);
    }

    // ---------------------------------------------------------------- finding

    private sealed record Query(string? Name, string? ControlType, string? AutomationId, string? Window, bool Exact);

    private static Query ReadQuery(JsonObject a)
    {
        var q = new Query(Str(a, "name"), Str(a, "control_type"), Str(a, "automation_id"), Str(a, "window"), a["exact"]?.GetValue<bool>() == true);
        if (q.Name is null && q.AutomationId is null && q.ControlType is null)
            throw new ArgumentException("Give at least one of name, automation_id, control_type");
        return q;
    }

    private static string? Str(JsonObject a, string k) => a[k]?.GetValue<string>() is { Length: > 0 } s ? s : null;

    private static AutomationElement Root(string? window)
    {
        if (string.Equals(window, "desktop", StringComparison.OrdinalIgnoreCase)) return AutomationElement.RootElement;
        if (window is not null) return FindTopLevel(window) ?? throw new ArgumentException($"No window title contains '{window}' (see list_windows)");
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) throw new InvalidOperationException("No foreground window");
        return AutomationElement.FromHandle(fg);
    }

    private static AutomationElement? FindTopLevel(string titleOrHwnd)
    {
        if (long.TryParse(titleOrHwnd, out var hwnd))
        {
            try { return AutomationElement.FromHandle(new IntPtr(hwnd)); } catch { }
        }
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? exact = null, partial = null;
        for (var child = walker.GetFirstChild(AutomationElement.RootElement); child is not null; child = walker.GetNextSibling(child))
        {
            string name;
            try { name = child.Current.Name ?? ""; } catch { continue; }
            if (name.Equals(titleOrHwnd, StringComparison.OrdinalIgnoreCase)) exact ??= child;
            else if (name.Contains(titleOrHwnd, StringComparison.OrdinalIgnoreCase)) partial ??= child;
        }
        return exact ?? partial;
    }

    private static List<AutomationElement> Matches(Query q, int max)
    {
        var root = Root(q.Window);
        Condition cond = Automation.ControlViewCondition;
        if (q.AutomationId is not null)
            cond = new AndCondition(cond, new PropertyCondition(AutomationElement.AutomationIdProperty, q.AutomationId));
        if (q.ControlType is not null)
        {
            var ct = ParseControlType(q.ControlType);
            cond = new AndCondition(cond, new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
        }
        var all = root.FindAll(TreeScope.Subtree, cond);
        var found = new List<(AutomationElement El, int Score)>();
        foreach (AutomationElement el in all)
        {
            string name;
            bool offscreen;
            try { name = el.Current.Name ?? ""; offscreen = el.Current.IsOffscreen; } catch { continue; }
            int score;
            if (q.Name is null) score = 1;
            else if (name.Equals(q.Name, StringComparison.OrdinalIgnoreCase)) score = 3;
            else if (!q.Exact && name.Contains(q.Name, StringComparison.OrdinalIgnoreCase)) score = 2;
            else continue;
            if (!offscreen) score += 10;
            found.Add((el, score));
        }
        return found.OrderByDescending(f => f.Score).Take(max).Select(f => f.El).ToList();
    }

    private static ControlType ParseControlType(string s)
    {
        var name = s.Replace("ControlType.", "").Trim();
        foreach (var f in typeof(ControlType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (f.FieldType == typeof(ControlType) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (ControlType)f.GetValue(null)!;
        throw new ArgumentException($"Unknown control_type '{s}' (e.g. Button, Edit, MenuItem, ListItem, TabItem, Hyperlink, CheckBox, ComboBox)");
    }

    private static JsonObject Describe(AutomationElement el)
    {
        var o = new JsonObject();
        try
        {
            var c = el.Current;
            o["name"] = c.Name;
            o["controlType"] = c.ControlType.ProgrammaticName.Replace("ControlType.", "");
            if (!string.IsNullOrEmpty(c.AutomationId)) o["automationId"] = c.AutomationId;
            var r = c.BoundingRectangle;
            if (!r.IsEmpty && !double.IsInfinity(r.X))
            {
                o["rect"] = new JsonArray((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                o["center"] = new JsonArray((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
            }
            o["enabled"] = c.IsEnabled;
            if (c.IsOffscreen) o["offscreen"] = true;
            if (!c.IsPassword && el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = ((ValuePattern)vp).Current.Value;
                if (!string.IsNullOrEmpty(v)) o["value"] = v.Length > 300 ? v[..300] + "…" : v;
            }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) o["toggle"] = ((TogglePattern)tp).Current.ToggleState.ToString();
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) o["selected"] = ((SelectionItemPattern)sp).Current.IsSelected;
        }
        catch (ElementNotAvailableException) { o["stale"] = true; }
        return o;
    }

    public static Task<JsonObject> FindAsync(JsonObject a) => Sta(() =>
    {
        var q = ReadQuery(a);
        int max = Math.Clamp(a["max"]?.GetValue<int>() ?? 20, 1, 200);
        var list = Matches(q, max);
        return new JsonObject
        {
            ["count"] = list.Count,
            ["matches"] = new JsonArray(list.Select(e => (JsonNode)Describe(e)).ToArray()),
            ["coordinates"] = "physical screen pixels; rect = [x, y, width, height]",
        };
    });

    // ---------------------------------------------------------------- acting

    /// <summary>What to do with the element; the click variants move the real pointer, the rest use UIA patterns.</summary>
    public static async Task<JsonObject> ActAsync(JsonObject a)
    {
        var action = Str(a, "action") ?? "click";
        int index = Math.Max(0, a["index"]?.GetValue<int>() ?? 0);
        var value = a["value"]?.GetValue<string>();

        // Resolve, and do the pattern-based work, on the STA thread. Pointer clicks happen afterwards.
        var plan = await Sta(() =>
        {
            var q = ReadQuery(a);
            var list = Matches(q, index + 20);
            if (list.Count <= index)
                throw new ArgumentException(list.Count == 0
                    ? $"No element matches {Describe(q)}. Check the window with ui_tree or find_element."
                    : $"Only {list.Count} element(s) match {Describe(q)}; index {index} is out of range.");
            var el = list[index];
            var info = Describe(el);
            info["matches"] = list.Count;
            var c = el.Current;
            if (!c.IsEnabled && action is not ("focus" or "read")) throw new InvalidOperationException($"'{c.Name}' is disabled");

            switch (action)
            {
                case "click" or "double_click" or "right_click":
                {
                    if (c.IsOffscreen && el.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var sip))
                    {
                        try { ((ScrollItemPattern)sip).ScrollIntoView(); Thread.Sleep(150); c = el.Current; } catch (InvalidOperationException) { }
                    }
                    var r = c.BoundingRectangle;
                    if (r.IsEmpty || double.IsInfinity(r.X) || c.IsOffscreen)
                    {
                        if (action == "click" && el.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                        {
                            ((InvokePattern)ip).Invoke();
                            info["method"] = "invoke (element is not on screen)";
                            return (info, (int?)null, (int?)null);
                        }
                        throw new InvalidOperationException($"'{c.Name}' is not on screen; scroll it into view or bring its window to the front");
                    }
                    info["method"] = "pointer";
                    return (info, (int?)(int)(r.X + r.Width / 2), (int?)(int)(r.Y + r.Height / 2));
                }
                case "invoke":
                    if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) ((InvokePattern)inv).Invoke();
                    else if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tog)) ((TogglePattern)tog).Toggle();
                    else if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) ((SelectionItemPattern)sel).Select();
                    else if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec)) ((ExpandCollapsePattern)ec).Expand();
                    else throw new InvalidOperationException($"'{c.Name}' cannot be invoked; use action 'click'");
                    info["method"] = "invoke";
                    break;
                case "toggle":
                    if (!el.TryGetCurrentPattern(TogglePattern.Pattern, out var t)) throw new InvalidOperationException($"'{c.Name}' is not a toggle");
                    ((TogglePattern)t).Toggle();
                    info["toggle"] = ((TogglePattern)t).Current.ToggleState.ToString();
                    break;
                case "select":
                    if (!el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var s)) throw new InvalidOperationException($"'{c.Name}' is not selectable");
                    ((SelectionItemPattern)s).Select();
                    break;
                case "expand" or "collapse":
                    if (!el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var e)) throw new InvalidOperationException($"'{c.Name}' cannot expand/collapse");
                    if (action == "expand") ((ExpandCollapsePattern)e).Expand(); else ((ExpandCollapsePattern)e).Collapse();
                    break;
                case "focus":
                    el.SetFocus();
                    break;
                case "set_value":
                {
                    if (value is null) throw new ArgumentException("set_value needs 'value'");
                    if (c.IsPassword) throw new InvalidOperationException("Refusing to fill a password field; ask the person to type it");
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !((ValuePattern)vp).Current.IsReadOnly)
                    {
                        try
                        {
                            ((ValuePattern)vp).SetValue(value);
                            info["method"] = "ValuePattern";
                            info["value"] = ((ValuePattern)vp).Current.Value is var v && v.Length > 300 ? v[..300] + "…" : ((ValuePattern)vp).Current.Value;
                            return (info, (int?)null, (int?)null);
                        }
                        catch (InvalidOperationException) { }
                    }
                    el.SetFocus();
                    info["method"] = "focus + select all + type";
                    return (info, (int?)-1, (int?)-1);
                }
                default:
                    throw new ArgumentException($"Unknown action '{action}' (click, double_click, right_click, invoke, toggle, select, expand, collapse, focus, set_value)");
            }
            return (info, (int?)null, (int?)null);
        });

        var (result, x, y) = plan;
        if (x == -1)
        {
            await InputInjector.KeyAsync("ctrl+a");
            await InputInjector.TypeAsync(value!);
        }
        else if (x is int px && y is int py)
        {
            await InputInjector.ClickAsync(px, py, action == "right_click" ? "right" : "left", action == "double_click" ? 2 : 1);
        }
        return result;
    }

    private static string Describe(Query q)
    {
        var parts = new List<string>();
        if (q.Name is not null) parts.Add($"name '{q.Name}'");
        if (q.ControlType is not null) parts.Add($"type {q.ControlType}");
        if (q.AutomationId is not null) parts.Add($"automation_id '{q.AutomationId}'");
        parts.Add(q.Window is null ? "in the foreground window" : $"in window '{q.Window}'");
        return string.Join(", ", parts);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>The visible text of a window (names and values of its elements, in tree order), without a screenshot.</summary>
    public static Task<JsonObject> TextAsync(string? window, int maxChars) => Sta(() =>
    {
        var root = Root(window);
        var sb = new StringBuilder();
        string? last = null;
        int nodes = 0;
        bool truncated = false;
        void Walk(AutomationElement el, int depth)
        {
            if (truncated || depth > 40) return;
            nodes++;
            try
            {
                var c = el.Current;
                if (!c.IsOffscreen)
                {
                    var type = c.ControlType.ProgrammaticName.Replace("ControlType.", "");
                    string? val = null;
                    if (!c.IsPassword && el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) val = ((ValuePattern)vp).Current.Value;
                    if (!c.IsPassword && string.IsNullOrEmpty(val) && type is "Document" or "Edit" && el.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
                        val = ((TextPattern)tp).DocumentRange.GetText(maxChars);
                    var line = string.IsNullOrWhiteSpace(val) ? c.Name : string.IsNullOrWhiteSpace(c.Name) ? val : $"{c.Name}: {val}";
                    if (!string.IsNullOrWhiteSpace(line) && line != last)
                    {
                        sb.Append(type is "Button" or "MenuItem" or "TabItem" or "Hyperlink" or "CheckBox" or "RadioButton" or "Edit" or "ComboBox" ? $"[{type}] " : "")
                          .AppendLine(line.Trim());
                        last = line;
                        if (sb.Length > maxChars) { truncated = true; return; }
                    }
                }
            }
            catch (ElementNotAvailableException) { return; }
            var walker = TreeWalker.ControlViewWalker;
            AutomationElement? child;
            try { child = walker.GetFirstChild(el); } catch { child = null; }
            while (child is not null && !truncated)
            {
                Walk(child, depth + 1);
                try { child = walker.GetNextSibling(child); } catch { child = null; }
            }
        }
        Walk(root, 0);
        string title;
        try { title = root.Current.Name; } catch { title = ""; }
        var text = sb.ToString();
        if (text.Length > maxChars) text = text[..maxChars];
        return new JsonObject { ["window"] = title, ["text"] = text, ["truncated"] = truncated, ["elements"] = nodes };
    }, TimeSpan.FromSeconds(30));

    // ---------------------------------------------------------------- windows

    public static Task<JsonObject> WindowsAsync() => Sta(() =>
    {
        var fg = Native.GetForegroundWindow();
        var arr = new JsonArray();
        var walker = TreeWalker.ControlViewWalker;
        for (var w = walker.GetFirstChild(AutomationElement.RootElement); w is not null; w = walker.GetNextSibling(w))
        {
            try
            {
                var c = w.Current;
                if (string.IsNullOrWhiteSpace(c.Name) || c.NativeWindowHandle == 0) continue;
                if (c.ClassName is "Shell_TrayWnd" or "Progman" or "Shell_SecondaryTrayWnd") continue;
                var o = new JsonObject { ["title"] = c.Name, ["hwnd"] = c.NativeWindowHandle };
                try { o["process"] = Process.GetProcessById(c.ProcessId).ProcessName; } catch { }
                var r = c.BoundingRectangle;
                if (!r.IsEmpty && !double.IsInfinity(r.X)) o["rect"] = new JsonArray((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                if (w.TryGetCurrentPattern(WindowPattern.Pattern, out var wp))
                    o["state"] = ((WindowPattern)wp).Current.WindowVisualState.ToString();
                if (new IntPtr(c.NativeWindowHandle) == fg) o["foreground"] = true;
                arr.Add(o);
            }
            catch (ElementNotAvailableException) { }
        }
        return new JsonObject { ["windows"] = arr };
    });

    public static async Task<JsonObject> FocusWindowAsync(string titleOrHwnd)
    {
        var (hwnd, title) = await Sta(() =>
        {
            var el = FindTopLevel(titleOrHwnd) ?? throw new ArgumentException($"No window title contains '{titleOrHwnd}' (see list_windows)");
            if (el.TryGetCurrentPattern(WindowPattern.Pattern, out var wp) && ((WindowPattern)wp).Current.WindowVisualState == WindowVisualState.Minimized)
                ((WindowPattern)wp).SetWindowVisualState(WindowVisualState.Normal);
            return (new IntPtr(el.Current.NativeWindowHandle), el.Current.Name);
        });
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"'{title}' has no window handle");
        // Windows only lets the process with the last input change the foreground; a tap of Alt makes it us.
        await InputInjector.KeyAsync("alt");
        SetForegroundWindow(hwnd);
        await Task.Delay(200);
        var now = Native.GetForegroundWindow();
        return new JsonObject { ["focused"] = now == hwnd, ["title"] = title, ["foreground"] = Native.WindowTitle(now) };
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---------------------------------------------------------------- clipboard and launching

    public static Task<JsonObject> ClipboardGetAsync() => Sta(() =>
    {
        var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        return new JsonObject
        {
            ["text"] = text is { Length: > 100_000 } ? text[..100_000] : text,
            ["hasImage"] = System.Windows.Clipboard.ContainsImage(),
            ["hasFiles"] = System.Windows.Clipboard.ContainsFileDropList(),
        };
    }, TimeSpan.FromSeconds(10));

    public static Task<JsonObject> ClipboardSetAsync(string text) => Sta(() =>
    {
        System.Windows.Clipboard.SetDataObject(text, true);
        return new JsonObject { ["done"] = true, ["length"] = text.Length };
    }, TimeSpan.FromSeconds(10));

    /// <summary>Opens an app, file, folder or URL like the Run box / a double-click (ShellExecute).</summary>
    public static async Task<JsonObject> OpenAsync(string target, string? args)
    {
        var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target)) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        int? pid = null;
        try { using var p = Process.Start(psi); pid = p?.Id; }
        catch (System.ComponentModel.Win32Exception ex) { throw new ArgumentException($"Could not open '{target}': {ex.Message}"); }
        await Task.Delay(1500);
        var fg = Native.GetForegroundWindow();
        return new JsonObject { ["opened"] = target, ["pid"] = pid, ["foreground"] = fg == IntPtr.Zero ? null : Native.WindowTitle(fg) };
    }
}
