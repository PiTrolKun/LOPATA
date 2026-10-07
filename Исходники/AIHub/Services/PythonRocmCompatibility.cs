using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Acquisition hint from AMD's versioned Windows 7.2.1 matrix; execution still requires a real probe.</summary>
internal static partial class PythonRocmCompatibility
{
    [GeneratedRegex(@"\b(?:RX\s*(?:9070(?:\s*XT)?|9060\s*XT|7900\s*XTX|7700)|PRO\s+W7900(?:\s+Dual\s+Slot)?|AI\s+PRO\s+R9700|Radeon(?:\(TM\))?\s+(?:8060S|8050S|890M|880M|860M))(?:\s+Graphics)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportedRadeon();

    internal static bool ShouldAcquire(string name, Version windowsVersion) =>
        windowsVersion.Major >= 10 && windowsVersion.Build >= 22000
        && name.Contains("AMD", StringComparison.OrdinalIgnoreCase) && SupportedRadeon().IsMatch(name.Trim());
}
