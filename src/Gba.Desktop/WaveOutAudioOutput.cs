using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Gba.Core.Audio;

namespace Gba.Desktop;

internal sealed class WaveOutAudioOutput : IDisposable
{
    private const int SampleRate = 44_100;
    private const int Channels = 2;
    private const int BitsPerSample = 16;
    private const int BytesPerSample = BitsPerSample / 8;
    private const int FrameBytes = Channels * BytesPerSample;
    private const int BufferFrames = 1_024;
    private const int BufferCount = 4;
    private const int MaxQueuedFrames = SampleRate / 2;
    private const int MaxFramesPerEvent = SampleRate / 10;
    private const uint WaveMapper = 0xFFFF_FFFF;
    private const ushort WaveFormatPcm = 1;
    private const uint CallbackNull = 0;
    private const uint WaveHeaderDone = 0x0000_0001;

    private readonly object _queueSync = new();
    private readonly object _deviceSync = new();
    private readonly Queue<short> _queuedSamples = new(MaxQueuedFrames * Channels);
    private readonly MixedPcmResampler _resampler = new(SampleRate, maxFramesPerEvent: MaxFramesPerEvent);
    private readonly WaveBuffer[] _buffers = new WaveBuffer[BufferCount];
    private readonly Thread? _pumpThread;
    private volatile bool _disposed;
    private volatile bool _enabled = true;
    private IntPtr _waveOut;

    public WaveOutAudioOutput()
    {
        try
        {
            OpenDevice();
            for (var i = 0; i < _buffers.Length; i++)
            {
                _buffers[i] = new WaveBuffer(BufferFrames * FrameBytes);
                WriteBuffer(_buffers[i]);
            }

            IsAvailable = true;
            _pumpThread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "gbaSharp audio"
            };
            _pumpThread.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            LastError = ex.Message;
            Dispose();
        }
    }

    public bool IsAvailable { get; private set; }

    public string? LastError { get; private set; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                Clear();
            }
        }
    }

    public void Enqueue(DirectSoundPcmSample sample)
    {
        if (!_enabled || !IsAvailable || _disposed)
        {
            return;
        }

        lock (_queueSync)
        {
            _resampler.Process(sample, EnqueueFrame);
        }
    }

    public void Enqueue(PsgPcmSample sample)
    {
        if (!_enabled || !IsAvailable || _disposed)
        {
            return;
        }

        lock (_queueSync)
        {
            _resampler.Process(sample, EnqueueFrame);
        }
    }

    public void Clear()
    {
        lock (_queueSync)
        {
            _queuedSamples.Clear();
            _resampler.Reset();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _pumpThread?.Join(TimeSpan.FromSeconds(1));
        lock (_deviceSync)
        {
            if (_waveOut != IntPtr.Zero)
            {
                waveOutReset(_waveOut);
            }

            foreach (var buffer in _buffers)
            {
                buffer?.Dispose(_waveOut);
            }

            if (_waveOut != IntPtr.Zero)
            {
                waveOutClose(_waveOut);
                _waveOut = IntPtr.Zero;
            }
        }

        IsAvailable = false;
    }

    private void OpenDevice()
    {
        var format = new WaveFormat
        {
            FormatTag = WaveFormatPcm,
            Channels = Channels,
            SamplesPerSec = SampleRate,
            AvgBytesPerSec = SampleRate * FrameBytes,
            BlockAlign = FrameBytes,
            BitsPerSample = BitsPerSample
        };

        var result = waveOutOpen(out _waveOut, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull);
        if (result != 0)
        {
            throw new InvalidOperationException($"waveOutOpen failed with MMRESULT {result}.");
        }
    }

    private void Pump()
    {
        while (!_disposed)
        {
            lock (_deviceSync)
            {
                if (_waveOut != IntPtr.Zero)
                {
                    foreach (var buffer in _buffers)
                    {
                        if (buffer is not null && buffer.IsDone)
                        {
                            buffer.Unprepare(_waveOut);
                            WriteBuffer(buffer);
                        }
                    }
                }
            }

            Thread.Sleep(4);
        }
    }

    private void WriteBuffer(WaveBuffer buffer)
    {
        FillBuffer(buffer.Data);
        buffer.PrepareAndWrite(_waveOut);
    }

    private void FillBuffer(byte[] data)
    {
        lock (_queueSync)
        {
            for (var offset = 0; offset < data.Length; offset += FrameBytes)
            {
                var left = DequeueSampleOrSilence();
                var right = DequeueSampleOrSilence();
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), left);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 2, 2), right);
            }
        }
    }

    private short DequeueSampleOrSilence()
    {
        if (!_enabled || _queuedSamples.Count == 0)
        {
            return 0;
        }

        return _queuedSamples.Dequeue();
    }

    private void EnqueueFrame(short left, short right)
    {
        while (_queuedSamples.Count > (MaxQueuedFrames - 1) * Channels)
        {
            _queuedSamples.Dequeue();
            _queuedSamples.Dequeue();
        }

        _queuedSamples.Enqueue(left);
        _queuedSamples.Enqueue(right);
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr waveOut, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr waveOut, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr waveOut, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr waveOut);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public UIntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }

    private sealed class WaveBuffer : IDisposable
    {
        private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
        private readonly GCHandle _dataHandle;
        private IntPtr _headerPtr;
        private bool _prepared;
        private bool _disposed;

        public WaveBuffer(int bytes)
        {
            Data = new byte[bytes];
            _dataHandle = GCHandle.Alloc(Data, GCHandleType.Pinned);
            _headerPtr = Marshal.AllocHGlobal(HeaderSize);
            WriteHeader(new WaveHeader
            {
                Data = _dataHandle.AddrOfPinnedObject(),
                BufferLength = (uint)Data.Length
            });
        }

        public byte[] Data { get; }

        public bool IsDone => (ReadHeader().Flags & WaveHeaderDone) != 0;

        public void PrepareAndWrite(IntPtr waveOut)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var header = ReadHeader();
            header.BufferLength = (uint)Data.Length;
            header.BytesRecorded = 0;
            header.Flags = 0;
            header.Loops = 0;
            WriteHeader(header);
            Check(waveOutPrepareHeader(waveOut, _headerPtr, HeaderSize), "waveOutPrepareHeader");
            _prepared = true;
            Check(waveOutWrite(waveOut, _headerPtr, HeaderSize), "waveOutWrite");
        }

        public void Unprepare(IntPtr waveOut)
        {
            if (!_prepared || waveOut == IntPtr.Zero)
            {
                return;
            }

            Check(waveOutUnprepareHeader(waveOut, _headerPtr, HeaderSize), "waveOutUnprepareHeader");
            _prepared = false;
        }

        public void Dispose() => Dispose(IntPtr.Zero);

        public void Dispose(IntPtr waveOut)
        {
            if (_disposed)
            {
                return;
            }

            Unprepare(waveOut);
            if (_dataHandle.IsAllocated)
            {
                _dataHandle.Free();
            }

            if (_headerPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_headerPtr);
                _headerPtr = IntPtr.Zero;
            }

            _disposed = true;
        }

        private WaveHeader ReadHeader() => Marshal.PtrToStructure<WaveHeader>(_headerPtr);

        private void WriteHeader(WaveHeader header) => Marshal.StructureToPtr(header, _headerPtr, false);
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{operation} failed with MMRESULT {result}.");
        }
    }
}
