namespace Gba.Desktop;

internal sealed record DesktopInputResult(
    string Command,
    string Keys,
    int DurationMs,
    int DelayMs,
    DesktopControlStatus Before,
    DesktopControlStatus After);
