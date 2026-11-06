using System.Security;
using System.Text;

namespace TerminalWindows.Cli.Setup;

public sealed record UnattendOptions(
    string ComputerName,
    string User,
    string Password,
    string Locale,
    string UiLanguage,
    string TimeZone,
    int? ImageIndex,
    string? ProductKey,
    bool BypassTpmCheck = false);

/// <summary>
/// Generates autounattend.xml for a fully unattended Windows 11 install in the GUEST VM:
/// wipes VM disk 0 (UEFI/GPT), creates a local admin account with a random password,
/// skips OOBE and online-account screens, minimal privacy settings, enables auto-logon, and
/// runs the helper installer from the setup ISO on first logon.
/// Windows 11 requirements are met by VirtualBox's EFI + Secure Boot + TPM 2.0; no TPM/CPU
/// bypass registry keys are used.
/// </summary>
public static class UnattendGenerator
{
    private const string Component = "processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\"";

    public static string KeyboardFor(string locale) => locale.ToLowerInvariant() switch
    {
        // Japanese keyboard + Microsoft IME
        "ja-jp" => "0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}",
        "en-us" => "0409:00000409",
        "en-gb" => "0809:00000809",
        _ => locale,
    };

    /// <summary>
    /// Microsoft's published KMS client setup keys
    /// (https://learn.microsoft.com/windows-server/get-started/kms-client-activation-keys).
    /// Used ONLY so that unattended Setup on multi-edition media installs the chosen edition without
    /// stopping at the product-key page (an all-zero key was verified to stop there). They do not
    /// activate Windows; licensing the guest remains the user's responsibility (--product-key).
    /// Home has no KMS key, so Home needs --product-key.
    /// </summary>
    public static string? InstallKeyFor(string? editionId) => editionId switch
    {
        "Professional" => "W269N-WFGWX-YVC9B-4J6C9-T83GX",
        "ProfessionalN" => "MH37W-N47XK-V7XM9-C7227-GCQG9",
        "ProfessionalWorkstation" => "NRG8B-VKK3Q-CXVCJ-9G2XF-6Q84J",
        "ProfessionalEducation" => "6TP4R-GNPTD-KYYHQ-7B7DP-J447Y",
        "Education" => "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2",
        "Enterprise" => "NPPR9-FWDCX-D2C8J-H872K-2YT43",
        _ => null,
    };

    public static string DefaultTimeZone(string locale) => locale.ToLowerInvariant() switch
    {
        "ja-jp" => "Tokyo Standard Time",
        "en-us" => "Pacific Standard Time",
        "en-gb" => "GMT Standard Time",
        _ => "UTC",
    };

    /// <summary>NetBIOS-safe computer name (max 15 chars).</summary>
    public static string ComputerNameFor(string machine)
    {
        var clean = new string(("TWIN-" + machine.ToUpperInvariant()).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return clean.Length > 15 ? clean[..15] : clean;
    }

    public static string Generate(UnattendOptions o)
    {
        string X(string s) => SecurityElement.Escape(s)!;
        var keyboard = X(KeyboardFor(o.Locale));
        var locale = X(o.Locale);
        var ui = X(o.UiLanguage);

        var installFrom = o.ImageIndex is int idx
            ? $"""
                        <InstallFrom>
                            <MetaData wcm:action="add">
                                <Key>/IMAGE/INDEX</Key>
                                <Value>{idx}</Value>
                            </MetaData>
                        </InstallFrom>
            """
            : "";
        // A key is only written when the caller resolved one (user key or a published KMS client setup key).
        var key = string.IsNullOrWhiteSpace(o.ProductKey) ? null : o.ProductKey;
        var productKey = key is null
            ? ""
            : $"""
                    <ProductKey>
                        <Key>{X(key!)}</Key>
                        <WillShowUI>OnError</WillShowUI>
                    </ProductKey>
            """;

        // FirstLogonCommands: find the setup ISO by its marker file and run the helper installer.
        const string firstLogon = "cmd.exe /c \"for %d in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %d:\\terminal-windows\\setup.cmd call %d:\\terminal-windows\\setup.cmd\"";

        // VMware's virtual TPM needs the VM encrypted with a password; for a disposable AI VM we skip
        // Setup's TPM / Secure Boot check instead (Microsoft's LabConfig switches, read in windowsPE).
        var labConfig = o.BypassTpmCheck ? """
            <RunSynchronous>
                <RunSynchronousCommand wcm:action="add">
                    <Order>1</Order>
                    <Path>reg.exe add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path>
                </RunSynchronousCommand>
                <RunSynchronousCommand wcm:action="add">
                    <Order>2</Order>
                    <Path>reg.exe add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path>
                </RunSynchronousCommand>
            </RunSynchronous>
""" : "";

        var sb = new StringBuilder();
        sb.Append($$"""
<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by terminal-windows for an AI-only VirtualBox guest. Contains the guest's random
     auto-logon password; this file lives only on the temporary setup ISO. -->
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
    <settings pass="windowsPE">
        <component name="Microsoft-Windows-International-Core-WinPE" {{Component}}>
            <SetupUILanguage>
                <UILanguage>{{ui}}</UILanguage>
            </SetupUILanguage>
            <InputLocale>{{keyboard}}</InputLocale>
            <SystemLocale>{{locale}}</SystemLocale>
            <UILanguage>{{ui}}</UILanguage>
            <UserLocale>{{locale}}</UserLocale>
        </component>
        <component name="Microsoft-Windows-Setup" {{Component}}>
            <DiskConfiguration>
                <Disk wcm:action="add">
                    <DiskID>0</DiskID>
                    <WillWipeDisk>true</WillWipeDisk>
                    <CreatePartitions>
                        <CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>260</Size></CreatePartition>
                        <CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>
                        <CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
                    </CreatePartitions>
                    <ModifyPartitions>
                        <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>
                        <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID></ModifyPartition>
                        <ModifyPartition wcm:action="add"><Order>3</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
                    </ModifyPartitions>
                </Disk>
            </DiskConfiguration>
            <ImageInstall>
                <OSImage>
{{installFrom}}
                    <InstallTo>
                        <DiskID>0</DiskID>
                        <PartitionID>3</PartitionID>
                    </InstallTo>
                </OSImage>
            </ImageInstall>
            <UserData>
                <AcceptEula>true</AcceptEula>
                <FullName>{{X(o.User)}}</FullName>
                <Organization>terminal-windows</Organization>
{{productKey}}
            </UserData>
{{labConfig}}
        </component>
    </settings>
    <settings pass="specialize">
        <component name="Microsoft-Windows-Shell-Setup" {{Component}}>
            <ComputerName>{{X(o.ComputerName)}}</ComputerName>
            <TimeZone>{{X(o.TimeZone)}}</TimeZone>
        </component>
        <component name="Microsoft-Windows-Deployment" {{Component}}>
            <RunSynchronous>
                <RunSynchronousCommand wcm:action="add">
                    <Order>1</Order>
                    <Description>Allow OOBE without a network / Microsoft account</Description>
                    <Path>reg.exe add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f</Path>
                </RunSynchronousCommand>
            </RunSynchronous>
        </component>
    </settings>
    <settings pass="oobeSystem">
        <component name="Microsoft-Windows-International-Core" {{Component}}>
            <InputLocale>{{keyboard}}</InputLocale>
            <SystemLocale>{{locale}}</SystemLocale>
            <UILanguage>{{ui}}</UILanguage>
            <UserLocale>{{locale}}</UserLocale>
        </component>
        <component name="Microsoft-Windows-Shell-Setup" {{Component}}>
            <TimeZone>{{X(o.TimeZone)}}</TimeZone>
            <OOBE>
                <HideEULAPage>true</HideEULAPage>
                <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
                <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                <HideLocalAccountScreen>true</HideLocalAccountScreen>
                <ProtectYourPC>3</ProtectYourPC>
            </OOBE>
            <UserAccounts>
                <LocalAccounts>
                    <LocalAccount wcm:action="add">
                        <Name>{{X(o.User)}}</Name>
                        <DisplayName>{{X(o.User)}}</DisplayName>
                        <Group>Administrators</Group>
                        <Password>
                            <Value>{{X(o.Password)}}</Value>
                            <PlainText>true</PlainText>
                        </Password>
                    </LocalAccount>
                </LocalAccounts>
            </UserAccounts>
            <AutoLogon>
                <Enabled>true</Enabled>
                <Username>{{X(o.User)}}</Username>
                <Password>
                    <Value>{{X(o.Password)}}</Value>
                    <PlainText>true</PlainText>
                </Password>
                <LogonCount>9999999</LogonCount>
            </AutoLogon>
            <FirstLogonCommands>
                <SynchronousCommand wcm:action="add">
                    <Order>1</Order>
                    <Description>Install the terminal-windows guest helper</Description>
                    <CommandLine>{{X(firstLogon)}}</CommandLine>
                    <RequiresUserInput>false</RequiresUserInput>
                </SynchronousCommand>
            </FirstLogonCommands>
        </component>
    </settings>
</unattend>
""");
        return sb.ToString();
    }
}
