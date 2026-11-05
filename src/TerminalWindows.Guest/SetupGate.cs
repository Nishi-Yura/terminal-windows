using System.Globalization;
using System.Text.Json.Nodes;

namespace TerminalWindows.Guest;

public enum SetupGateDecision
{
    /// <summary>Setup is not finished (or the post-install reboot has not happened yet): do not serve.</summary>
    Wait,
    /// <summary>First start after the post-install reboot: mark setup complete, then serve.</summary>
    CompleteNowAndServe,
    /// <summary>Setup completed earlier: serve.</summary>
    Serve,
}

/// <summary>
/// Decides whether the helper may serve. The scheduled task's repeating trigger can start the helper
/// while FirstLogonCommands are still installing; serving then would make the host finalise setup
/// (eject media, shut down, snapshot) in the middle of the Guest Additions install. So the helper
/// serves only when setup-state.json says "complete", or says "installed" AND the machine has booted
/// since that state was written.
/// </summary>
public static class SetupGate
{
    public static SetupGateDecision Decide(string? stateJson, DateTime bootTimeUtc)
    {
        string phase = "";
        DateTime stateTime = DateTime.MaxValue;
        try
        {
            var state = string.IsNullOrWhiteSpace(stateJson) ? null : JsonNode.Parse(stateJson);
            phase = state?["phase"]?.GetValue<string>() ?? "";
            var time = state?["time"]?.GetValue<string>();
            if (time is not null)
                stateTime = DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }
        catch
        {
            return SetupGateDecision.Wait; // unreadable state: never serve by accident
        }

        return phase switch
        {
            "complete" => SetupGateDecision.Serve,
            "installed" when bootTimeUtc > stateTime => SetupGateDecision.CompleteNowAndServe,
            _ => SetupGateDecision.Wait,
        };
    }
}
