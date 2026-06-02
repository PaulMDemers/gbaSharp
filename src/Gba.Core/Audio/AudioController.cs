using Gba.Core.Dma;
using Gba.Core.Memory;

namespace Gba.Core.Audio;

public sealed class AudioController
{
    private const int PsgSampleRate = 32_768;
    private const int PsgCyclesPerSample = DirectSoundPcmResampler.DefaultGbaClockHz / PsgSampleRate;
    private const int PsgSamplesPerFrameSequencerTick = PsgSampleRate / 512;
    private readonly MemoryBus _bus;
    private readonly List<DirectSoundPcmSample> _pendingSamples = [];
    private readonly List<PsgPcmSample> _pendingPsgSamples = [];
    private readonly SquareChannel _square1 = new(0);
    private readonly SquareChannel _square2 = new(1);
    private readonly WaveChannel _wave;
    private long _psgCyclesUntilNext = PsgCyclesPerSample;
    private int _psgSamplesUntilFrameSequencer = PsgSamplesPerFrameSequencerTick;
    private int _frameSequencerStep;

    public AudioController(MemoryBus bus, DmaController dma)
    {
        _bus = bus;
        _wave = new WaveChannel(bus);
        dma.SoundFifoSampleClockedDetailed += OnSoundFifoSampleClocked;
        bus.AddIoWriteObserver(OnIoWrite);
        bus.SoundIoReset += ResetPsg;
    }

    public IReadOnlyList<DirectSoundPcmSample> PendingSamples => _pendingSamples;

    public bool CaptureSamples { get; set; }

    public bool CapturePsgSamples { get; set; }

    public event Action<DirectSoundPcmSample>? SampleProduced;

    public event Action<PsgPcmSample>? PsgSampleProduced;

    public void Reset()
    {
        _pendingSamples.Clear();
        ResetPsg();
    }

    public void Advance(long cycles, long currentCycle)
    {
        if (cycles <= 0)
        {
            return;
        }

        var remaining = cycles;
        var sampleCycle = currentCycle - cycles;
        while (remaining >= _psgCyclesUntilNext)
        {
            sampleCycle += _psgCyclesUntilNext;
            remaining -= _psgCyclesUntilNext;
            EmitPsgSample(sampleCycle);
            ClockFrameSequencerIfNeeded();
            _psgCyclesUntilNext = PsgCyclesPerSample;
        }

        _psgCyclesUntilNext -= remaining;
    }

    public DirectSoundPcmSample[] DrainSamples()
    {
        var samples = _pendingSamples.ToArray();
        _pendingSamples.Clear();
        return samples;
    }

    public PsgPcmSample[] DrainPsgSamples()
    {
        var samples = _pendingPsgSamples.ToArray();
        _pendingPsgSamples.Clear();
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

    private void OnIoWrite(uint address, int bytes)
    {
        if (Overlaps(address, bytes, IoRegisters.SOUND1CNT_X, 2)
            && (_bus.PeekIo16(IoRegisters.SOUND1CNT_X) is var square1FrequencyControl))
        {
            if ((square1FrequencyControl & (1 << 15)) != 0)
            {
                _square1.Trigger(_bus.PeekIo16(IoRegisters.SOUND1CNT_H), square1FrequencyControl, _bus.PeekIo16(IoRegisters.SOUND1CNT_L));
            }
            else
            {
                _square1.UpdateFrequencyControl(square1FrequencyControl);
            }
        }

        if (Overlaps(address, bytes, IoRegisters.SOUND2CNT_H, 2)
            && (_bus.PeekIo16(IoRegisters.SOUND2CNT_H) is var square2FrequencyControl))
        {
            if ((square2FrequencyControl & (1 << 15)) != 0)
            {
                _square2.Trigger(_bus.PeekIo16(IoRegisters.SOUND2CNT_L), square2FrequencyControl);
            }
            else
            {
                _square2.UpdateFrequencyControl(square2FrequencyControl);
            }
        }

        if (Overlaps(address, bytes, IoRegisters.SOUND3CNT_X, 2)
            && (_bus.PeekIo16(IoRegisters.SOUND3CNT_X) is var waveFrequencyControl))
        {
            if ((waveFrequencyControl & (1 << 15)) != 0)
            {
                _wave.Trigger(
                    _bus.PeekIo16(IoRegisters.SOUND3CNT_L),
                    _bus.PeekIo16(IoRegisters.SOUND3CNT_H),
                    waveFrequencyControl);
            }
            else
            {
                _wave.UpdateFrequencyControl(waveFrequencyControl);
            }
        }
    }

    private void ResetPsg()
    {
        _pendingPsgSamples.Clear();
        _square1.Reset();
        _square2.Reset();
        _wave.Reset();
        _psgCyclesUntilNext = PsgCyclesPerSample;
        _psgSamplesUntilFrameSequencer = PsgSamplesPerFrameSequencerTick;
        _frameSequencerStep = 0;
    }

    private void ClockFrameSequencerIfNeeded()
    {
        _psgSamplesUntilFrameSequencer--;
        if (_psgSamplesUntilFrameSequencer > 0)
        {
            return;
        }

        _psgSamplesUntilFrameSequencer = PsgSamplesPerFrameSequencerTick;
        if ((_frameSequencerStep & 1) == 0)
        {
            _square1.ClockLength();
            _square2.ClockLength();
        }

        if (_frameSequencerStep is 2 or 6)
        {
            _square1.ClockSweep();
        }

        if (_frameSequencerStep == 7)
        {
            _square1.ClockEnvelope();
            _square2.ClockEnvelope();
        }

        _frameSequencerStep = (_frameSequencerStep + 1) & 7;
    }

    private void EmitPsgSample(long cycle)
    {
        if ((_bus.PeekIo16(IoRegisters.SOUNDCNT_X) & (1 << 7)) == 0)
        {
            return;
        }

        var soundControl = _bus.PeekIo16(IoRegisters.SOUNDCNT_L);
        var leftVolume = ((soundControl >> 4) & 0x7) + 1;
        var rightVolume = (soundControl & 0x7) + 1;
        var left = 0;
        var right = 0;
        MixSquare(_square1, soundControl, 8, 12, leftVolume, rightVolume, ref left, ref right);
        MixSquare(_square2, soundControl, 9, 13, leftVolume, rightVolume, ref left, ref right);
        MixWave(_wave, soundControl, 10, 14, leftVolume, rightVolume, ref left, ref right);
        if (left == 0 && right == 0)
        {
            return;
        }

        var sample = new PsgPcmSample(cycle, ClampPsg(left), ClampPsg(right));
        PsgSampleProduced?.Invoke(sample);
        if (CapturePsgSamples)
        {
            _pendingPsgSamples.Add(sample);
        }
    }

    private static void MixSquare(
        SquareChannel channel,
        ushort soundControl,
        int rightEnableBit,
        int leftEnableBit,
        int leftVolume,
        int rightVolume,
        ref int left,
        ref int right)
    {
        var output = channel.NextOutput();
        if (output == 0)
        {
            return;
        }

        if ((soundControl & (1 << leftEnableBit)) != 0)
        {
            left += output * leftVolume;
        }

        if ((soundControl & (1 << rightEnableBit)) != 0)
        {
            right += output * rightVolume;
        }
    }

    private static void MixWave(
        WaveChannel channel,
        ushort soundControl,
        int rightEnableBit,
        int leftEnableBit,
        int leftVolume,
        int rightVolume,
        ref int left,
        ref int right)
    {
        var output = channel.NextOutput();
        if (output == 0)
        {
            return;
        }

        if ((soundControl & (1 << leftEnableBit)) != 0)
        {
            left += output * leftVolume;
        }

        if ((soundControl & (1 << rightEnableBit)) != 0)
        {
            right += output * rightVolume;
        }
    }

    private static short ClampPsg(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static bool Overlaps(uint writeAddress, int writeBytes, uint registerAddress, int registerBytes)
        => writeAddress < registerAddress + registerBytes && registerAddress < writeAddress + writeBytes;

    private sealed class SquareChannel(int channel)
    {
        private static readonly double[] DutyThresholds = [0.125, 0.25, 0.5, 0.75];
        private bool _enabled;
        private int _frequency;
        private int _volume;
        private int _duty;
        private int _lengthCounter;
        private bool _lengthEnabled;
        private int _envelopeVolume;
        private int _envelopePeriod;
        private int _envelopeTimer;
        private bool _envelopeIncrease;
        private double _phase;

        public void Reset()
        {
            _enabled = false;
            _frequency = 0;
            _volume = 0;
            _duty = 0;
            _lengthCounter = 0;
            _lengthEnabled = false;
            _envelopeVolume = 0;
            _envelopePeriod = 0;
            _envelopeTimer = 0;
            _envelopeIncrease = false;
            _sweepPeriod = 0;
            _sweepTimer = 0;
            _sweepShift = 0;
            _sweepNegate = false;
            _sweepEnabled = false;
            _phase = 0;
        }

        public void Trigger(ushort control, ushort frequencyControl, ushort sweepControl = 0)
        {
            _frequency = frequencyControl & 0x07FF;
            _volume = (control >> 12) & 0xF;
            _envelopeVolume = _volume;
            _envelopeIncrease = (control & (1 << 11)) != 0;
            _envelopePeriod = (control >> 8) & 0x7;
            _envelopeTimer = _envelopePeriod;
            _duty = (control >> 6) & 0x3;
            var lengthLoad = control & 0x3F;
            _lengthCounter = lengthLoad == 0 ? 64 : 64 - lengthLoad;
            _lengthEnabled = (frequencyControl & (1 << 14)) != 0;
            ConfigureSweep(sweepControl);
            _phase = 0;
            _enabled = _envelopeVolume > 0 && _frequency < 2048;
        }

        public void UpdateFrequencyControl(ushort frequencyControl)
        {
            _frequency = frequencyControl & 0x07FF;
            _lengthEnabled = (frequencyControl & (1 << 14)) != 0;
        }

        public void ClockLength()
        {
            if (!_enabled || !_lengthEnabled || _lengthCounter <= 0)
            {
                return;
            }

            _lengthCounter--;
            if (_lengthCounter == 0)
            {
                _enabled = false;
            }
        }

        public void ClockEnvelope()
        {
            if (!_enabled || _envelopePeriod == 0)
            {
                return;
            }

            _envelopeTimer--;
            if (_envelopeTimer > 0)
            {
                return;
            }

            _envelopeTimer = _envelopePeriod;
            var nextVolume = _envelopeVolume + (_envelopeIncrease ? 1 : -1);
            if (nextVolume is >= 0 and <= 15)
            {
                _envelopeVolume = nextVolume;
                if (_envelopeVolume == 0)
                {
                    _enabled = false;
                }
            }
        }

        public void ClockSweep()
        {
            if (!_enabled || !_sweepEnabled)
            {
                return;
            }

            _sweepTimer--;
            if (_sweepTimer > 0)
            {
                return;
            }

            _sweepTimer = _sweepPeriod == 0 ? 8 : _sweepPeriod;
            if (_sweepShift == 0)
            {
                return;
            }

            var delta = _frequency >> _sweepShift;
            var next = _sweepNegate ? _frequency - delta : _frequency + delta;
            if (next is < 0 or > 2047)
            {
                _enabled = false;
                return;
            }

            _frequency = next;
        }

        public int NextOutput()
        {
            if (!_enabled || _envelopeVolume == 0)
            {
                return 0;
            }

            var frequencyHz = 131_072.0 / (2048 - _frequency);
            _phase += frequencyHz / PsgSampleRate;
            _phase -= Math.Floor(_phase);
            var sign = _phase < DutyThresholds[_duty] ? 1 : -1;
            return sign * _envelopeVolume * 8;
        }

        public override string ToString() => $"Square {channel + 1}";

        private int _sweepPeriod;
        private int _sweepTimer;
        private int _sweepShift;
        private bool _sweepNegate;
        private bool _sweepEnabled;

        private void ConfigureSweep(ushort sweepControl)
        {
            _sweepPeriod = (sweepControl >> 4) & 0x7;
            _sweepTimer = _sweepPeriod == 0 ? 8 : _sweepPeriod;
            _sweepNegate = (sweepControl & (1 << 3)) != 0;
            _sweepShift = sweepControl & 0x7;
            _sweepEnabled = channel == 0 && (_sweepPeriod > 0 || _sweepShift > 0);
        }
    }

    private sealed class WaveChannel(MemoryBus bus)
    {
        private bool _enabled;
        private int _frequency;
        private int _volumeCode;
        private double _sampleIndex;

        public void Reset()
        {
            _enabled = false;
            _frequency = 0;
            _volumeCode = 0;
            _sampleIndex = 0;
        }

        public void Trigger(ushort control, ushort lengthAndVolume, ushort frequencyControl)
        {
            _frequency = frequencyControl & 0x07FF;
            _volumeCode = (lengthAndVolume >> 13) & 0x3;
            _sampleIndex = 0;
            _enabled = (control & (1 << 7)) != 0 && _volumeCode != 0 && _frequency < 2048;
        }

        public void UpdateFrequencyControl(ushort frequencyControl)
        {
            _frequency = frequencyControl & 0x07FF;
        }

        public int NextOutput()
        {
            if (!_enabled)
            {
                return 0;
            }

            var sample = ReadWaveSample((int)_sampleIndex);
            var centered = sample - 8;
            var scaled = _volumeCode switch
            {
                1 => centered,
                2 => centered / 2,
                3 => centered / 4,
                _ => 0
            };
            var stepRate = 2_097_152.0 / (2048 - _frequency);
            _sampleIndex += stepRate / PsgSampleRate;
            _sampleIndex %= 32;
            return scaled * 8;
        }

        private int ReadWaveSample(int index)
        {
            var value = bus.Read8(IoRegisters.WAVE_RAM + (uint)(index / 2));
            return (index & 1) == 0 ? value >> 4 : value & 0xF;
        }
    }
}

public readonly record struct DirectSoundPcmSample(int Fifo, int Timer, long Cycle, sbyte RawSample, short Left, short Right);

public readonly record struct PsgPcmSample(long Cycle, short Left, short Right);
