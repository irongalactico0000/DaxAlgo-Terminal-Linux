namespace DaxAlgo.Daxq.Host.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows security APIs.";
    }
}

public sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS Keychain or code-signature APIs.";
    }
}

public sealed class DesktopSecurityFactAttribute : FactAttribute
{
    public DesktopSecurityFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            Skip = "DAXQ persistent protection is supported on Windows and macOS.";
    }
}
