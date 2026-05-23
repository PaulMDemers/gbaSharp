namespace Gba.Core.Cartridges;

public sealed class Cartridge
{
    public const int HeaderLength = 0xC0;
    public const int EntryPointOffset = 0x000;
    public const int NintendoLogoOffset = 0x004;
    public const int TitleOffset = 0x0A0;
    public const int GameCodeOffset = 0x0AC;
    public const int MakerCodeOffset = 0x0B0;
    public const int FixedValueOffset = 0x0B2;
    public const int MainUnitCodeOffset = 0x0B3;
    public const int DeviceTypeOffset = 0x0B4;
    public const int SoftwareVersionOffset = 0x0BC;
    public const int ComplementCheckOffset = 0x0BD;

    private Cartridge(byte[] rom, CartridgeHeader header, SaveType saveType)
    {
        Rom = rom;
        Header = header;
        SaveType = saveType;
    }

    public byte[] Rom { get; }

    public CartridgeHeader Header { get; }

    public SaveType SaveType { get; }

    public static Cartridge Load(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderLength)
        {
            throw new ArgumentException("A GBA ROM must contain at least the 192-byte cartridge header.", nameof(rom));
        }

        var copy = rom.ToArray();
        return new Cartridge(copy, CartridgeHeader.Parse(copy), DetectSaveType(copy));
    }

    public static async Task<Cartridge> LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var rom = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Load(rom);
    }

    private static SaveType DetectSaveType(ReadOnlySpan<byte> rom)
    {
        if (ContainsAnyAscii(rom, "FLASH1M_V"))
        {
            return SaveType.Flash128K;
        }

        if (ContainsAnyAscii(rom, "FLASH_V", "FLASH512_V"))
        {
            return SaveType.Flash64K;
        }

        if (ContainsAnyAscii(rom, "EEPROM_V"))
        {
            return SaveType.Eeprom;
        }

        if (ContainsAnyAscii(rom, "SRAM_V"))
        {
            return SaveType.Sram;
        }

        return SaveType.None;
    }

    private static bool ContainsAnyAscii(ReadOnlySpan<byte> source, params string[] values)
    {
        foreach (var value in values)
        {
            if (ContainsAscii(source, value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> source, string value)
    {
        Span<byte> needle = stackalloc byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            needle[i] = (byte)value[i];
        }

        return source.IndexOf(needle) >= 0;
    }
}
