using Gba.Core.Cartridges;

namespace Gba.Tests;

public sealed class CartridgeHeaderTests
{
    [Fact]
    public void ParseReadsMetadataAndValidatesChecksum()
    {
        var rom = CreateMinimalRom();

        var header = CartridgeHeader.Parse(rom);

        Assert.Equal(0xEA00002E, header.EntryPoint);
        Assert.Equal("TEST GAME", header.Title);
        Assert.Equal("TGME", header.GameCode);
        Assert.Equal("01", header.MakerCode);
        Assert.True(header.HasValidFixedValue);
        Assert.True(header.HasValidComplementCheck);
    }

    [Fact]
    public void LoadRejectsTooSmallRom()
    {
        Assert.Throws<ArgumentException>(() => Cartridge.Load(new byte[Cartridge.HeaderLength - 1]));
    }

    [Theory]
    [InlineData("SRAM_V113", SaveType.Sram)]
    [InlineData("EEPROM_V124", SaveType.Eeprom)]
    [InlineData("FLASH_V123", SaveType.Flash64K)]
    [InlineData("FLASH1M_V103", SaveType.Flash128K)]
    public void LoadDetectsSaveTypeFromRomMarker(string marker, SaveType expected)
    {
        var rom = CreateMinimalRom();
        Array.Resize(ref rom, 0x200);
        WriteAscii(rom, 0x100, marker.Length, marker);

        var cartridge = Cartridge.Load(rom);

        Assert.Equal(expected, cartridge.SaveType);
    }

    [Fact]
    public void LoadIgnoresBareFlashStringWhenEepromMarkerIsPresent()
    {
        var rom = CreateMinimalRom();
        Array.Resize(ref rom, 0x220);
        WriteAscii(rom, 0x100, "EEPROM_V124".Length, "EEPROM_V124");
        WriteAscii(rom, 0x180, "FLASH".Length, "FLASH");

        var cartridge = Cartridge.Load(rom);

        Assert.Equal(SaveType.Eeprom, cartridge.SaveType);
    }

    [Fact]
    public void LoadDoesNotTreatBareBackupWordsAsSaveMarkers()
    {
        var rom = CreateMinimalRom();
        Array.Resize(ref rom, 0x220);
        WriteAscii(rom, 0x100, "FLASH".Length, "FLASH");
        WriteAscii(rom, 0x180, "SRAM".Length, "SRAM");

        var cartridge = Cartridge.Load(rom);

        Assert.Equal(SaveType.None, cartridge.SaveType);
    }

    private static byte[] CreateMinimalRom()
    {
        var rom = new byte[Cartridge.HeaderLength];
        rom[0] = 0x2E;
        rom[1] = 0x00;
        rom[2] = 0x00;
        rom[3] = 0xEA;
        WriteAscii(rom, Cartridge.TitleOffset, 12, "TEST GAME");
        WriteAscii(rom, Cartridge.GameCodeOffset, 4, "TGME");
        WriteAscii(rom, Cartridge.MakerCodeOffset, 2, "01");
        rom[Cartridge.FixedValueOffset] = 0x96;
        rom[Cartridge.ComplementCheckOffset] = CartridgeHeader.ComputeComplementCheck(rom);
        return rom;
    }

    private static void WriteAscii(byte[] target, int offset, int length, string value)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, target, offset, Math.Min(length, bytes.Length));
    }
}
