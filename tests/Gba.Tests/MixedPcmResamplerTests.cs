using Gba.Core.Audio;

namespace Gba.Tests;

public sealed class MixedPcmResamplerTests
{
    [Fact]
    public void SameCycleDirectAndPsgUpdatesMixBeforeNextFrame()
    {
        var resampler = new MixedPcmResampler(sampleRate: 4, clockHz: 8, directScale: 2, psgScale: 3);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 0, 4, -4), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(0, 2, 1), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(0, 0, 2, 0, 8, 8), (left, right) => frames.Add((left, right)));

        Assert.Equal([(14, -5)], frames);
    }

    [Fact]
    public void MixesBothDirectFifosAndPsgOnSharedCycleTimeline()
    {
        var resampler = new MixedPcmResampler(sampleRate: 4, clockHz: 8, directScale: 1, psgScale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 0, 10, 20), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(1, 0, 0, 0, 3, -5), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(0, -2, 7), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(2, 1, 1), (left, right) => frames.Add((left, right)));

        Assert.Equal([(11, 22)], frames);
    }

    [Fact]
    public void AccumulatesFractionalFramesAcrossMixedEvents()
    {
        var resampler = new MixedPcmResampler(sampleRate: 3, clockHz: 8, directScale: 1, psgScale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new PsgPcmSample(0, 1, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(1, 2, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new DirectSoundPcmSample(0, 0, 2, 0, 3, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(3, 4, 0), (left, right) => frames.Add((left, right)));

        Assert.Equal([(5, 0)], frames);
    }

    [Fact]
    public void ResetClearsHeldMixedState()
    {
        var resampler = new MixedPcmResampler(sampleRate: 4, clockHz: 8, directScale: 1, psgScale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new DirectSoundPcmSample(0, 0, 0, 0, 5, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(0, 4, 0), (left, right) => frames.Add((left, right)));
        resampler.Reset();
        resampler.Process(new DirectSoundPcmSample(0, 0, 2, 0, 1, 0), (left, right) => frames.Add((left, right)));

        Assert.Empty(frames);
    }

    [Fact]
    public void OlderEventsDoNotOverwriteHeldState()
    {
        var resampler = new MixedPcmResampler(sampleRate: 4, clockHz: 8, directScale: 1, psgScale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new PsgPcmSample(4, 5, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(2, 9, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(6, 1, 0), (left, right) => frames.Add((left, right)));

        Assert.Equal([(5, 0)], frames);
    }

    [Fact]
    public void LargeCycleGapsAreCapped()
    {
        var resampler = new MixedPcmResampler(sampleRate: 4, clockHz: 8, directScale: 1, psgScale: 1, maxFramesPerEvent: 2);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new PsgPcmSample(0, 5, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(80, 9, 0), (left, right) => frames.Add((left, right)));

        Assert.Equal([(5, 0), (5, 0)], frames);
    }
}
