namespace Gba.Core.Audio;

public sealed class DirectSoundPcmResampler
{
    public const int DefaultGbaClockHz = 16_777_216;
    private readonly int _sampleRate;
    private readonly int _clockHz;
    private readonly int _scale;
    private readonly int _maxFramesPerEvent;
    private readonly short[,] _currentByFifo = new short[2, 2];
    private long _lastCycle = -1;
    private double _fractionalFrames;

    public DirectSoundPcmResampler(int sampleRate, int clockHz = DefaultGbaClockHz, int scale = 192, int maxFramesPerEvent = 4_410)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be greater than zero.");
        }

        if (clockHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clockHz), clockHz, "Clock rate must be greater than zero.");
        }

        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be greater than zero.");
        }

        if (maxFramesPerEvent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFramesPerEvent), maxFramesPerEvent, "Frame cap must be greater than zero.");
        }

        _sampleRate = sampleRate;
        _clockHz = clockHz;
        _scale = scale;
        _maxFramesPerEvent = maxFramesPerEvent;
    }

    public void Reset()
    {
        Array.Clear(_currentByFifo);
        _lastCycle = -1;
        _fractionalFrames = 0;
    }

    public void Process(DirectSoundPcmSample sample, Action<short, short> emitFrame)
    {
        ArgumentNullException.ThrowIfNull(emitFrame);

        if (sample.Cycle <= _lastCycle || _lastCycle < 0)
        {
            _lastCycle = sample.Cycle;
            _fractionalFrames = 0;
            UpdateFifo(sample);
            return;
        }

        var exactFrames = ((sample.Cycle - _lastCycle) * (double)_sampleRate / _clockHz) + _fractionalFrames;
        var frames = (int)exactFrames;
        _fractionalFrames = exactFrames - frames;
        if (frames > _maxFramesPerEvent)
        {
            frames = _maxFramesPerEvent;
            _fractionalFrames = 0;
        }

        for (var i = 0; i < frames; i++)
        {
            emitFrame(
                Clamp16((_currentByFifo[0, 0] + _currentByFifo[1, 0]) * _scale),
                Clamp16((_currentByFifo[0, 1] + _currentByFifo[1, 1]) * _scale));
        }

        _lastCycle = sample.Cycle;
        UpdateFifo(sample);
    }

    private void UpdateFifo(DirectSoundPcmSample sample)
    {
        if (sample.Fifo is not (0 or 1))
        {
            return;
        }

        _currentByFifo[sample.Fifo, 0] = sample.Left;
        _currentByFifo[sample.Fifo, 1] = sample.Right;
    }

    private static short Clamp16(int value)
        => (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
