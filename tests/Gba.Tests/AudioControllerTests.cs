using Gba.Core;
using Gba.Core.Audio;
using Gba.Core.Memory;

namespace Gba.Tests;

public sealed class AudioControllerTests
{
    [Fact]
    public void DirectSoundAFifoSamplesHonorVolumeAndPanning()
    {
        var gba = new GbaSystem();
        gba.Audio.CaptureSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_H, (1 << 2) | (1 << 8) | (1 << 9));
        gba.Bus.Write32(IoRegisters.FIFO_A, 0x0000_0040);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);

        var sample = Assert.Single(gba.Audio.PendingSamples);
        Assert.Equal(0, sample.Fifo);
        Assert.Equal(0, sample.Timer);
        Assert.Equal(1, sample.Cycle);
        Assert.Equal(64, sample.RawSample);
        Assert.Equal(64, sample.Left);
        Assert.Equal(64, sample.Right);
    }

    [Fact]
    public void DirectSoundBFifoSamplesCanRouteToOneSideAtHalfVolume()
    {
        var gba = new GbaSystem();
        gba.Audio.CaptureSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_H, 1 << 13);
        gba.Bus.Write32(IoRegisters.FIFO_B, 0x0000_0080);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);

        var sample = Assert.Single(gba.Audio.PendingSamples);
        Assert.Equal(1, sample.Fifo);
        Assert.Equal(0, sample.Timer);
        Assert.Equal(1, sample.Cycle);
        Assert.Equal(-128, sample.RawSample);
        Assert.Equal(-64, sample.Left);
        Assert.Equal(0, sample.Right);
    }

    [Fact]
    public void DrainSamplesReturnsAndClearsPendingAudio()
    {
        var gba = new GbaSystem();
        gba.Audio.CaptureSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_H, (1 << 2) | (1 << 8));
        gba.Bus.Write32(IoRegisters.FIFO_A, 0x0000_0001);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);
        gba.Scheduler.Advance(1);

        var samples = gba.Audio.DrainSamples();

        Assert.Single(samples);
        Assert.Empty(gba.Audio.PendingSamples);
    }

    [Fact]
    public void SamplesDoNotAccumulateUnlessCaptureIsEnabled()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.SOUNDCNT_H, (1 << 2) | (1 << 8));
        gba.Bus.Write32(IoRegisters.FIFO_A, 0x0000_0020);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);

        Assert.Empty(gba.Audio.PendingSamples);
    }

    [Fact]
    public void SampleProducedEventFiresWithoutPendingCapture()
    {
        var gba = new GbaSystem();
        DirectSoundPcmSample? produced = null;
        gba.Audio.SampleProduced += sample => produced = sample;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_H, (1 << 2) | (1 << 8));
        gba.Bus.Write32(IoRegisters.FIFO_A, 0x0000_0020);
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);

        Assert.NotNull(produced);
        Assert.Equal(0, produced.Value.Timer);
        Assert.Equal(1, produced.Value.Cycle);
        Assert.Equal(32, produced.Value.RawSample);
        Assert.Equal(0, produced.Value.Left);
        Assert.Equal(32, produced.Value.Right);
        Assert.Empty(gba.Audio.PendingSamples);
    }
}
