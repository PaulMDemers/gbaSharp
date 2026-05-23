namespace Gba.Core.Memory;

public static class GbaMemoryMap
{
    public const int BiosSize = 16 * 1024;
    public const int EwramSize = 256 * 1024;
    public const int IwramSize = 32 * 1024;
    public const int IoSize = 1024;
    public const int PaletteSize = 1024;
    public const int VramSize = 96 * 1024;
    public const int OamSize = 1024;
    public const int SramSize = 128 * 1024;

    public const uint BiosStart = 0x0000_0000;
    public const uint EwramStart = 0x0200_0000;
    public const uint IwramStart = 0x0300_0000;
    public const uint IoStart = 0x0400_0000;
    public const uint PaletteStart = 0x0500_0000;
    public const uint VramStart = 0x0600_0000;
    public const uint OamStart = 0x0700_0000;
    public const uint GamePakRomStart = 0x0800_0000;
    public const uint GamePakRomEnd = 0x0DFF_FFFF;
    public const uint GamePakSramStart = 0x0E00_0000;
    public const uint GamePakSramEnd = 0x0FFF_FFFF;

    public const uint RomEntryPoint = GamePakRomStart;
}
