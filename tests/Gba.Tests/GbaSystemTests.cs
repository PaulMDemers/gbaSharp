using Gba.Core;
using Gba.Core.Cartridges;
using Gba.Core.Memory;
using Gba.Core.Video;

namespace Gba.Tests;

public sealed class GbaSystemTests
{
    [Fact]
    public void NoBiosCartridgeLoadStartsAtBiosHandoffState()
    {
        var rom = new byte[Cartridge.HeaderLength + 4];
        rom[Cartridge.FixedValueOffset] = 0x96;
        var gba = new GbaSystem();

        gba.LoadCartridge(Cartridge.Load(rom));

        Assert.Equal(GbaMemoryMap.RomEntryPoint, gba.Cpu.Pc);
        Assert.Equal(1, gba.Bus.PostFlag);
        Assert.Equal(0x4317, gba.Bus.PeekIo16(IoRegisters.WAITCNT));
        Assert.Equal(0x7E, gba.Bus.VerticalCount);

        gba.Scheduler.Advance(116);

        Assert.Equal(0, gba.Bus.DisplayStatus & IoRegisters.DispstatHBlank);

        gba.Scheduler.Advance(1);

        Assert.Equal(IoRegisters.DispstatHBlank, gba.Bus.DisplayStatus & IoRegisters.DispstatHBlank);
        Assert.Equal(
            VideoController.CyclesPerScanline * (VideoController.VisibleLines - 0x7E) - VideoController.HDrawCycles,
            gba.Video.CyclesUntilNextVBlankStart);
    }
}
