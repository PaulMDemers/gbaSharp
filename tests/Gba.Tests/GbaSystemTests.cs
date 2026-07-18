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

    [Fact]
    public void StopFreezesHardwareUntilSupportedEnabledInterruptIsRequested()
    {
        var rom = new byte[Cartridge.HeaderLength + 8];
        rom[Cartridge.FixedValueOffset] = 0x96;
        Write32(rom, 0, 0xEF03_0000); // swi Stop
        Write32(rom, 4, 0xE3A0_0001); // mov r0, #1
        var gba = new GbaSystem();
        gba.LoadCartridge(Cartridge.Load(rom));
        gba.Bus.InterruptEnable = (ushort)(IoRegisters.InterruptVBlank | IoRegisters.InterruptKeypad);
        gba.Bus.InterruptMasterEnable = false;

        gba.Step();
        var stoppedCycle = gba.Scheduler.Now;
        var stoppedLine = gba.Bus.VerticalCount;

        Assert.True(gba.Cpu.IsStopped);

        gba.Bus.RequestInterrupt(IoRegisters.InterruptVBlank);
        gba.Step();

        Assert.True(gba.Cpu.IsStopped);
        Assert.Equal(stoppedCycle, gba.Scheduler.Now);
        Assert.Equal(stoppedLine, gba.Bus.VerticalCount);
        Assert.Equal(GbaMemoryMap.RomEntryPoint + 4, gba.Cpu.Pc);

        gba.Bus.RequestInterrupt(IoRegisters.InterruptKeypad);
        gba.Step();

        Assert.False(gba.Cpu.IsStopped);
        Assert.Equal(1u, gba.Cpu[0]);
        Assert.True(gba.Scheduler.Now > stoppedCycle);
    }

    [Fact]
    public void HaltKeepsHardwareRunningWhileCpuWaits()
    {
        var rom = new byte[Cartridge.HeaderLength + 8];
        rom[Cartridge.FixedValueOffset] = 0x96;
        Write32(rom, 0, 0xEF02_0000); // swi Halt
        Write32(rom, 4, 0xE3A0_0001); // mov r0, #1
        var gba = new GbaSystem();
        gba.LoadCartridge(Cartridge.Load(rom));
        gba.Bus.InterruptEnable = IoRegisters.InterruptVBlank;

        gba.Step();
        var haltedCycle = gba.Scheduler.Now;
        var haltedPc = gba.Cpu.Pc;
        gba.Step();

        Assert.True(gba.Cpu.IsHalted);
        Assert.True(gba.Scheduler.Now > haltedCycle);
        Assert.Equal(haltedPc, gba.Cpu.Pc);
        Assert.Equal(0u, gba.Cpu[0]);
    }

    private static void Write32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
