namespace Gba.Core.Audio;

public sealed class MixedPcmResampler
{
    public const int DefaultDirectScale = 192;
    public const int DefaultPsgScale = 32;
    private readonly int _sampleRate;
    private readonly int _clockHz;
    private readonly int _directScale;
    private readonly int _psgScale;
    private readonly double _outputGain;
    private readonly int _maxFramesPerEvent;
    private readonly short[,] _currentDirectByFifo = new short[2, 2];
    private short _currentPsgLeft;
    private short _currentPsgRight;
    private long _lastCycle = -1;
    private double _fractionalFrames;

    public MixedPcmResampler(
        int sampleRate,
        int clockHz = DirectSoundPcmResampler.DefaultGbaClockHz,
        int directScale = DefaultDirectScale,
        int psgScale = DefaultPsgScale,
        double outputGain = 1.0,
        int maxFramesPerEvent = 4_410)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be greater than zero.");
        }

        if (clockHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clockHz), clockHz, "Clock rate must be greater than zero.");
        }

        if (directScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(directScale), directScale, "Direct-sound scale must be greater than zero.");
        }

        if (psgScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(psgScale), psgScale, "PSG scale must be greater than zero.");
        }

        if (outputGain <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputGain), outputGain, "Output gain must be greater than zero.");
        }

        if (maxFramesPerEvent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFramesPerEvent), maxFramesPerEvent, "Frame cap must be greater than zero.");
        }

        _sampleRate = sampleRate;
        _clockHz = clockHz;
        _directScale = directScale;
        _psgScale = psgScale;
        _outputGain = outputGain;
        _maxFramesPerEvent = maxFramesPerEvent;
    }

    public void Reset()
    {
        Array.Clear(_currentDirectByFifo);
        _currentPsgLeft = 0;
        _currentPsgRight = 0;
        _lastCycle = -1;
        _fractionalFrames = 0;
    }

    public void Process(DirectSoundPcmSample sample, Action<short, short> emitFrame)
    {
        if (!AdvanceTo(sample.Cycle, emitFrame))
        {
            return;
        }

        if (sample.Fifo is not (0 or 1))
        {
            return;
        }

        _currentDirectByFifo[sample.Fifo, 0] = sample.Left;
        _currentDirectByFifo[sample.Fifo, 1] = sample.Right;
    }

    public void Process(PsgPcmSample sample, Action<short, short> emitFrame)
    {
        if (!AdvanceTo(sample.Cycle, emitFrame))
        {
            return;
        }

        _currentPsgLeft = sample.Left;
        _currentPsgRight = sample.Right;
    }

    private bool AdvanceTo(long cycle, Action<short, short> emitFrame)
    {
        ArgumentNullException.ThrowIfNull(emitFrame);

        if (_lastCycle < 0)
        {
            _lastCycle = cycle;
            _fractionalFrames = 0;
            return true;
        }

        if (cycle < _lastCycle)
        {
            return false;
        }

        if (cycle == _lastCycle)
        {
            return true;
        }

        var exactFrames = ((cycle - _lastCycle) * (double)_sampleRate / _clockHz) + _fractionalFrames;
        var frames = (int)exactFrames;
        _fractionalFrames = exactFrames - frames;
        if (frames > _maxFramesPerEvent)
        {
            frames = _maxFramesPerEvent;
            _fractionalFrames = 0;
        }

        for (var i = 0; i < frames; i++)
        {
            emitFrame(MixLeft(), MixRight());
        }

        _lastCycle = cycle;
        return true;
    }

    private short MixLeft()
        => Clamp16(
            (int)Math.Round((((_currentDirectByFifo[0, 0] + _currentDirectByFifo[1, 0]) * _directScale)
            + (_currentPsgLeft * _psgScale)) * _outputGain));

    private short MixRight()
        => Clamp16(
            (int)Math.Round((((_currentDirectByFifo[0, 1] + _currentDirectByFifo[1, 1]) * _directScale)
            + (_currentPsgRight * _psgScale)) * _outputGain));

    private static short Clamp16(int value)
        => (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
