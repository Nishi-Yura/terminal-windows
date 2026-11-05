using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace TerminalWindows.Guest;

/// <summary>
/// Dumps the UI Automation control-view tree of the foreground window (or a window whose title
/// contains <c>window</c>, or the whole desktop's top-level windows) as JSON. Runs on an STA
/// thread with a timeout because UIA calls can block on hung applications.
/// </summary>
public static class UiaTree
{
    public static Task<JsonObject> GetAsync(string? window, int depth, int maxNodes, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(Build(window, depth, maxNodes)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "uia" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task.WaitAsync(timeout);
    }

    private static JsonObject Build(string? window, int depth, int maxNodes)
    {
        AutomationElement root;
        if (string.Equals(window, "desktop", StringComparison.OrdinalIgnoreCase))
        {
            root = AutomationElement.RootElement;
            depth = Math.Min(depth, 1);
        }
        else if (!string.IsNullOrEmpty(window))
        {
            root = FindWindow(window) ?? throw new ArgumentException($"No top-level window title contains '{window}'");
        }
        else
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) throw new InvalidOperationException("No foreground window");
            root = AutomationElement.FromHandle(fg);
        }

        int count = 0;
        bool truncated = false;
        var tree = Walk(root, depth, maxNodes, ref count, ref truncated);
        return new JsonObject
        {
            ["root"] = tree,
            ["nodes"] = count,
            ["truncated"] = truncated,
            ["coordinates"] = "physical screen pixels (same as screenshot); rect = [x, y, width, height]",
        };
    }

    private static AutomationElement? FindWindow(string titlePart)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var child = walker.GetFirstChild(AutomationElement.RootElement); child is not null; child = walker.GetNextSibling(child))
        {
            string name;
            try { name = child.Current.Name ?? ""; } catch { continue; }
            if (name.Contains(titlePart, StringComparison.OrdinalIgnoreCase)) return child;
        }
        if (long.TryParse(titlePart, out var hwnd)) return AutomationElement.FromHandle(new IntPtr(hwnd));
        return null;
    }

    private static JsonObject Walk(AutomationElement el, int depth, int maxNodes, ref int count, ref bool truncated)
    {
        count++;
        var node = Describe(el);
        if (depth <= 0) return node;

        var children = new JsonArray();
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? child;
        try { child = walker.GetFirstChild(el); } catch { child = null; }
        while (child is not null)
        {
            if (count >= maxNodes)
            {
                truncated = true;
                break;
            }
            children.Add(Walk(child, depth - 1, maxNodes, ref count, ref truncated));
            try { child = walker.GetNextSibling(child); } catch { child = null; }
        }
        if (children.Count > 0) node["children"] = children;
        return node;
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
            if (!string.IsNullOrEmpty(c.ClassName)) o["className"] = c.ClassName;
            var r = c.BoundingRectangle;
            if (!r.IsEmpty && !double.IsInfinity(r.X))
                o["rect"] = new JsonArray((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            o["enabled"] = c.IsEnabled;
            if (c.HasKeyboardFocus) o["focused"] = true;
            if (c.IsOffscreen) o["offscreen"] = true;
            if (c.NativeWindowHandle != 0) o["hwnd"] = c.NativeWindowHandle;
            // Never read the value of password fields.
            if (!c.IsPassword && el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = ((ValuePattern)vp).Current.Value;
                if (!string.IsNullOrEmpty(v)) o["value"] = v.Length > 500 ? v[..500] + "…" : v;
            }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tp))
                o["toggle"] = ((TogglePattern)tp).Current.ToggleState.ToString();
        }
        catch (ElementNotAvailableException)
        {
            o["stale"] = true;
        }
        return o;
    }
}
