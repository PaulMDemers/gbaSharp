using System.Text;

namespace Gba.Core.Cartridges;

public sealed record CartridgeHeader(
    uint EntryPoint,
    string Title,
    string GameCode,
    string MakerCode,
    byte FixedValue,
    byte MainUnitCode,
    byte DeviceType,
    byte SoftwareVersion,
    byte ComplementCheck,
    bool HasValidFixedValue,
    bool HasValidComplementCheck)
{
    public static CartridgeHeader Parse(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < Cartridge.HeaderLength)
        {
            throw new ArgumentException("A GBA ROM must contain at least the 192-byte cartridge header.", nameof(rom));
        }

        var entryPoint = ReadUInt32LittleEndian(rom[Cartridge.EntryPointOffset..]);
        var title = ReadAscii(rom.Slice(Cartridge.TitleOffset, 12));
        var gameCode = ReadAscii(rom.Slice(Cartridge.GameCodeOffset, 4));
        var makerCode = ReadAscii(rom.Slice(Cartridge.MakerCodeOffset, 2));
        var fixedValue = rom[Cartridge.FixedValueOffset];
        var mainUnitCode = rom[Cartridge.MainUnitCodeOffset];
        var deviceType = rom[Cartridge.DeviceTypeOffset];
        var softwareVersion = rom[Cartridge.SoftwareVersionOffset];
        var complementCheck = rom[Cartridge.ComplementCheckOffset];

        return new CartridgeHeader(
            entryPoint,
            title,
            gameCode,
            makerCode,
            fixedValue,
            mainUnitCode,
            deviceType,
            softwareVersion,
            complementCheck,
            fixedValue == 0x96,
            complementCheck == ComputeComplementCheck(rom));
    }

    public static byte ComputeComplementCheck(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < Cartridge.HeaderLength)
        {
            throw new ArgumentException("A GBA ROM must contain at least the 192-byte cartridge header.", nameof(rom));
        }

        byte value = 0;
        for (var i = 0xA0; i <= 0xBC; i++)
        {
            value -= rom[i];
        }

        value = (byte)(value - 0x19);
        return value;
    }

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> source)
        => (uint)(source[0] | (source[1] << 8) | (source[2] << 16) | (source[3] << 24));

    private static string ReadAscii(ReadOnlySpan<byte> source)
    {
        var end = source.IndexOf((byte)0);
        if (end < 0)
        {
            end = source.Length;
        }

        return Encoding.ASCII.GetString(source[..end]).TrimEnd();
    }
}

