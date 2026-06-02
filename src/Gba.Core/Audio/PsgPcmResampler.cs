namespace Gba.Core.Audio;

public sealed class PsgPcmResampler
{
    public const int DefaultPsgSampleRate = 32_768;
    private readonly int _sampleRate;
    private readonly int _psgSampleRate;
    private readonly int _scale;
    private PsgPcmSample _heldSample;
    private bool _hasHeldSample;
    private double _fractionalFrames;

    public PsgPcmResampler(int sampleRate, int psgSampleRate = DefaultPsgSampleRate, int scale = 32)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be greater than zero.");
        }

        if (psgSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(psgSampleRate), psgSampleRate, "PSG sample rate must be greater than zero.");
        }

        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be greater than zero.");
        }

        _sampleRate = sampleRate;
        _psgSampleRate = psgSampleRate;
        _scale = scale;
    }

    public void Reset()
    {
        _heldSample = default;
        _hasHeldSample = false;
        _fractionalFrames = 0;
    }

    public void Process(PsgPcmSample sample, Action<short, short> emitFrame)
    {
        ArgumentNullException.ThrowIfNull(emitFrame);

        if (!_hasHeldSample)
        {
            _heldSample = sample;
            _hasHeldSample = true;
            return;
        }

        var exactFrames = ((double)_sampleRate / _psgSampleRate) + _fractionalFrames;
        var frames = (int)exactFrames;
        _fractionalFrames = exactFrames - frames;
        for (var i = 0; i < frames; i++)
        {
            emitFrame(Clamp16(_heldSample.Left * _scale), Clamp16(_heldSample.Right * _scale));
        }

        _heldSample = sample;
    }

    private static short Clamp16(int value)
        => (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
