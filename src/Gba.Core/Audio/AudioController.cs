using Gba.Core.Dma;
using Gba.Core.Memory;

namespace Gba.Core.Audio;

public sealed class AudioController
{
    private readonly MemoryBus _bus;
    private readonly List<DirectSoundPcmSample> _pendingSamples = [];

    public AudioController(MemoryBus bus, DmaController dma)
    {
        _bus = bus;
        dma.SoundFifoSampleClockedDetailed += OnSoundFifoSampleClocked;
    }

    public IReadOnlyList<DirectSoundPcmSample> PendingSamples => _pendingSamples;

    public bool CaptureSamples { get; set; }

    public event Action<DirectSoundPcmSample>? SampleProduced;

    public void Reset() => _pendingSamples.Clear();

    public DirectSoundPcmSample[] DrainSamples()
    {
        var samples = _pendingSamples.ToArray();
        _pendingSamples.Clear();
        return samples;
    }

    private void OnSoundFifoSampleClocked(SoundFifoSampleClock clock)
    {
        var control = _bus.PeekIo16(IoRegisters.SOUNDCNT_H);
        var volume = DirectSoundVolume(control, clock.Fifo);
        var scaled = ScaleSample(clock.Sample, volume);
        var rightEnabled = clock.Fifo == 0
            ? (control & (1 << 8)) != 0
            : (control & (1 << 12)) != 0;
        var leftEnabled = clock.Fifo == 0
            ? (control & (1 << 9)) != 0
            : (control & (1 << 13)) != 0;

        var pcmSample = new DirectSoundPcmSample(
            clock.Fifo,
            clock.Timer,
            clock.Cycle,
            clock.Sample,
            leftEnabled ? scaled : (short)0,
            rightEnabled ? scaled : (short)0);
        SampleProduced?.Invoke(pcmSample);
        if (CaptureSamples)
        {
            _pendingSamples.Add(pcmSample);
        }
    }

    private static int DirectSoundVolume(ushort control, int fifo)
    {
        var fullVolumeBit = fifo == 0 ? 1 << 2 : 1 << 3;
        return (control & fullVolumeBit) != 0 ? 1 : 2;
    }

    private static short ScaleSample(sbyte sample, int divisor)
        => (short)(sample / divisor);
}

public readonly record struct DirectSoundPcmSample(int Fifo, int Timer, long Cycle, sbyte RawSample, short Left, short Right);
