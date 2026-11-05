using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using TerminalWindows.Protocol;

namespace TerminalWindows.Guest;

/// <summary>
/// Configures THE GUEST VM ONLY for unattended AI operation. Invoked from autounattend.xml
/// FirstLogonCommands (elevated) on first logon.
///
/// WARNING: The settings below (UAC prompts without the secure desktop, permanent auto-logon,
/// no lock screen) are acceptable only because this is a disposable, AI-dedicated VM.
/// Never apply them to a host or to a PC people use day to day. <see cref="VmCheck"/> refuses to
/// run outside VirtualBox to prevent accidents (it is not a security boundary).
/// </summary>
public static class Installer
{
    public static int Run(string sourceDir)
    {
        Directory.CreateDirectory(GuestPaths.LogDir);
        Log.FilePath = Path.Combine(GuestPaths.LogDir, "install.log");
        Log.Write($"install from {sourceDir}");

        var config = GuestConfig.Load(Path.Combine(sourceDir, "guest.json"));

        // Until the post-install reboot, 'run' refuses to serve (the scheduled task can fire mid-install,
        // and the host must not mistake a reachable helper for a finished setup).
        WriteState("installing");
        Step("copy files", () => CopyFiles(sourceDir));
        Step("uac", ConfigureUac);
        Step("autologon", () => ConfigureAutoLogon(config));
        Step("lock screen / power", ConfigureLockAndPower);
        Step("notifications", ConfigureNotifications);
        Step("windows update", ConfigureWindowsUpdate);
        if (config.Locale.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) Step("japanese keyboard", ConfigureJapaneseKeyboard);
        Step("inbox/outbox", () => CreateExchangeFolders(config));
        Step("firewall", () => ConfigureFirewall(config));
        Step("scheduled task", () => RegisterTask(config));
        Step("guest additions", InstallGuestAdditions);

        WriteState("installed");
        Log.Write("install finished; rebooting");
        Exec("shutdown.exe", "/r /t 10 /c \"terminal-windows setup finished\"");
        return 0;
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
            Log.Write($"[ok] {name}");
        }
        catch (Exception ex)
        {
            Log.Write($"[failed] {name}: {ex}");
        }
    }

    public static void WriteState(string phase)
    {
        Directory.CreateDirectory(GuestPaths.DataDir);
        var state = new JsonObject { ["phase"] = phase, ["time"] = DateTime.UtcNow.ToString("o") };
        File.WriteAllText(GuestPaths.StateFile, state.ToJsonString());
    }

    private static void CopyFiles(string sourceDir)
    {
        Directory.CreateDirectory(GuestPaths.InstallDir);
        File.Copy(Path.Combine(sourceDir, "TerminalWindowsGuest.exe"), GuestPaths.InstalledExe, true);
        // Files copied from CD/UDF keep FILE_ATTRIBUTE_READONLY, which would make self-update's move fail.
        File.SetAttributes(GuestPaths.InstalledExe, FileAttributes.Normal);

        Directory.CreateDirectory(GuestPaths.DataDir);
        File.Copy(Path.Combine(sourceDir, "guest.json"), GuestPaths.ConfigFile, true);
        File.SetAttributes(GuestPaths.ConfigFile, FileAttributes.Normal);
        // The token and the auto-logon password are only readable by SYSTEM and Administrators.
        var fi = new FileInfo(GuestPaths.ConfigFile);
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        fi.SetAccessControl(sec);
    }

    private static void SetDword(RegistryKey root, string path, string name, int value)
    {
        using var k = root.CreateSubKey(path, true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void SetString(RegistryKey root, string path, string name, string value)
    {
        using var k = root.CreateSubKey(path, true);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    private static void ConfigureUac()
    {
        // GUEST ONLY: elevation without a prompt and without switching to the secure desktop,
        // because the agent cannot see or click the secure desktop.
        const string p = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        SetDword(Registry.LocalMachine, p, "ConsentPromptBehaviorAdmin", 0);
        SetDword(Registry.LocalMachine, p, "PromptOnSecureDesktop", 0);
    }

    private static void ConfigureAutoLogon(GuestConfig c)
    {
        // GUEST ONLY. The password is random and exists only for this VM.
        const string p = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        SetString(Registry.LocalMachine, p, "AutoAdminLogon", "1");
        SetString(Registry.LocalMachine, p, "DefaultUserName", c.User);
        SetString(Registry.LocalMachine, p, "DefaultDomainName", Environment.MachineName);
        SetString(Registry.LocalMachine, p, "DefaultPassword", c.Password);
        using var k = Registry.LocalMachine.OpenSubKey(p, true);
        k?.DeleteValue("AutoLogonCount", false);
        // Windows 11 "passwordless" toggle hides the auto-logon option; turn it off.
        SetDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device", "DevicePasswordLessBuildVersion", 0);
    }

    private static void ConfigureLockAndPower()
    {
        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 1);
        SetDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "InactivityTimeoutSecs", 0);
        SetString(Registry.CurrentUser, @"Control Panel\Desktop", "ScreenSaveActive", "0");
        SetString(Registry.CurrentUser, @"Control Panel\Desktop", "ScreenSaverIsSecure", "0");
        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop", "ScreenSaveActive", 0);
        foreach (var args in new[]
                 {
                     "/change monitor-timeout-ac 0", "/change monitor-timeout-dc 0",
                     "/change standby-timeout-ac 0", "/change standby-timeout-dc 0",
                     "/change hibernate-timeout-ac 0", "/hibernate off",
                     "/setacvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK 0",
                     "/setdcvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK 0",
                     "/setactive SCHEME_CURRENT",
                 })
            Exec("powercfg.exe", args);
    }

    private static void ConfigureNotifications()
    {
        SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0);
        const string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        foreach (var v in new[] { "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled", "SubscribedContent-353694Enabled",
                     "SubscribedContent-353696Enabled", "SubscribedContent-310093Enabled", "SoftLandingEnabled", "SystemPaneSuggestionsEnabled",
                     "SilentInstalledAppsEnabled", "RotatingLockScreenEnabled", "RotatingLockScreenOverlayEnabled" })
            SetDword(Registry.CurrentUser, cdm, v, 0);
        SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0);
        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1);
        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableSoftLanding", 1);
        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\OOBE", "DisablePrivacyExperience", 1);
        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "HideFirstRunExperience", 1);
        SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1);
        SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 0);
    }

    private static void ConfigureWindowsUpdate()
    {
        // Restart only outside active hours (07:00-01:00) and never while the agent user is logged on.
        const string au = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
        SetDword(Registry.LocalMachine, au, "SetActiveHours", 1);
        SetDword(Registry.LocalMachine, au, "ActiveHoursStart", 7);
        SetDword(Registry.LocalMachine, au, "ActiveHoursEnd", 1);
        SetDword(Registry.LocalMachine, au + @"\AU", "NoAutoRebootWithLoggedOnUsers", 1);
    }

    private static void ConfigureJapaneseKeyboard()
    {
        const string p = @"SYSTEM\CurrentControlSet\Services\i8042prt\Parameters";
        SetString(Registry.LocalMachine, p, "LayerDriver JPN", "kbd106.dll");
        SetString(Registry.LocalMachine, p, "OverrideKeyboardIdentifier", "PCAT_106KEY");
        SetDword(Registry.LocalMachine, p, "OverrideKeyboardType", 7);
        SetDword(Registry.LocalMachine, p, "OverrideKeyboardSubtype", 2);
    }

    private static void CreateExchangeFolders(GuestConfig c)
    {
        var desktop = GuestPaths.UserDesktop(c.User);
        Directory.CreateDirectory(Path.Combine(desktop, "Outbox"));
        Directory.CreateDirectory(Path.Combine(desktop, "Inbox"));
    }

    private static void ConfigureFirewall(GuestConfig c)
    {
        const string rule = "terminal-windows helper";
        Exec("netsh.exe", $"advfirewall firewall delete rule name=\"{rule}\"");
        Exec("netsh.exe", $"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP localport={c.Port} remoteip={ProtocolConstants.NatSubnet} program=\"{GuestPaths.InstalledExe}\" profile=any");
        if (c.Offline)
        {
            // "Don't reach the internet by accident" only — the agent has admin rights and could undo this.
            Exec("netsh.exe", "advfirewall set allprofiles state on");
            Exec("netsh.exe", "advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound");
        }
    }

    private static void RegisterTask(GuestConfig c)
    {
        var xml = BuildTaskXml($"{Environment.MachineName}\\{c.User}", GuestPaths.InstalledExe);
        var tmp = Path.Combine(GuestPaths.DataDir, "task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        Exec("schtasks.exe", $"/create /tn \"{ProtocolConstants.ScheduledTaskName}\" /xml \"{tmp}\" /f", throwOnError: true);
        File.Delete(tmp);
    }

    /// <summary>Task Scheduler XML: logon trigger + 5-minute repeat (restarts a crashed helper), highest privileges, interactive session.</summary>
    public static string BuildTaskXml(string user, string exePath)
    {
        string Esc(string s) => System.Security.SecurityElement.Escape(s)!;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>terminal-windows guest helper (AI-only VM)</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId></LogonTrigger>
    <TimeTrigger><Enabled>true</Enabled><StartBoundary>2024-01-01T00:00:00</StartBoundary><Repetition><Interval>PT5M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition></TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>"{Esc(exePath)}"</Command><Arguments>run</Arguments></Exec>
  </Actions>
</Task>
""";
    }

    private static void InstallGuestAdditions()
    {
        // VBoxGuestAdditions.iso ships with VirtualBox itself (not the Extension Pack).
        // Clipboard, drag & drop and shared folders stay disabled on the host side.
        var drive = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom && d.IsReady)
            .FirstOrDefault(d => File.Exists(Path.Combine(d.RootDirectory.FullName, "VBoxWindowsAdditions.exe")));
        if (drive is null)
        {
            Log.Write("Guest Additions ISO not found; skipping");
            return;
        }
        var root = drive.RootDirectory.FullName;
        var certDir = Path.Combine(root, "cert");
        var certUtil = Path.Combine(certDir, "VBoxCertUtil.exe");
        if (File.Exists(certUtil))
        {
            var certs = Directory.GetFiles(certDir, "vbox*.cer").Select(f => $"\"{f}\"").ToList();
            if (certs.Count > 0)
                Exec(certUtil, $"add-trusted-publisher {string.Join(' ', certs)} --root {string.Join(' ', certs)}", workDir: certDir);
        }
        // No output redirection: NSIS may leave child processes holding inherited pipe handles,
        // which would block reading stdout forever.
        Exec(Path.Combine(root, "VBoxWindowsAdditions.exe"), "/S", timeoutMinutes: 15, captureOutput: false);
    }

    private static int Exec(string file, string args, bool throwOnError = false, string? workDir = null, int timeoutMinutes = 5, bool captureOutput = true)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = captureOutput, RedirectStandardError = captureOutput,
            WorkingDirectory = workDir ?? Environment.SystemDirectory,
        };
        using var p = Process.Start(psi)!;
        var stdout = captureOutput ? p.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var stderr = captureOutput ? p.StandardError.ReadToEndAsync() : Task.FromResult("");
        if (!p.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes)))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{file} timed out");
        }
        // Never block indefinitely on output (a grandchild may still hold the pipe open).
        string Out(Task<string> t) => t.Wait(TimeSpan.FromSeconds(10)) ? t.Result.Trim() : "(output still open)";
        Log.Write($"  {Path.GetFileName(file)} {args} -> {p.ExitCode} {Out(stdout)} {Out(stderr)}");
        if (throwOnError && p.ExitCode != 0) throw new InvalidOperationException($"{file} exited with {p.ExitCode}");
        return p.ExitCode;
    }
}
