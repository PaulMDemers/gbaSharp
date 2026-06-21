namespace Gba.Desktop;

internal sealed record DesktopStartupOptions(
    string? StartupRomPath,
    bool ControlServerEnabled = false,
    int ControlPort = DesktopStartupOptions.DefaultControlPort)
{
    public const int DefaultControlPort = 8765;
}
