using Gba.Core;
using Gba.Core.Cartridges;
using Gba.Core.Dma;
using Gba.Core.Memory;
using Gba.Core.Video;

namespace Gba.Tests;

public sealed class DmaControllerTests
{
    [Fact]
    public void ImmediateDmaCopiesHalfwordsAndDisablesChannel()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0x1111);
        gba.Bus.Write16(GbaMemoryMap.IwramStart + 2, 0x2222);

        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 2, IoRegisters.DmaEnable);

        Assert.Equal(0x1111, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(0x2222, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x102));
        Assert.Equal(0, gba.Bus.PeekIo16(IoRegisters.DMA0CNT_H) & IoRegisters.DmaEnable);
    }

    [Fact]
    public void ImmediateDmaCopiesWords()
    {
        var gba = new GbaSystem();
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0x1122_3344);

        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 1, IoRegisters.DmaEnable | IoRegisters.DmaWord);

        Assert.Equal(0x1122_3344u, gba.Bus.Read32(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void ImmediateDmaConsumesCpuHaltCycles()
    {
        var gba = new GbaSystem();
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0x1122_3344);
        gba.Bus.Write32(GbaMemoryMap.IwramStart + 4, 0x5566_7788);

        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 2, IoRegisters.DmaEnable | IoRegisters.DmaWord);

        Assert.Equal(4, gba.Dma.ConsumePendingCycles());
        Assert.Equal(0, gba.Dma.ConsumePendingCycles());
    }

    [Fact]
    public void ImmediateWordDmaAlignsSourceAndDestination()
    {
        var gba = new GbaSystem();
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0xE92D_4FF0);

        ConfigureDma0(gba, GbaMemoryMap.IwramStart + 1, GbaMemoryMap.IwramStart + 0x101, 1, IoRegisters.DmaEnable | IoRegisters.DmaWord);

        Assert.Equal(0xE92D_4FF0u, gba.Bus.Read32(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void ImmediateDmaHonorsFixedDestination()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0x1111);
        gba.Bus.Write16(GbaMemoryMap.IwramStart + 2, 0x2222);
        const ushort fixedDestination = 2 << 5;

        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 2, IoRegisters.DmaEnable | fixedDestination);

        Assert.Equal(0x2222, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void VBlankDmaRunsWhenVideoEntersVBlank()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0xCAFE);
        const ushort vblankTiming = 1 << 12;

        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 1, IoRegisters.DmaEnable | vblankTiming);

        Assert.Equal(0, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));

        gba.Scheduler.Advance(VideoController.CyclesPerScanline * VideoController.VisibleLines);

        Assert.Equal(0xCAFE, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void VBlankDmaConsumesCpuHaltCycles()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0xCAFE);
        const ushort vblankTiming = 1 << 12;

        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 1, IoRegisters.DmaEnable | vblankTiming);

        gba.Scheduler.Advance(VideoController.CyclesPerScanline * VideoController.VisibleLines);

        Assert.Equal(2, gba.Dma.ConsumePendingCycles());
        Assert.Equal(0, gba.Dma.ConsumePendingCycles());
    }

    [Fact]
    public void HBlankDmaRunsWhenVideoEntersHBlank()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0xBEEF);
        const ushort hblankTiming = 2 << 12;

        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 1, IoRegisters.DmaEnable | hblankTiming);

        gba.Scheduler.Advance(VideoController.HDrawCycles);

        Assert.Equal(0xBEEF, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void HBlankDmaDoesNotRunDuringVBlank()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0xBEEF);
        const ushort hblankTiming = 2 << 12;

        gba.Scheduler.Advance(VideoController.CyclesPerScanline * VideoController.VisibleLines);
        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 1, IoRegisters.DmaEnable | hblankTiming);

        gba.Scheduler.Advance(VideoController.HDrawCycles);

        Assert.Equal(VideoController.VisibleLines, gba.Bus.VerticalCount);
        Assert.Equal(0, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));

        gba.Scheduler.Advance(
            VideoController.HBlankCycles
            + VideoController.CyclesPerScanline * (VideoController.TotalLines - VideoController.VisibleLines - 1)
            + VideoController.HDrawCycles);

        Assert.Equal(0, gba.Bus.VerticalCount);
        Assert.Equal(0xBEEF, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void DmaRequestsInterruptWhenEnabled()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0x1234);

        ConfigureDma0(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 1, IoRegisters.DmaEnable | IoRegisters.DmaIrq);

        Assert.Equal(IoRegisters.InterruptDma0, gba.Bus.InterruptFlags & IoRegisters.InterruptDma0);
    }

    [Fact]
    public void SoundFifoDmaRunsFourWordsOnSelectedTimerOverflow()
    {
        var gba = new GbaSystem();
        for (uint i = 0; i < 8; i++)
        {
            gba.Bus.Write32(GbaMemoryMap.IwramStart + i * 4, 0x1111_0000u + i);
        }

        const ushort specialTiming = 3 << 12;
        ConfigureDma1(
            gba,
            GbaMemoryMap.IwramStart,
            IoRegisters.FIFO_A,
            0,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);
        Assert.Equal(0x1111_0003u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));

        gba.Scheduler.Advance(1);
        Assert.Equal(0x1111_0007u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));
    }

    [Fact]
    public void SoundFifoResetBitsClearTrackedLevelsAndDoNotRemainLatched()
    {
        var gba = new GbaSystem();
        gba.Bus.Write32(IoRegisters.FIFO_A, 0x1111_2222);
        gba.Bus.Write32(IoRegisters.FIFO_A, 0x3333_4444);
        gba.Bus.Write32(IoRegisters.FIFO_B, 0x5555_6666);

        Assert.Equal(8, gba.Dma.SoundFifoALevel);
        Assert.Equal(4, gba.Dma.SoundFifoBLevel);

        gba.Bus.Write16(IoRegisters.SOUNDCNT_H, (1 << 11) | (1 << 15));

        Assert.Equal(0, gba.Dma.SoundFifoALevel);
        Assert.Equal(0, gba.Dma.SoundFifoBLevel);
        Assert.Equal(0, gba.Bus.PeekIo16(IoRegisters.SOUNDCNT_H) & ((1 << 11) | (1 << 15)));
    }

    [Fact]
    public void RegisterRamResetSoundClearsTrackedFifoLevels()
    {
        var gba = new GbaSystem();
        gba.Bus.Write32(IoRegisters.FIFO_A, 0x1111_2222);
        gba.Bus.Write32(IoRegisters.FIFO_B, 0x3333_4444);

        Assert.Equal(4, gba.Dma.SoundFifoALevel);
        Assert.Equal(4, gba.Dma.SoundFifoBLevel);

        gba.Bus.RegisterRamReset(1u << 6);

        Assert.Equal(0, gba.Dma.SoundFifoALevel);
        Assert.Equal(0, gba.Dma.SoundFifoBLevel);
    }

    [Fact]
    public void SoundFifoDmaRefillsWhenTimerDrainReachesSixteenBytes()
    {
        var gba = new GbaSystem();
        for (uint i = 0; i < 4; i++)
        {
            gba.Bus.Write32(IoRegisters.FIFO_A, 0xAAAA_0000u + i);
        }

        for (uint i = 0; i < 4; i++)
        {
            gba.Bus.Write32(GbaMemoryMap.IwramStart + i * 4, 0xBBBB_0000u + i);
        }

        const ushort specialTiming = 3 << 12;
        const ushort fixedDestination = 2 << 5;
        ConfigureDma1(
            gba,
            GbaMemoryMap.IwramStart,
            IoRegisters.FIFO_A,
            0,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming | fixedDestination);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        Assert.Equal(16, gba.Dma.SoundFifoALevel);

        gba.Scheduler.Advance(1);

        Assert.Equal(31, gba.Dma.SoundFifoALevel);
        Assert.Equal(0xBBBB_0003u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));
    }

    [Fact]
    public void SoundFifoClocksQueuedSignedSamplesInByteOrder()
    {
        var gba = new GbaSystem();
        var samples = new List<sbyte>();
        var detailed = new List<SoundFifoSampleClock>();
        gba.Dma.SoundFifoSampleClocked += (fifo, sample) =>
        {
            Assert.Equal(0, fifo);
            samples.Add(sample);
        };
        gba.Dma.SoundFifoSampleClockedDetailed += detailed.Add;

        gba.Bus.Write32(IoRegisters.FIFO_A, 0x807F_0100);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        for (var i = 0; i < 4; i++)
        {
            gba.Scheduler.Advance(1);
        }

        Assert.Equal([0, 1, 127, -128], samples.Select(sample => (int)sample));
        Assert.Equal([1, 2, 3, 4], detailed.Select(sample => (int)sample.Cycle));
        Assert.All(detailed, sample =>
        {
            Assert.Equal(0, sample.Fifo);
            Assert.Equal(0, sample.Timer);
        });
        Assert.Equal(0, gba.Dma.SoundFifoALevel);
    }

    [Fact]
    public void SoundFifoDmaRefillQueuesSamplesForLaterTimerClocks()
    {
        var gba = new GbaSystem();
        for (uint i = 0; i < 4; i++)
        {
            gba.Bus.Write32(GbaMemoryMap.IwramStart + i * 4, 0x0302_0100u + i * 0x0404_0404u);
        }

        var samples = new List<int>();
        gba.Dma.SoundFifoSampleClocked += (_, sample) => samples.Add(sample);

        const ushort specialTiming = 3 << 12;
        const ushort fixedDestination = 2 << 5;
        ConfigureDma1(
            gba,
            GbaMemoryMap.IwramStart,
            IoRegisters.FIFO_A,
            0,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming | fixedDestination);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);
        for (var i = 0; i < 4; i++)
        {
            gba.Scheduler.Advance(1);
        }

        Assert.Equal([0, 1, 2, 3], samples);
        Assert.Equal(28, gba.Dma.SoundFifoALevel);
    }

    [Fact]
    public void SoundFifoDmaUsesEachFifoSelectedTimer()
    {
        var gba = new GbaSystem();
        for (uint i = 0; i < 4; i++)
        {
            gba.Bus.Write32(GbaMemoryMap.IwramStart + i * 4, 0xAAAA_0000u + i);
            gba.Bus.Write32(GbaMemoryMap.IwramStart + 0x100 + i * 4, 0xBBBB_0000u + i);
        }

        const ushort specialTiming = 3 << 12;
        const ushort fixedDestination = 2 << 5;
        ConfigureDma1(
            gba,
            GbaMemoryMap.IwramStart,
            IoRegisters.FIFO_A,
            0,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming | fixedDestination);
        ConfigureDma2(
            gba,
            GbaMemoryMap.IwramStart + 0x100,
            IoRegisters.FIFO_B,
            0,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming | fixedDestination);

        gba.Bus.Write16(IoRegisters.SOUNDCNT_H, 1 << 10); // FIFO A uses timer 1, FIFO B uses timer 0.
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);

        Assert.Equal(0u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));
        Assert.Equal(0xBBBB_0003u, gba.Bus.PeekIo32(IoRegisters.FIFO_B));

        gba.Bus.Write16(IoRegisters.TM1CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM1CNT_H, IoRegisters.TimerEnable);
        gba.Scheduler.Advance(1);

        Assert.Equal(0xAAAA_0003u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));
    }

    [Fact]
    public void SoundFifoDmaForcesWordWidthAndFourWordCount()
    {
        var gba = new GbaSystem();
        for (uint i = 0; i < 4; i++)
        {
            gba.Bus.Write32(GbaMemoryMap.IwramStart + i * 4, 0xCCCC_0000u + i);
        }

        const ushort specialTiming = 3 << 12;
        const ushort fixedDestination = 2 << 5;
        ConfigureDma1(
            gba,
            GbaMemoryMap.IwramStart,
            IoRegisters.FIFO_A,
            1,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | specialTiming | fixedDestination);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);

        Assert.Equal(0xCCCC_0003u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));
        Assert.Equal(16, gba.Dma.SoundFifoALevel);
        Assert.NotEqual(0, gba.Bus.PeekIo16(IoRegisters.DMA1CNT_H) & IoRegisters.DmaEnable);
    }

    [Fact]
    public void Dma3SpecialRunsOnDisplayStartWindow()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0x1357);
        const ushort specialTiming = 3 << 12;

        ConfigureDma3(gba, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramStart + 0x100, 1, IoRegisters.DmaEnable | specialTiming);

        gba.Scheduler.Advance(VideoController.CyclesPerScanline + VideoController.HDrawCycles);

        Assert.Equal(1, gba.Bus.VerticalCount);
        Assert.Equal(0, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));

        gba.Scheduler.Advance(VideoController.HBlankCycles + VideoController.HDrawCycles);

        Assert.Equal(2, gba.Bus.VerticalCount);
        Assert.Equal(0x1357, gba.Bus.Read16(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void RepeatingDma3SpecialDisablesAfterLastDisplayStartLine()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(GbaMemoryMap.IwramStart, 0x2468);
        const ushort specialTiming = 3 << 12;

        ConfigureDma3(
            gba,
            GbaMemoryMap.IwramStart,
            GbaMemoryMap.IwramStart + 0x100,
            1,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | specialTiming);

        gba.Scheduler.Advance(VideoController.CyclesPerScanline * (VideoController.VisibleLines + 1) + VideoController.HDrawCycles);

        Assert.Equal(VideoController.VisibleLines + 1, gba.Bus.VerticalCount);
        Assert.Equal(0, gba.Bus.PeekIo16(IoRegisters.DMA3CNT_H) & IoRegisters.DmaEnable);
    }

    [Fact]
    public void ImmediateDmaDoesNotRestartWhenRewritingEnabledSpecialChannel()
    {
        var gba = new GbaSystem();
        for (uint i = 0; i < 8; i++)
        {
            gba.Bus.Write32(GbaMemoryMap.IwramStart + i * 4, 0x2222_0000u + i);
        }

        const ushort specialTiming = 3 << 12;
        const ushort fixedDestination = 2 << 5;
        ConfigureDma1(
            gba,
            GbaMemoryMap.IwramStart,
            IoRegisters.FIFO_A,
            0,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming | fixedDestination);

        gba.Bus.Write32(
            IoRegisters.DMA1CNT_L,
            ((uint)(IoRegisters.DmaEnable | IoRegisters.DmaWord | fixedDestination) << 16) | 4);

        Assert.Equal(0u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));
        Assert.NotEqual(0, gba.Bus.PeekIo16(IoRegisters.DMA1CNT_H) & IoRegisters.DmaEnable);
    }

    [Fact]
    public void ImmediateDmaReenableReloadsInitialRegisterAddresses()
    {
        var gba = new GbaSystem();
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0x1111_2222);
        gba.Bus.Write32(GbaMemoryMap.IwramStart + 4, 0x3333_4444);

        ConfigureDma1(
            gba,
            GbaMemoryMap.IwramStart,
            GbaMemoryMap.IwramStart + 0x100,
            1,
            IoRegisters.DmaEnable | IoRegisters.DmaWord);
        gba.Bus.Write32(
            IoRegisters.DMA1CNT_L,
            ((uint)(IoRegisters.DmaEnable | IoRegisters.DmaWord) << 16) | 1);

        Assert.Equal(0x1111_2222u, gba.Bus.Read32(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(0u, gba.Bus.Read32(GbaMemoryMap.IwramStart + 0x104));
    }

    [Fact]
    public void SoundFifoDmaReenableReloadsInitialSourceAddress()
    {
        var gba = new GbaSystem();
        for (uint i = 0; i < 8; i++)
        {
            gba.Bus.Write32(GbaMemoryMap.IwramStart + i * 4, 0x4444_0000u + i);
        }

        const ushort specialTiming = 3 << 12;
        const ushort fixedDestination = 2 << 5;
        ConfigureDma1(
            gba,
            GbaMemoryMap.IwramStart,
            IoRegisters.FIFO_A,
            0,
            IoRegisters.DmaEnable | IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming | fixedDestination);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);
        Assert.Equal(0x4444_0003u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));

        gba.Bus.Write16(IoRegisters.DMA1CNT_H, (ushort)(IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming | fixedDestination));
        gba.Bus.Write16(IoRegisters.DMA1CNT_H, (ushort)(IoRegisters.DmaEnable | IoRegisters.DmaRepeat | IoRegisters.DmaWord | specialTiming | fixedDestination));
        gba.Scheduler.Advance(1);

        Assert.Equal(0x4444_0003u, gba.Bus.PeekIo32(IoRegisters.FIFO_A));
    }

    [Fact]
    public void DmaWritesToDmaControlDoNotStartNestedImmediateTransfer()
    {
        var gba = new GbaSystem();
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0x0000_0001);
        gba.Bus.Write32(GbaMemoryMap.IwramStart + 4, 0x8400_0000);
        gba.Bus.Write32(GbaMemoryMap.IwramStart + 8, 0x2222_3333);

        gba.Bus.Write32(IoRegisters.DMA1SAD, GbaMemoryMap.IwramStart + 8);
        gba.Bus.Write32(IoRegisters.DMA1DAD, GbaMemoryMap.IwramStart + 0x300);
        gba.Bus.Write16(IoRegisters.DMA1CNT_L, 1);
        gba.Bus.Write16(IoRegisters.DMA1CNT_H, IoRegisters.DmaWord);
        ConfigureDma0(
            gba,
            GbaMemoryMap.IwramStart,
            IoRegisters.DMA1CNT_L,
            2,
            IoRegisters.DmaEnable | IoRegisters.DmaWord);

        Assert.Equal(0u, gba.Bus.Read32(GbaMemoryMap.IwramStart + 0x300));
    }

    [Fact]
    public void ImmediateDmaCanWriteAndReadEepromSerialData()
    {
        var rom = new byte[Cartridge.HeaderLength + 0x100];
        WriteAscii(rom, 0x100, "EEPROM_V122");
        var gba = new GbaSystem();
        gba.LoadCartridge(Cartridge.Load(rom));
        const uint writeBits = GbaMemoryMap.IwramStart;
        const uint readCommandBits = GbaMemoryMap.IwramStart + 0x100;
        const uint readBits = GbaMemoryMap.IwramStart + 0x200;
        const ulong value = 0x0123_4567_89AB_CDEFul;

        WriteEepromCommandBits(gba, writeBits, 0b10, 3, 6, value);
        ConfigureDma0(gba, writeBits, 0x0D00_0000, 73, IoRegisters.DmaEnable);
        WriteEepromReadCommandBits(gba, readCommandBits, 3, 6);
        ConfigureDma0(gba, readCommandBits, 0x0D00_0000, 9, IoRegisters.DmaEnable);
        ConfigureDma0(gba, 0x0D00_0000, readBits, 68, IoRegisters.DmaEnable);

        for (var i = 0u; i < 4; i++)
        {
            Assert.Equal(0, gba.Bus.Read16(readBits + i * 2) & 1);
        }

        ulong read = 0;
        for (var i = 0u; i < 64; i++)
        {
            read = (read << 1) | (uint)(gba.Bus.Read16(readBits + (4 + i) * 2) & 1);
        }

        Assert.Equal(value, read);
    }

    [Fact]
    public void ImmediateDmaInfersSixBitEepromCommandsForSixteenMiBRom()
    {
        var rom = new byte[16 * 1024 * 1024];
        WriteAscii(rom, 0x100, "EEPROM_V124");
        var gba = new GbaSystem();
        gba.LoadCartridge(Cartridge.Load(rom));
        const uint writeBits = GbaMemoryMap.IwramStart;
        const uint readCommandBits = GbaMemoryMap.IwramStart + 0x100;
        const uint readBits = GbaMemoryMap.IwramStart + 0x200;
        const int address = 0x21;
        const ulong value = 0xBEEF_CAFE_0123_4567ul;

        WriteEepromCommandBits(gba, writeBits, 0b10, address, 6, value);
        ConfigureDma0(gba, writeBits, 0x0D00_0000, 73, IoRegisters.DmaEnable);
        WriteEepromReadCommandBits(gba, readCommandBits, address, 6);
        ConfigureDma0(gba, readCommandBits, 0x0D00_0000, 9, IoRegisters.DmaEnable);
        ConfigureDma0(gba, 0x0D00_0000, readBits, 68, IoRegisters.DmaEnable);

        for (var i = 0u; i < 4; i++)
        {
            Assert.Equal(0, gba.Bus.Read16(readBits + i * 2) & 1);
        }

        ulong read = 0;
        for (var i = 0u; i < 64; i++)
        {
            read = (read << 1) | (uint)(gba.Bus.Read16(readBits + (4 + i) * 2) & 1);
        }

        Assert.Equal(value, read);
    }

    private static void ConfigureDma0(GbaSystem gba, uint source, uint destination, ushort count, ushort control)
    {
        gba.Bus.Write32(IoRegisters.DMA0SAD, source);
        gba.Bus.Write32(IoRegisters.DMA0DAD, destination);
        gba.Bus.Write16(IoRegisters.DMA0CNT_L, count);
        gba.Bus.Write16(IoRegisters.DMA0CNT_H, control);
    }

    private static void ConfigureDma1(GbaSystem gba, uint source, uint destination, ushort count, ushort control)
    {
        gba.Bus.Write32(IoRegisters.DMA1SAD, source);
        gba.Bus.Write32(IoRegisters.DMA1DAD, destination);
        gba.Bus.Write16(IoRegisters.DMA1CNT_L, count);
        gba.Bus.Write16(IoRegisters.DMA1CNT_H, control);
    }

    private static void ConfigureDma2(GbaSystem gba, uint source, uint destination, ushort count, ushort control)
    {
        gba.Bus.Write32(IoRegisters.DMA2SAD, source);
        gba.Bus.Write32(IoRegisters.DMA2DAD, destination);
        gba.Bus.Write16(IoRegisters.DMA2CNT_L, count);
        gba.Bus.Write16(IoRegisters.DMA2CNT_H, control);
    }

    private static void ConfigureDma3(GbaSystem gba, uint source, uint destination, ushort count, ushort control)
    {
        gba.Bus.Write32(IoRegisters.DMA3SAD, source);
        gba.Bus.Write32(IoRegisters.DMA3DAD, destination);
        gba.Bus.Write16(IoRegisters.DMA3CNT_L, count);
        gba.Bus.Write16(IoRegisters.DMA3CNT_H, control);
    }

    private static void WriteEepromCommandBits(GbaSystem gba, uint destination, int command, int address, int addressBits, ulong value)
    {
        var cursor = destination;
        cursor = WriteBits(gba, cursor, (ulong)command, 2);
        cursor = WriteBits(gba, cursor, (ulong)address, addressBits);
        cursor = WriteBits(gba, cursor, value, 64);
        _ = WriteBits(gba, cursor, 0, 1);
    }

    private static void WriteEepromReadCommandBits(GbaSystem gba, uint destination, int address, int addressBits)
    {
        var cursor = destination;
        cursor = WriteBits(gba, cursor, 0b11, 2);
        cursor = WriteBits(gba, cursor, (ulong)address, addressBits);
        _ = WriteBits(gba, cursor, 0, 1);
    }

    private static uint WriteBits(GbaSystem gba, uint destination, ulong value, int bits)
    {
        for (var bit = bits - 1; bit >= 0; bit--)
        {
            gba.Bus.Write16(destination, (ushort)((value >> bit) & 1));
            destination += 2;
        }

        return destination;
    }

    private static void WriteAscii(byte[] target, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            target[offset + i] = (byte)value[i];
        }
    }
}
