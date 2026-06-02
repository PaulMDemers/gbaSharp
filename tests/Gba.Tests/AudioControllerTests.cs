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

    [Fact]
    public void Square1ProducesRoutedPsgSamplesWhenTriggered()
    {
        var gba = new GbaSystem();
        gba.Audio.CapturePsgSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_X, 1 << 7);
        gba.Bus.Write16(IoRegisters.SOUNDCNT_L, (7 << 4) | 7 | (1 << 12) | (1 << 8));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_H, (2 << 6) | (8 << 12));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_X, 0x8000);

        gba.Audio.Advance(512, 512);

        var sample = Assert.Single(gba.Audio.DrainPsgSamples());
        Assert.Equal(512, sample.Cycle);
        Assert.Equal(512, sample.Left);
        Assert.Equal(512, sample.Right);
    }

    [Fact]
    public void Square2HonorsIndependentRouting()
    {
        var gba = new GbaSystem();
        gba.Audio.CapturePsgSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_X, 1 << 7);
        gba.Bus.Write16(IoRegisters.SOUNDCNT_L, (3 << 4) | 7 | (1 << 13));
        gba.Bus.Write16(IoRegisters.SOUND2CNT_L, (2 << 6) | (6 << 12));
        gba.Bus.Write16(IoRegisters.SOUND2CNT_H, 0x8000);

        gba.Audio.Advance(512, 512);

        var sample = Assert.Single(gba.Audio.DrainPsgSamples());
        Assert.Equal(192, sample.Left);
        Assert.Equal(0, sample.Right);
    }

    [Fact]
    public void PsgMasterDisableSuppressesSamples()
    {
        var gba = new GbaSystem();
        gba.Audio.CapturePsgSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_L, (7 << 4) | 7 | (1 << 12) | (1 << 8));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_H, (2 << 6) | (8 << 12));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_X, 0x8000);

        gba.Audio.Advance(512, 512);

        Assert.Empty(gba.Audio.DrainPsgSamples());
    }

    [Fact]
    public void SoundIoResetClearsPsgChannels()
    {
        var gba = new GbaSystem();
        gba.Audio.CapturePsgSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_X, 1 << 7);
        gba.Bus.Write16(IoRegisters.SOUNDCNT_L, (7 << 4) | 7 | (1 << 12) | (1 << 8));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_H, (2 << 6) | (8 << 12));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_X, 0x8000);
        gba.Bus.RegisterRamReset(1u << 6);

        gba.Audio.Advance(512, 512);

        Assert.Empty(gba.Audio.DrainPsgSamples());
    }

    [Fact]
    public void PsgSamplingDoesNotScheduleCpuWakeEvents()
    {
        var gba = new GbaSystem();
        var before = gba.Scheduler.CyclesUntilNextEvent;

        gba.Audio.Advance(512, 512);

        Assert.Equal(before, gba.Scheduler.CyclesUntilNextEvent);
    }

    [Fact]
    public void SquareLengthCounterStopsChannel()
    {
        var gba = new GbaSystem();
        gba.Audio.CapturePsgSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_X, 1 << 7);
        gba.Bus.Write16(IoRegisters.SOUNDCNT_L, (7 << 4) | 7 | (1 << 12) | (1 << 8));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_H, 63 | (2 << 6) | (8 << 12));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_X, (1 << 15) | (1 << 14) | 2047);

        gba.Audio.Advance(512, 512);
        Assert.NotEmpty(gba.Audio.DrainPsgSamples());

        gba.Audio.Advance(32_768, 33_280);
        gba.Audio.DrainPsgSamples();
        gba.Audio.Advance(512, 33_792);

        Assert.Empty(gba.Audio.DrainPsgSamples());
    }

    [Fact]
    public void SquareEnvelopeCanDecreaseVolume()
    {
        var gba = new GbaSystem();
        gba.Audio.CapturePsgSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_X, 1 << 7);
        gba.Bus.Write16(IoRegisters.SOUNDCNT_L, (7 << 4) | 7 | (1 << 12) | (1 << 8));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_H, (2 << 6) | (1 << 8) | (2 << 12));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_X, (1 << 15) | 2047);

        gba.Audio.Advance(512, 512);
        var before = Assert.Single(gba.Audio.DrainPsgSamples());
        Assert.Equal(128, before.Left);

        gba.Audio.Advance(262_144, 262_656);
        gba.Audio.DrainPsgSamples();
        gba.Audio.Advance(512, 263_168);

        var after = Assert.Single(gba.Audio.DrainPsgSamples());
        Assert.Equal(64, after.Left);
    }

    [Fact]
    public void Square1SweepOverflowDisablesChannel()
    {
        var gba = new GbaSystem();
        gba.Audio.CapturePsgSamples = true;
        gba.Bus.Write16(IoRegisters.SOUNDCNT_X, 1 << 7);
        gba.Bus.Write16(IoRegisters.SOUNDCNT_L, (7 << 4) | 7 | (1 << 12) | (1 << 8));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_L, (1 << 4) | 1);
        gba.Bus.Write16(IoRegisters.SOUND1CNT_H, (2 << 6) | (8 << 12));
        gba.Bus.Write16(IoRegisters.SOUND1CNT_X, (1 << 15) | 2047);

        gba.Audio.Advance(512, 512);
        Assert.NotEmpty(gba.Audio.DrainPsgSamples());

        gba.Audio.Advance(97_792, 98_304);
        gba.Audio.DrainPsgSamples();
        gba.Audio.Advance(512, 98_816);

        Assert.Empty(gba.Audio.DrainPsgSamples());
    }
}
