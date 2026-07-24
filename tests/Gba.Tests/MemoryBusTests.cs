using Gba.Core;
using Gba.Core.Cartridges;
using Gba.Core.Memory;

namespace Gba.Tests;

public sealed class MemoryBusTests
{
    [Fact]
    public void HaltControlRequestsPowerDownOnlyFromPostBootBiosContext()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        bios[0] = 1;
        var bus = new MemoryBus(bios);
        var requests = new List<bool>();
        bus.PowerDownRequested += requests.Add;
        bus.PostFlag = 1;

        bus.Write8(IoRegisters.HALTCNT, 0x00);
        Assert.Empty(requests);

        bus.SetBiosAccessible(true);
        bus.Write8(IoRegisters.HALTCNT, 0x00);
        bus.Write8(IoRegisters.HALTCNT, 0x80);

        Assert.Equal([false, true], requests);
    }

    [Fact]
    public void HaltControlIgnoresBiosWriteBeforePostBootFlag()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        bios[0] = 1;
        var bus = new MemoryBus(bios);
        var requested = false;
        bus.PowerDownRequested += _ => requested = true;
        bus.SetBiosAccessible(true);

        bus.Write8(IoRegisters.HALTCNT, 0x00);

        Assert.False(requested);
    }

    [Fact]
    public void EwramMirrorsAcrossRegion()
    {
        var bus = new MemoryBus();

        bus.Write32(0x0200_0000, 0x1234_5678);

        Assert.Equal(0x1234_5678u, bus.Read32(0x0204_0000));
    }

    [Fact]
    public void IwramMirrorsAcrossRegion()
    {
        var bus = new MemoryBus();

        bus.Write16(0x0300_0002, 0xBEEF);

        Assert.Equal(0xBEEF, bus.Read16(0x0300_8002));
    }

    [Fact]
    public void RegisterRamResetPreservesBiosIwramWorkArea()
    {
        var bus = new MemoryBus();
        bus.Write32(0x0300_7DFC, 0x1111_2222);
        bus.Write32(0x0300_7E00, 0x3333_4444);
        bus.Write32(0x0300_7FFC, 0x0300_021C);

        bus.RegisterRamReset(1u << 1);

        Assert.Equal(0u, bus.Read32(0x0300_7DFC));
        Assert.Equal(0x3333_4444u, bus.Read32(0x0300_7E00));
        Assert.Equal(0x0300_021Cu, bus.Read32(0x0300_7FFC));
    }

    [Fact]
    public void VramMirrorsUpperHoleToObjBlock()
    {
        var bus = new MemoryBus();

        bus.Write16(0x0601_0000, 0x0042);

        Assert.Equal(0x42, bus.Read8(0x0601_8000));
    }

    [Fact]
    public void IoRegisterMirrorAt04000800UsesDispcntInsteadOfThrowing()
    {
        var bus = new MemoryBus();

        bus.Write16(0x0400_0800, 0x1234);

        Assert.Equal(0x1234, bus.PeekIo16(IoRegisters.DISPCNT));
        Assert.Equal(0x1234, bus.Read16(0x0400_0800));
    }

    [Fact]
    public void UnalignedWordReadRotatesAlignedValue()
    {
        var bus = new MemoryBus();

        bus.Write32(0x0200_0000, 0x1122_3344);

        Assert.Equal(0x4411_2233u, bus.Read32(0x0200_0001));
        Assert.Equal(0x3344_1122u, bus.Read32(0x0200_0002));
        Assert.Equal(0x2233_4411u, bus.Read32(0x0200_0003));
    }

    [Fact]
    public void RomReadsFromCartridgeAndIgnoresWrites()
    {
        var rom = new byte[Cartridge.HeaderLength + 8];
        rom[Cartridge.FixedValueOffset] = 0x96;
        rom[Cartridge.HeaderLength] = 0xAA;
        rom[Cartridge.HeaderLength + 1] = 0xBB;
        rom[0xC4] = 0xCC;
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);

        bus.Write8(0x0800_00C0, 0x11);
        bus.Write8(0x0800_00C4, 0x22);

        Assert.Equal(0xAA, bus.Read8(0x0800_00C0));
        Assert.Equal(0xBB, bus.Read8(0x0800_00C1));
        Assert.Equal(0xCC, bus.Read8(0x0800_00C4));
    }

    [Fact]
    public void RomReadsPastLoadedImageReturnOpenBusInsteadOfWrappingToHeader()
    {
        var rom = new byte[Cartridge.HeaderLength + 8];
        rom[0] = 0x2E;
        rom[1] = 0x00;
        rom[Cartridge.FixedValueOffset] = 0x96;
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);

        Assert.Equal(0x002E, bus.Read16(0x0800_0000));
        Assert.Equal(0xFFFF, bus.Read16(0x0D00_0000));
    }

    [Fact]
    public void NoBiosExternalBiosReadsReturnZeroUntilBiosOpenBusIsSeeded()
    {
        var bus = new MemoryBus();

        bus.SetOpenBus(0x1234_5678);

        Assert.Equal(0x00, bus.Read8(0x0000_0000));
        Assert.Equal(0x00, bus.Read8(0x0000_0001));
        Assert.Equal(0u, bus.Read32(0x0000_0090));
    }

    [Fact]
    public void NoBiosExternalBiosReadsReturnSeededBiosOpenBus()
    {
        var bus = new MemoryBus();

        bus.SetBiosOpenBus(0x1234_5678);

        Assert.Equal(0x78, bus.Read8(0x0000_0000));
        Assert.Equal(0x56, bus.Read8(0x0000_0001));
        Assert.Equal(0x1234_5678u, bus.Read32(0x0000_0090));
    }

    [Fact]
    public void NoBiosCartridgeLoadKeepsZeroPostStartupBiosOpenBus()
    {
        var rom = new byte[Cartridge.HeaderLength + 4];
        rom[0] = 0x2E;
        rom[1] = 0x00;
        rom[Cartridge.FixedValueOffset] = 0x96;
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();

        bus.LoadCartridge(cartridge);

        Assert.Equal(0x00, bus.Read8(0x0000_00C0));
        Assert.Equal(0x00, bus.Read8(0x0000_00C1));
        Assert.Equal(0u, bus.Read32(0x0000_0090));
    }

    [Theory]
    [InlineData("A2LE")]
    [InlineData("AMRE")]
    public void GuardedNoBiosBiosByteProbeReturnsNonZeroPostStartupValue(string gameCode)
    {
        var bus = new MemoryBus();

        bus.LoadCartridge(Cartridge.Load(CreateRom(gameCode)));

        Assert.Equal(0xE1, bus.Read8(0x0000_00C3));
        Assert.Equal(0u, bus.Read32(0x0000_0090));
    }

    [Theory]
    [InlineData("ATOE")]
    [InlineData("ATOJ")]
    public void TacticsOgreNoBiosBiosWordProbeReturnsLockedBiosOpenBus(string gameCode)
    {
        var bus = new MemoryBus();

        bus.LoadCartridge(Cartridge.Load(CreateRom(gameCode)));

        Assert.Equal(0xE510_F004u, bus.Read32(0x0000_0000));
        Assert.Equal(0x04, bus.Read8(0x0000_0000));
        Assert.Equal(0xF0, bus.Read8(0x0000_0001));
        Assert.Equal(0u, bus.Read32(0x0000_0090));
    }

    [Theory]
    [InlineData("APTE")]
    [InlineData("TEST")]
    public void UnguardedNoBiosBiosByteProbeKeepsZeroPostStartupValue(string gameCode)
    {
        var bus = new MemoryBus();

        bus.LoadCartridge(Cartridge.Load(CreateRom(gameCode)));

        Assert.Equal(0x00, bus.Read8(0x0000_00C3));
        Assert.Equal(0u, bus.Read32(0x0000_0090));
    }

    [Fact]
    public void BiosReadsAreLockedOutsideBiosExecution()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        bios[0] = 0xAA;
        bios[1] = 0xBB;
        bios[2] = 0xCC;
        bios[3] = 0xDD;
        var bus = new MemoryBus(bios);

        bus.SetBiosAccessible(true);
        Assert.Equal(0xDDCC_BBAAu, bus.Read32(0));

        bus.SetBiosOpenBus(0x1234_5678);
        bus.SetBiosAccessible(false);

        Assert.Equal(0x1234_5678u, bus.Read32(0));
    }

    [Fact]
    public void SramMirrorsAndIsWritable()
    {
        var rom = new byte[Cartridge.HeaderLength + 32];
        rom[Cartridge.FixedValueOffset] = 0x96;
        WriteAscii(rom, Cartridge.HeaderLength, "SRAM_V");
        var bus = new MemoryBus();
        bus.LoadCartridge(Cartridge.Load(rom));

        bus.Write8(0x0E00_0000, 0x5A);

        Assert.Equal(0x5A, bus.Read8(0x0E01_0000));
    }

    [Fact]
    public void SramWideReadsRepeatTheAddressedByte()
    {
        var rom = new byte[Cartridge.HeaderLength + 32];
        rom[Cartridge.FixedValueOffset] = 0x96;
        WriteAscii(rom, Cartridge.HeaderLength, "SRAM_V");
        var bus = new MemoryBus();
        bus.LoadCartridge(Cartridge.Load(rom));
        bus.Write8(GbaMemoryMap.GamePakSramStart + 1, 0x5A);

        Assert.Equal(0x5A5A, bus.Read16(GbaMemoryMap.GamePakSramStart + 1));
        Assert.Equal(0x5A5A_5A5Au, bus.Read32(GbaMemoryMap.GamePakSramStart + 1));
    }

    [Fact]
    public void SramWideWritesUseOnlyTheAddressSelectedByteLane()
    {
        var rom = new byte[Cartridge.HeaderLength + 32];
        rom[Cartridge.FixedValueOffset] = 0x96;
        WriteAscii(rom, Cartridge.HeaderLength, "SRAM_V");
        var bus = new MemoryBus();
        bus.LoadCartridge(Cartridge.Load(rom));

        bus.Write16(GbaMemoryMap.GamePakSramStart + 1, 0xAABB);
        bus.Write32(GbaMemoryMap.GamePakSramStart + 6, 0x1122_3344);

        Assert.Equal(0xAA, bus.Read8(GbaMemoryMap.GamePakSramStart + 1));
        Assert.Equal(0x22, bus.Read8(GbaMemoryMap.GamePakSramStart + 6));
        Assert.Equal(0xFF, bus.Read8(GbaMemoryMap.GamePakSramStart + 2));
        Assert.Equal(0xFF, bus.Read8(GbaMemoryMap.GamePakSramStart + 7));
    }

    [Fact]
    public void MissingSaveDeviceReturnsErasedBusAndIgnoresWrites()
    {
        var rom = new byte[Cartridge.HeaderLength + 4];
        rom[Cartridge.FixedValueOffset] = 0x96;
        var bus = new MemoryBus();
        bus.LoadCartridge(Cartridge.Load(rom));

        bus.Write8(GbaMemoryMap.GamePakSramStart, 0x12);
        bus.Write32(GbaMemoryMap.GamePakSramStart + 1, 0x1234_5678);

        Assert.Equal(0xFF, bus.Read8(GbaMemoryMap.GamePakSramStart));
        Assert.Equal(0xFFFF, bus.Read16(GbaMemoryMap.GamePakSramStart));
        Assert.Equal(0xFFFF_FFFFu, bus.Read32(GbaMemoryMap.GamePakSramStart + 1));
    }

    [Fact]
    public void FreshSaveMemoryStartsErased()
    {
        var eepromRom = new byte[Cartridge.HeaderLength + 32];
        eepromRom[Cartridge.FixedValueOffset] = 0x96;
        "EEPROM_V".Select(c => (byte)c).ToArray().CopyTo(eepromRom, Cartridge.HeaderLength);
        var eepromBus = new MemoryBus();
        eepromBus.LoadCartridge(Cartridge.Load(eepromRom));

        eepromBus.Write16(0x0D00_0000, 1);
        eepromBus.Write16(0x0D00_0000, 1);
        for (var i = 0; i < 6 + 1; i++)
        {
            eepromBus.Write16(0x0D00_0000, 0);
        }

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, eepromBus.Read16(0x0D00_0000) & 1);
        }

        for (var i = 0; i < 64; i++)
        {
            Assert.Equal(1, eepromBus.Read16(0x0D00_0000) & 1);
        }

        var sramRom = new byte[Cartridge.HeaderLength + 32];
        sramRom[Cartridge.FixedValueOffset] = 0x96;
        "SRAM_V".Select(c => (byte)c).ToArray().CopyTo(sramRom, Cartridge.HeaderLength);
        var sramBus = new MemoryBus();
        sramBus.LoadCartridge(Cartridge.Load(sramRom));

        Assert.Equal(0xFF, sramBus.Read8(0x0E00_0000));
    }

    [Fact]
    public void MemoryReadObserverReceivesNonIoReads()
    {
        var bus = new MemoryBus();
        uint? observedAddress = null;
        int? observedBytes = null;
        uint? observedValue = null;

        bus.Write8(GbaMemoryMap.EwramStart, 0x42);
        bus.AddMemoryReadObserver((address, bytes, value) =>
        {
            observedAddress = address;
            observedBytes = bytes;
            observedValue = value;
        });

        var value = bus.Read8(GbaMemoryMap.EwramStart);

        Assert.Equal(0x42, value);
        Assert.Equal(GbaMemoryMap.EwramStart, observedAddress);
        Assert.Equal(1, observedBytes);
        Assert.Equal(0x42u, observedValue);
    }

    [Fact]
    public void IoReadsDoNotTriggerMemoryReadObserver()
    {
        var bus = new MemoryBus();
        var readCount = 0;
        bus.AddMemoryReadObserver((_, _, _) => readCount++);

        bus.Read16(IoRegisters.KEYINPUT);

        Assert.Equal(0, readCount);
    }

    [Fact]
    public void FlashSaveSupportsIdModeAndByteProgram()
    {
        var rom = new byte[Cartridge.HeaderLength + 0x100];
        WriteAscii(rom, 0x100, "FLASH1M_V103");
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);

        bus.Write8(0x0E00_5555, 0xAA);
        bus.Write8(0x0E00_2AAA, 0x55);
        bus.Write8(0x0E00_5555, 0x90);

        Assert.Equal(0x62, bus.Read8(0x0E00_0000));
        Assert.Equal(0x13, bus.Read8(0x0E00_0001));

        bus.Write8(0x0E00_0000, 0xF0);
        bus.Write8(0x0E00_5555, 0xAA);
        bus.Write8(0x0E00_2AAA, 0x55);
        bus.Write8(0x0E00_5555, 0xA0);
        bus.Write8(0x0E00_1234, 0x42);

        Assert.Equal(0x42, bus.Read8(0x0E00_1234));
    }

    [Fact]
    public void Flash128KSaveDataCanBeExportedAndLoaded()
    {
        var rom = new byte[Cartridge.HeaderLength + 0x100];
        WriteAscii(rom, 0x100, "FLASH1M_V103");
        var cartridge = Cartridge.Load(rom);
        var source = new MemoryBus();
        source.LoadCartridge(cartridge);

        source.Write8(0x0E00_5555, 0xAA);
        source.Write8(0x0E00_2AAA, 0x55);
        source.Write8(0x0E00_5555, 0xB0);
        source.Write8(0x0E00_0000, 1);
        source.Write8(0x0E00_5555, 0xAA);
        source.Write8(0x0E00_2AAA, 0x55);
        source.Write8(0x0E00_5555, 0xA0);
        source.Write8(0x0E00_2345, 0x77);

        var target = new MemoryBus();
        target.LoadCartridge(cartridge);
        target.LoadSaveData(source.ExportSaveData());
        target.Write8(0x0E00_5555, 0xAA);
        target.Write8(0x0E00_2AAA, 0x55);
        target.Write8(0x0E00_5555, 0xB0);
        target.Write8(0x0E00_0000, 1);

        Assert.Equal(GbaMemoryMap.SramSize, source.SaveDataSize);
        Assert.Equal(0x77, target.Read8(0x0E00_2345));
    }

    [Fact]
    public void RegisterRamResetDoesNotClearGamePakFlash()
    {
        var rom = new byte[Cartridge.HeaderLength + 0x100];
        WriteAscii(rom, 0x100, "FLASH1M_V103");
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);

        bus.RegisterRamReset(1u << 5);

        Assert.Equal(0xFF, bus.Read8(0x0E00_0000));
    }

    [Fact]
    public void CartridgeGpioRegistersSupportReadWriteMode()
    {
        var rom = new byte[Cartridge.HeaderLength + 0x100];
        rom[Cartridge.FixedValueOffset] = 0x96;
        WriteAscii(rom, 0xD0, "SIIRTC_V001");
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);

        bus.Write16(0x0800_00C8, 1);
        bus.Write16(0x0800_00C6, 0xF);
        bus.Write16(0x0800_00C4, 0x5);

        Assert.Equal(1, bus.Read16(0x0800_00C8));
        Assert.Equal(0xF, bus.Read16(0x0800_00C6));
        Assert.Equal(0x5, bus.Read16(0x0800_00C4) & 0xF);
    }

    [Fact]
    public void BoktaiSolarSensorDrivesGpioFlag()
    {
        var rom = CreateRom("U3IE");
        WriteAscii(rom, 0xD0, "SIIRTC_V001");
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);
        bus.SetSolarSensorLevel(2);

        bus.Write16(0x0800_00C8, 1);
        bus.Write16(0x0800_00C6, 0x3);
        bus.Write16(0x0800_00C4, 0x2);
        bus.Write16(0x0800_00C4, 0x0);
        Assert.Equal(0, bus.Read16(0x0800_00C4) & 0x8);

        bus.Write16(0x0800_00C4, 0x1);
        bus.Write16(0x0800_00C4, 0x0);
        bus.Write16(0x0800_00C4, 0x1);

        Assert.Equal(0x8, bus.Read16(0x0800_00C4) & 0x8);
    }

    [Fact]
    public void WarioWareGyroStreamsNeutralValueAndTracksRumble()
    {
        var cartridge = Cartridge.Load(CreateRom("RZWE"));
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);
        bus.SetGyroSensorValue(0x06C0);

        bus.Write16(0x0800_00C8, 1);
        bus.Write16(0x0800_00C6, 0xB);
        bus.Write16(0x0800_00C4, 0x9);
        Assert.True(bus.CartridgeRumbleEnabled);

        var bits = 0;
        for (var i = 0; i < 16; i++)
        {
            bits = (bits << 1) | ((bus.Read16(0x0800_00C4) >> 2) & 1);
            bus.Write16(0x0800_00C4, 0xB);
            bus.Write16(0x0800_00C4, 0x9);
        }

        Assert.Equal(0x06C0, bits);

        bus.Write16(0x0800_00C4, 0x1);
        Assert.False(bus.CartridgeRumbleEnabled);
    }

    [Fact]
    public void YoshiTiltSensorExposesReadyAxesInSramRange()
    {
        var cartridge = Cartridge.Load(CreateRom("KYGJ"));
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);
        bus.SetTiltSensor(0x0123, 0x0ABC);

        bus.Write8(0x0E00_8000, 0x55);
        bus.Write8(0x0E00_8100, 0xAA);

        Assert.Equal(0x23, bus.Read8(0x0E00_8200));
        Assert.Equal(0x81, bus.Read8(0x0E00_8300));
        Assert.Equal(0xBC, bus.Read8(0x0E00_8400));
        Assert.Equal(0x8A, bus.Read8(0x0E00_8500));
    }

    [Fact]
    public void ByteWritesToPaletteMirrorAcrossHalfword()
    {
        var bus = new MemoryBus();

        bus.Write8(GbaMemoryMap.PaletteStart + 1, 0x3A);

        Assert.Equal(0x3A3A, bus.Read16(GbaMemoryMap.PaletteStart));
    }

    [Fact]
    public void ByteWritesToBackgroundVramMirrorAcrossHalfword()
    {
        var bus = new MemoryBus();

        bus.Write8(GbaMemoryMap.VramStart + 1, 0xAA);

        Assert.Equal(0xAAAA, bus.Read16(GbaMemoryMap.VramStart));
    }

    [Fact]
    public void ByteWritesToObjectVramAndOamAreIgnored()
    {
        var bus = new MemoryBus();
        bus.Write16(GbaMemoryMap.VramStart + 0x10000, 0x1234);
        bus.Write16(GbaMemoryMap.VramStart + 0x14000, 0x9ABC);
        bus.Write16(GbaMemoryMap.OamStart, 0x5678);

        bus.Write8(GbaMemoryMap.VramStart + 0x10000, 0xAA);
        bus.Write16(IoRegisters.DISPCNT, 3);
        bus.Write8(GbaMemoryMap.VramStart + 0x14000, 0xCC);
        bus.Write8(GbaMemoryMap.OamStart + 1, 0xBB);

        Assert.Equal(0x1234, bus.Read16(GbaMemoryMap.VramStart + 0x10000));
        Assert.Equal(0x9ABC, bus.Read16(GbaMemoryMap.VramStart + 0x14000));
        Assert.Equal(0x5678, bus.Read16(GbaMemoryMap.OamStart));
    }

    [Fact]
    public void EepromSaveWritesAndReadsSerialData()
    {
        var rom = new byte[Cartridge.HeaderLength + 0x100];
        WriteAscii(rom, 0x100, "EEPROM_V124");
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);

        WriteBits(bus, 0b10, 2);
        WriteBits(bus, 3, 6);
        WriteBits(bus, 0x0123_4567_89AB_CDEFul, 64);
        WriteBits(bus, 0, 1);

        WriteBits(bus, 0b11, 2);
        WriteBits(bus, 3, 6);
        WriteBits(bus, 0, 1);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, bus.Read16(0x0D00_0000) & 1);
        }

        ulong read = 0;
        for (var i = 0; i < 64; i++)
        {
            read = (read << 1) | (uint)(bus.Read16(0x0D00_0000) & 1);
        }

        Assert.Equal(0x0123_4567_89AB_CDEFul, read);
    }

    [Fact]
    public void EepromSaveDataCanBeExportedAndLoaded()
    {
        var rom = new byte[Cartridge.HeaderLength + 0x100];
        WriteAscii(rom, 0x100, "EEPROM_V124");
        var cartridge = Cartridge.Load(rom);
        var source = new MemoryBus();
        source.LoadCartridge(cartridge);

        WriteBits(source, 0b10, 2);
        WriteBits(source, 5, 6);
        WriteBits(source, 0x1122_3344_5566_7788ul, 64);
        WriteBits(source, 0, 1);

        var target = new MemoryBus();
        target.LoadCartridge(cartridge);
        target.LoadSaveData(source.ExportSaveData());
        WriteBits(target, 0b11, 2);
        WriteBits(target, 5, 6);
        WriteBits(target, 0, 1);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, target.Read16(0x0D00_0000) & 1);
        }

        ulong read = 0;
        for (var i = 0; i < 64; i++)
        {
            read = (read << 1) | (uint)(target.Read16(0x0D00_0000) & 1);
        }

        Assert.Equal(0x1122_3344_5566_7788ul, read);
    }

    [Fact]
    public void EepromAtSixteenMiBRomUsesFourteenBitAddresses()
    {
        var rom = new byte[16 * 1024 * 1024];
        WriteAscii(rom, 0x100, "EEPROM_V124");
        var cartridge = Cartridge.Load(rom);
        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);

        WriteBits(bus, 0b10, 2);
        WriteBits(bus, 0x1234, 14);
        WriteBits(bus, 0xA5A5_0123_4567_F00Dul, 64);
        WriteBits(bus, 0, 1);

        WriteBits(bus, 0b11, 2);
        WriteBits(bus, 0x1234, 14);
        WriteBits(bus, 0, 1);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, bus.Read16(0x0D00_0000) & 1);
        }

        ulong read = 0;
        for (var i = 0; i < 64; i++)
        {
            read = (read << 1) | (uint)(bus.Read16(0x0D00_0000) & 1);
        }

        Assert.Equal(0xA5A5_0123_4567_F00Dul, read);
    }

    [Fact]
    public void IoRegisterPropertiesReadAndWriteMappedRegisters()
    {
        var bus = new MemoryBus
        {
            DisplayControl = 0x0403,
            DisplayStatus = 0x0008,
            VerticalCount = 123,
            InterruptEnable = 0x0001,
            InterruptFlags = 0x0002,
            InterruptMasterEnable = true
        };

        Assert.Equal(0x0403, bus.Read16(IoRegisters.DISPCNT));
        Assert.Equal(0x0008, bus.Read16(IoRegisters.DISPSTAT));
        Assert.Equal(123, bus.Read16(IoRegisters.VCOUNT));
        Assert.Equal(0x0001, bus.Read16(IoRegisters.IE));
        Assert.Equal(0x0002, bus.Read16(IoRegisters.IF));
        Assert.True(bus.InterruptMasterEnable);
    }

    [Fact]
    public void CartridgeLoadInitializesIoPowerOnDefaults()
    {
        var rom = new byte[Cartridge.HeaderLength + 4];
        rom[Cartridge.FixedValueOffset] = 0x96;
        var bus = new MemoryBus();

        bus.Write16(IoRegisters.RCNT, 0);
        bus.Write16(IoRegisters.SOUNDBIAS, 0);
        bus.Write16(IoRegisters.BG2PA, 0);
        bus.LoadCartridge(Cartridge.Load(rom));

        Assert.Equal(0x0080, bus.Read16(IoRegisters.DISPCNT));
        Assert.Equal(0x8000, bus.Read16(IoRegisters.RCNT));
        Assert.Equal(0x03FF, bus.Read16(IoRegisters.KEYINPUT));
        Assert.Equal(0x0200, bus.Read16(IoRegisters.SOUNDBIAS));
        Assert.Equal(0x0100, bus.Read16(IoRegisters.BG2PA));
        Assert.Equal(0x0100, bus.Read16(IoRegisters.BG2PD));
        Assert.Equal(0x0100, bus.Read16(IoRegisters.BG3PA));
        Assert.Equal(0x0100, bus.Read16(IoRegisters.BG3PD));
    }

    [Fact]
    public void WordWriteToInterruptRegistersClearsInterruptFlags()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = 0x00FF
        };
        bus.RequestInterrupt((ushort)(IoRegisters.InterruptVBlank | IoRegisters.InterruptHBlank | IoRegisters.InterruptTimer0));

        bus.Write32(IoRegisters.IE, (uint)(0x0003 << 16) | IoRegisters.InterruptKeypad);

        Assert.Equal(IoRegisters.InterruptKeypad, bus.InterruptEnable);
        Assert.Equal(0, bus.InterruptFlags & (IoRegisters.InterruptVBlank | IoRegisters.InterruptHBlank));
        Assert.Equal(IoRegisters.InterruptTimer0, bus.InterruptFlags & IoRegisters.InterruptTimer0);
    }

    [Fact]
    public void RequestInterruptNotifiesObserversWithRequestedAndLatchedFlags()
    {
        var bus = new MemoryBus();
        var requests = new List<(ushort Requested, ushort Flags)>();
        bus.InterruptRequested += (requested, flags) => requests.Add((requested, flags));

        bus.RequestInterrupt(IoRegisters.InterruptVBlank);
        bus.RequestInterrupt(IoRegisters.InterruptVCount);

        Assert.Equal(
            [
                (IoRegisters.InterruptVBlank, IoRegisters.InterruptVBlank),
                (IoRegisters.InterruptVCount, (ushort)(IoRegisters.InterruptVBlank | IoRegisters.InterruptVCount))
            ],
            requests);
    }

    [Fact]
    public void MultiplayerSerialReadReportsIdleParentReadyState()
    {
        var bus = new MemoryBus();

        bus.Write16(IoRegisters.RCNT, 0);
        bus.Write16(IoRegisters.SIOCNT, 0x6003);

        Assert.Equal(0x600F, bus.Read16(IoRegisters.SIOCNT));
        Assert.Equal(0x0001, bus.Read16(IoRegisters.RCNT) & 0x000F);
    }

    [Fact]
    public void MultiplayerSerialTransferCompletesAfterTransferDelay()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = IoRegisters.InterruptSerial
        };

        bus.Write16(IoRegisters.RCNT, 0);
        bus.Write16(IoRegisters.SIOMLT_SEND, 0x1234);
        bus.Write16(IoRegisters.SIOCNT, 0x6083);

        Assert.Equal(0x608F, bus.Read16(IoRegisters.SIOCNT));
        Assert.Equal(0, bus.InterruptFlags & IoRegisters.InterruptSerial);
        Assert.Equal(0xFFFF, bus.Read16(IoRegisters.SIOMULTI0));
        Assert.Equal(0xFFFF, bus.Read16(IoRegisters.SIOMULTI1));
        Assert.Equal(0xFFFF, bus.Read16(IoRegisters.SIOMULTI2));
        Assert.Equal(0xFFFF, bus.Read16(IoRegisters.SIOMULTI3));

        bus.Advance(31_976);

        Assert.Equal(0x600F, bus.Read16(IoRegisters.SIOCNT));
        Assert.Equal(0, bus.Read16(IoRegisters.SIOMULTI0));
        Assert.Equal(0, bus.Read16(IoRegisters.SIOMULTI1));
        Assert.Equal(0, bus.Read16(IoRegisters.SIOMULTI2));
        Assert.Equal(0, bus.Read16(IoRegisters.SIOMULTI3));
        Assert.Equal(IoRegisters.InterruptSerial, bus.InterruptFlags & IoRegisters.InterruptSerial);
    }

    [Fact]
    public void MultiplayerSerialByteWriteCanStartTransfer()
    {
        var bus = new MemoryBus();

        bus.Write16(IoRegisters.RCNT, 0);
        bus.Write16(IoRegisters.SIOMLT_SEND, 0xCAFE);
        bus.Write16(IoRegisters.SIOCNT, 0x6003);
        bus.Write8(IoRegisters.SIOCNT, 0x83);

        Assert.Equal(0x608F, bus.Read16(IoRegisters.SIOCNT));

        bus.Advance(31_976);

        Assert.Equal(0x600F, bus.Read16(IoRegisters.SIOCNT));
        Assert.Equal(0, bus.Read16(IoRegisters.SIOMULTI0));
        Assert.Equal(0, bus.Read16(IoRegisters.SIOMULTI1));
    }

    [Fact]
    public void RegisterRamResetRestoresSerialRemoteControlDefault()
    {
        var bus = new MemoryBus();

        bus.Write16(IoRegisters.RCNT, 0);
        bus.Write16(IoRegisters.SIOCNT, 0x6003);

        bus.RegisterRamReset(1u << 5);

        Assert.Equal(0x8000, bus.Read16(IoRegisters.RCNT));
        Assert.Equal(0, bus.Read16(IoRegisters.SIOCNT));
    }

    [Fact]
    public void RegisterRamResetRestoresSoundAndOtherIoDefaults()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = 0x3FFF,
            InterruptFlags = 0x3FFF,
            InterruptMasterEnable = true,
            PostFlag = 1
        };
        bus.Write16(IoRegisters.SOUNDCNT_L, 0xFFFF);
        bus.Write16(IoRegisters.SOUNDCNT_H, 0xFFFF);
        bus.Write16(IoRegisters.SOUNDCNT_X, 0xFFFF);
        bus.Write16(IoRegisters.SOUNDBIAS, 0x1234);
        bus.Write16(IoRegisters.DISPCNT, 0x1340);
        bus.Write16(IoRegisters.DISPSTAT, 0x00F8);
        bus.Write16(IoRegisters.BG2PA, 0x0020);
        bus.Write16(IoRegisters.BG2PD, 0x0030);
        bus.Write16(IoRegisters.DMA3CNT_H, 0x8000);
        bus.Write16(IoRegisters.TM0CNT_H, 0x00C3);
        bus.Write16(IoRegisters.KEYCNT, 0xC000);
        bus.Write16(IoRegisters.WAITCNT, 0x4317);

        bus.RegisterRamReset((1u << 6) | (1u << 7));

        Assert.Equal(0, bus.Read16(IoRegisters.SOUNDCNT_L));
        Assert.Equal(0, bus.Read16(IoRegisters.SOUNDCNT_H));
        Assert.Equal(0, bus.Read16(IoRegisters.SOUNDCNT_X));
        Assert.Equal(0x0200, bus.Read16(IoRegisters.SOUNDBIAS));
        Assert.Equal(0x0080, bus.Read16(IoRegisters.DISPCNT));
        Assert.Equal(0, bus.Read16(IoRegisters.DISPSTAT) & 0xFFF8);
        Assert.Equal(0x0100, bus.Read16(IoRegisters.BG2PA));
        Assert.Equal(0x0100, bus.Read16(IoRegisters.BG2PD));
        Assert.Equal(0, bus.Read16(IoRegisters.DMA3CNT_H));
        Assert.Equal(0, bus.Read16(IoRegisters.TM0CNT_H));
        Assert.Equal(0, bus.Read16(IoRegisters.KEYCNT));
        Assert.Equal(0, bus.InterruptEnable);
        Assert.Equal(0, bus.InterruptFlags);
        Assert.False(bus.InterruptMasterEnable);
        Assert.Equal(0, bus.Read16(IoRegisters.WAITCNT));
        Assert.Equal(0, bus.PostFlag);
    }

    [Fact]
    public void RegisterRamResetOtherRegistersCancelsTimerState()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable | IoRegisters.TimerIrq);

        gba.Bus.RegisterRamReset(1u << 7);
        gba.Scheduler.Advance(8);

        Assert.Equal(0, gba.Bus.Read16(IoRegisters.TM0CNT_H));
        Assert.Equal(0, gba.Bus.InterruptFlags & IoRegisters.InterruptTimer0);
    }

    [Fact]
    public void GamePakAccessCyclesFollowWaitControl()
    {
        var bus = new MemoryBus();

        Assert.Equal(4, bus.GetCpuAccessCycles(0x0800_0000, 2, sequential: false));
        Assert.Equal(2, bus.GetCpuAccessCycles(0x0800_0002, 2, sequential: true));
        Assert.Equal(6, bus.GetCpuAccessCycles(0x0800_0000, 4, sequential: false));

        bus.Write16(IoRegisters.WAITCNT, 0x4017);

        Assert.Equal(3, bus.GetCpuAccessCycles(0x0800_0000, 2, sequential: false));
        Assert.Equal(1, bus.GetCpuAccessCycles(0x0800_0002, 2, sequential: true));
        Assert.Equal(4, bus.GetCpuAccessCycles(0x0800_0000, 4, sequential: false));
        Assert.Equal(4, bus.GetCpuAccessCycles(0x0C00_0000, 2, sequential: false));
        Assert.Equal(4, bus.GetCpuAccessCycles(0x0C00_0002, 2, sequential: true));
    }

    [Fact]
    public void RamHalfwordAndWordWritesNotifyObserversOnceAtTransferWidth()
    {
        var bus = new MemoryBus();
        var writes = new List<(uint Address, int Bytes)>();
        bus.AddMemoryWriteObserver((address, bytes) => writes.Add((address, bytes)));

        bus.Write16(GbaMemoryMap.IwramStart + 1, 0x1234);
        bus.Write32(GbaMemoryMap.EwramStart + 3, 0x89AB_CDEF);

        Assert.Equal([(GbaMemoryMap.IwramStart, 2), (GbaMemoryMap.EwramStart, 4)], writes);
    }

    [Fact]
    public void SaveAccessCyclesFollowWaitControl()
    {
        var bus = new MemoryBus();

        Assert.Equal(4, bus.GetCpuAccessCycles(0x0E00_0000, 1, sequential: false));

        bus.Write16(IoRegisters.WAITCNT, 0x0002);

        Assert.Equal(2, bus.GetCpuAccessCycles(0x0E00_0000, 1, sequential: false));

        bus.Write16(IoRegisters.WAITCNT, 0x0003);

        Assert.Equal(8, bus.GetCpuAccessCycles(0x0E00_0000, 1, sequential: true));
    }

    [Fact]
    public void RubySapphireMapMusicStopWaitTreatsPausedBgmTrackMaskAsStopped()
    {
        var bus = new MemoryBus();
        bus.LoadCartridge(Cartridge.Load(CreateRom("AXVE")));

        bus.Write8(0x0300_06D8, 5);
        bus.Write32(0x0300_7384, 0x8000_0001);

        Assert.Equal(0, bus.Read16(0x0300_7384));
        Assert.Equal(0x8000_0000u, bus.Read32(0x0300_7384));
    }

    [Fact]
    public void RubySapphireMplayStatusGuardsStayScopedToExpectedStates()
    {
        var rubyBus = new MemoryBus();
        rubyBus.LoadCartridge(Cartridge.Load(CreateRom("AXVE")));
        rubyBus.Write8(0x0300_06D8, 2);
        rubyBus.Write32(0x0300_7384, 0x8000_0001);

        Assert.Equal(1, rubyBus.Read16(0x0300_7384));

        rubyBus.Write32(0x0300_7384, 0x8000_0000);
        Assert.Equal(1, rubyBus.Read8(0x0300_7384));

        var otherBus = new MemoryBus();
        otherBus.LoadCartridge(Cartridge.Load(CreateRom("TEST")));
        otherBus.Write8(0x0300_06D8, 5);
        otherBus.Write32(0x0300_7384, 0x8000_0001);

        Assert.Equal(1, otherBus.Read16(0x0300_7384));
    }

    private static void WriteAscii(byte[] target, int offset, string value)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, target, offset, bytes.Length);
    }

    private static byte[] CreateRom(string gameCode)
    {
        var rom = new byte[Cartridge.HeaderLength + 0x100];
        rom[Cartridge.FixedValueOffset] = 0x96;
        WriteAscii(rom, Cartridge.GameCodeOffset, gameCode);
        return rom;
    }

    private static void WriteBits(MemoryBus bus, ulong value, int bits)
    {
        for (var bit = bits - 1; bit >= 0; bit--)
        {
            bus.Write16(0x0D00_0000, (ushort)((value >> bit) & 1));
        }
    }
}
