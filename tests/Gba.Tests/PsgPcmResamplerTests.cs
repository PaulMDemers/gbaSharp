using Gba.Core.Audio;

namespace Gba.Tests;

public sealed class PsgPcmResamplerTests
{
    [Fact]
    public void FirstSamplePrimesWithoutOutput()
    {
        var resampler = new PsgPcmResampler(sampleRate: 4, psgSampleRate: 2, scale: 2);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new PsgPcmSample(0, 5, -5), (left, right) => frames.Add((left, right)));

        Assert.Empty(frames);
    }

    [Fact]
    public void EmitsHeldSampleAtHostRate()
    {
        var resampler = new PsgPcmResampler(sampleRate: 4, psgSampleRate: 2, scale: 2);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new PsgPcmSample(0, 5, -5), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(1, 9, 1), (left, right) => frames.Add((left, right)));

        Assert.Equal([(10, -10), (10, -10)], frames);
    }

    [Fact]
    public void AccumulatesFractionalHostFrames()
    {
        var resampler = new PsgPcmResampler(sampleRate: 3, psgSampleRate: 2, scale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new PsgPcmSample(0, 1, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(1, 2, 0), (left, right) => frames.Add((left, right)));
        resampler.Process(new PsgPcmSample(2, 3, 0), (left, right) => frames.Add((left, right)));

        Assert.Equal([(1, 0), (2, 0), (2, 0)], frames);
    }

    [Fact]
    public void ResetClearsHeldSample()
    {
        var resampler = new PsgPcmResampler(sampleRate: 4, psgSampleRate: 2, scale: 1);
        var frames = new List<(short Left, short Right)>();

        resampler.Process(new PsgPcmSample(0, 1, 0), (left, right) => frames.Add((left, right)));
        resampler.Reset();
        resampler.Process(new PsgPcmSample(1, 2, 0), (left, right) => frames.Add((left, right)));

        Assert.Empty(frames);
    }
}
