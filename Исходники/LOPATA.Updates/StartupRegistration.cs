using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lopata.Updates;

/// <summary>One per-user startup entry. Never repairs it just because the application started.</summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistration(string launcher, string entry = "LOPATA",
    string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string approvalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
{
    public string Command { get; } = BuildCommand(launcher);

    public static string BuildCommand(string launcher)
    {
        if (!Path.IsPathFullyQualified(launcher) || launcher.Contains('"')) throw new ArgumentException("An absolute launcher path is required.");
        var command = "\"" + Path.GetFullPath(launcher) + "\" --launch --background";
        if (command.Length > 260) throw new ArgumentException("Startup command exceeds the Windows Run limit.");
        return command;
    }

    public bool IsRegistered
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(runKey); return key?.GetValue(entry) is string value && value.Equals(Command, StringComparison.OrdinalIgnoreCase); }
    }

    public bool IsEnabled
    {
        get
        {
            if (!IsRegistered) return false;
            using var key = Registry.CurrentUser.OpenSubKey(approvalKey);
            // Read conservatively, never rewrite Windows' decision on startup or an update.
            return key?.GetValue(entry) switch
            {
                null => true,
                byte[] data => data.Length > 0 && data[0] is 2 or 6,
                _ => false
            };
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKey, true);
        var existing = key.GetValue(entry);
        if (existing is not null && (existing is not string command || !command.Equals(Command, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The startup entry belongs to a different installation.");
        if (enabled)
        {
            if (!File.Exists(launcher)) throw new FileNotFoundException("The installed update launcher is missing.", launcher);
            key.SetValue(entry, Command, RegistryValueKind.String);
            // Only an explicit user choice may reset the disabled state of our own entry.
            using var approval = Registry.CurrentUser.OpenSubKey(approvalKey, true);
            approval?.DeleteValue(entry, false);
        }
        else if (IsRegistered)
        {
            key.DeleteValue(entry, false);
            using var approval = Registry.CurrentUser.OpenSubKey(approvalKey, true);
            approval?.DeleteValue(entry, false);
        }
        if (IsEnabled != enabled) throw new IOException("Windows startup registration was not confirmed.");
    }

    public sealed record Snapshot(string? Command, object? Approval, RegistryValueKind ApprovalKind);
    public Snapshot Capture()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKey);
        using var approval = Registry.CurrentUser.OpenSubKey(approvalKey);
        if (run?.GetValue(entry) is { } command && command is not string) throw new IOException("Unknown startup entry format.");
        var value = approval?.GetValue(entry);
        return new(run?.GetValue(entry) as string, value, value is null ? RegistryValueKind.None : approval!.GetValueKind(entry));
    }
    public void Restore(Snapshot previous)
    {
        using var run = Registry.CurrentUser.CreateSubKey(runKey, true);
        var current = run.GetValue(entry) as string;
        if (current is not null && current != previous.Command && !current.Equals(Command, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cannot replace another installation's startup entry.");
        if (previous.Command is null) run.DeleteValue(entry, false); else run.SetValue(entry, previous.Command, RegistryValueKind.String);
        using var approval = Registry.CurrentUser.CreateSubKey(approvalKey, true);
        if (previous.Approval is null) approval.DeleteValue(entry, false); else approval.SetValue(entry, previous.Approval, previous.ApprovalKind);
    }
}
