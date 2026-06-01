using Gba.Core.Audio;

namespace Gba.Tests;

public sealed class DirectSoundPcmResamplerTests
{
    [Fact]
    public void FirstSamplePrimesHeldFifoWithoutEmittingAFrame()
    {
        var resampler = new DirectSoundPcmResampler(sampleRate: 4, clockHz: 8, scale: 2);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 4, 4, -4), (left, right) => frames.Add((left, right)));

        Assert.Empty(frames);
    }

    [Fact]
    public void EmitsHeldFifoValueForElapsedOutputFramesBeforeUpdating()
    {
        var resampler = new DirectSoundPcmResampler(sampleRate: 4, clockHz: 8, scale: 2);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 4, 4, -4), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(0, 0, 2, 8, 8, 1), (left, right) => frames.Add((left, right)));

        Assert.Equal([(8, -8)], frames);
    }

    [Fact]
    public void MixesHeldFifoAAndFifoBValues()
    {
        var resampler = new DirectSoundPcmResampler(sampleRate: 4, clockHz: 8, scale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 0, 10, 20), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(1, 0, 0, 0, 3, -5), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(0, 0, 2, 0, 7, 8), (left, right) => frames.Add((left, right)));

        Assert.Equal([(13, 15)], frames);
    }

    [Fact]
    public void AccumulatesFractionalFramesAcrossSamples()
    {
        var resampler = new DirectSoundPcmResampler(sampleRate: 3, clockHz: 8, scale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 0, 5, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(0, 0, 1, 0, 6, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(0, 0, 2, 0, 7, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(0, 0, 3, 0, 8, 0), (left, right) => frames.Add((left, right)));

        Assert.Equal([(7, 0)], frames);
    }

    [Fact]
    public void ResetClearsHeldStateAndTiming()
    {
        var resampler = new DirectSoundPcmResampler(sampleRate: 4, clockHz: 8, scale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 0, 5, 0), (left, right) => frames.Add((left, right)));
        resampler.Reset();
        resampler.Process(new DirectSoundPcmSample(0, 0, 2, 0, 9, 0), (left, right) => frames.Add((left, right)));

        Assert.Empty(frames);
    }

    [Fact]
    public void LargeCycleGapsAreCapped()
    {
        var resampler = new DirectSoundPcmResampler(sampleRate: 4, clockHz: 8, scale: 1, maxFramesPerEvent: 2);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 0, 5, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(0, 0, 80, 0, 9, 0), (left, right) => frames.Add((left, right)));

        Assert.Equal([(5, 0), (5, 0)], frames);
    }
}
