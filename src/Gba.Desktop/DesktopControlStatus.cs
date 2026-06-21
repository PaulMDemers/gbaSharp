namespace Gba.Desktop;

internal sealed record DesktopControlStatus(
    bool HasRom,
    bool Running,
    string? RomPath,
    string? RomName,
    long EmulatedFrames,
    long PresentedFrames,
    string PressedKeys,
    double SpeedMultiplier,
    bool UnlimitedSpeed);
