using Gba.Core.Memory;

namespace Gba.Core.Dma;

public sealed class DmaController
{
    private static readonly uint[] SourceRegisters =
    [
        IoRegisters.DMA0SAD,
        IoRegisters.DMA1SAD,
        IoRegisters.DMA2SAD,
        IoRegisters.DMA3SAD
    ];
    private static readonly uint[] DestinationRegisters =
    [
        IoRegisters.DMA0DAD,
        IoRegisters.DMA1DAD,
        IoRegisters.DMA2DAD,
        IoRegisters.DMA3DAD
    ];
    private static readonly uint[] CountRegisters =
    [
        IoRegisters.DMA0CNT_L,
        IoRegisters.DMA1CNT_L,
        IoRegisters.DMA2CNT_L,
        IoRegisters.DMA3CNT_L
    ];
    private static readonly uint[] ControlRegisters =
    [
        IoRegisters.DMA0CNT_H,
        IoRegisters.DMA1CNT_H,
        IoRegisters.DMA2CNT_H,
        IoRegisters.DMA3CNT_H
    ];
    private static readonly ushort[] InterruptBits =
    [
        IoRegisters.InterruptDma0,
        IoRegisters.InterruptDma1,
        IoRegisters.InterruptDma2,
        IoRegisters.InterruptDma3
    ];

    private readonly MemoryBus _bus;
    private readonly DmaChannel[] _channels = [new(), new(), new(), new()];
    private readonly int[] _soundFifoLevels = new int[2];
    private readonly Queue<byte>[] _soundFifoBytes = [new(), new()];
    private int _transferDepth;
    private int _pendingCycles;

    public event Action<DmaTransferTrace>? TransferStarted;

    public event Action<int, sbyte>? SoundFifoSampleClocked;

    public event Action<SoundFifoSampleClock>? SoundFifoSampleClockedDetailed;

    public int SoundFifoALevel => _soundFifoLevels[0];

    public int SoundFifoBLevel => _soundFifoLevels[1];

    public int ConsumePendingCycles()
    {
        var cycles = _pendingCycles;
        _pendingCycles = 0;
        return cycles;
    }

    public DmaController(MemoryBus bus)
    {
        _bus = bus;
        _bus.AddIoWriteObserver(OnIoWrite);
        _bus.SoundIoReset += ResetSoundFifos;
    }

    public void Reset()
    {
        for (var channel = 0; channel < 4; channel++)
        {
            _channels[channel] = new DmaChannel();
            _bus.PokeIo16(CountRegisters[channel], 0);
            _bus.PokeIo16(ControlRegisters[channel], 0);
        }

        ResetSoundFifos();
        _pendingCycles = 0;
    }

    private void ResetSoundFifos()
    {
        Array.Clear(_soundFifoLevels);
        foreach (var fifo in _soundFifoBytes)
        {
            fifo.Clear();
        }
    }

    public void NotifyVBlank() => RunTriggeredTransfers(DmaStartTiming.VBlank);

    public void NotifyHBlank() => RunTriggeredTransfers(DmaStartTiming.HBlank);

    public void NotifyDisplayStart(bool lastDisplayStartLine)
    {
        var state = _channels[3];
        if (IsEnabled(state.Control) && StartTiming(state.Control) == DmaStartTiming.Special)
        {
            RunTransfer(3, forceDisableAfterTransfer: lastDisplayStartLine);
        }
    }

    public void NotifySoundTimerOverflow(int timer)
        => NotifySoundTimerOverflow(timer, -1);

    public void NotifySoundTimerOverflow(int timer, long cycle)
    {
        if (timer is not (0 or 1))
        {
            return;
        }

        var soundControl = _bus.PeekIo16(IoRegisters.SOUNDCNT_H);
        if (((soundControl >> 10) & 1) == timer)
        {
            ClockSoundFifo(IoRegisters.FIFO_A, timer, cycle);
        }

        if (((soundControl >> 14) & 1) == timer)
        {
            ClockSoundFifo(IoRegisters.FIFO_B, timer, cycle);
        }
    }

    private void OnIoWrite(uint address, int bytes)
    {
        TrackSoundFifoWrite(address, bytes);

        for (var channel = 0; channel < 4; channel++)
        {
            if (Overlaps(address, bytes, SourceRegisters[channel], 4))
            {
                _channels[channel].Source = _bus.PeekIo32(SourceRegisters[channel]);
            }

            if (Overlaps(address, bytes, DestinationRegisters[channel], 4))
            {
                var destination = _bus.PeekIo32(DestinationRegisters[channel]);
                _channels[channel].Destination = destination;
                _channels[channel].InitialDestination = destination;
            }

            if (Overlaps(address, bytes, CountRegisters[channel], 2))
            {
                _channels[channel].Count = _bus.PeekIo16(CountRegisters[channel]);
            }

            if (Overlaps(address, bytes, ControlRegisters[channel], 2))
            {
                var previous = _channels[channel].Control;
                var control = _bus.PeekIo16(ControlRegisters[channel]);
                _channels[channel].Control = control;

                if (IsEnabled(control))
                {
                    _channels[channel].Count = _bus.PeekIo16(CountRegisters[channel]);

                    if (_transferDepth == 0
                        && StartTiming(control) == DmaStartTiming.Immediate
                        && !IsEnabled(previous))
                    {
                        RunTransfer(channel);
                    }
                }
            }
        }
    }

    private void TrackSoundFifoWrite(uint address, int bytes)
    {
        if (Overlaps(address, bytes, IoRegisters.FIFO_A, 4))
        {
            TrackSoundFifoBytes(0, address, bytes, IoRegisters.FIFO_A);
        }

        if (Overlaps(address, bytes, IoRegisters.FIFO_B, 4))
        {
            TrackSoundFifoBytes(1, address, bytes, IoRegisters.FIFO_B);
        }

        if (Overlaps(address, bytes, IoRegisters.SOUNDCNT_H, 2))
        {
            var soundControl = _bus.PeekIo16(IoRegisters.SOUNDCNT_H);
            var resetBits = 0;
            if ((soundControl & (1 << 11)) != 0)
            {
                _soundFifoLevels[0] = 0;
                _soundFifoBytes[0].Clear();
                resetBits |= 1 << 11;
            }

            if ((soundControl & (1 << 15)) != 0)
            {
                _soundFifoLevels[1] = 0;
                _soundFifoBytes[1].Clear();
                resetBits |= 1 << 15;
            }

            if (resetBits != 0)
            {
                _bus.PokeIo16(IoRegisters.SOUNDCNT_H, (ushort)(soundControl & ~resetBits));
            }
        }
    }

    private void TrackSoundFifoBytes(int fifo, uint address, int bytes, uint fifoAddress)
    {
        var start = Math.Max(address, fifoAddress);
        var end = Math.Min(address + (uint)bytes, fifoAddress + 4);
        var fifoValue = _bus.PeekIo32(fifoAddress);
        for (var byteAddress = start; byteAddress < end && _soundFifoBytes[fifo].Count < 32; byteAddress++)
        {
            var shift = (int)((byteAddress - fifoAddress) * 8);
            _soundFifoBytes[fifo].Enqueue((byte)(fifoValue >> shift));
        }

        _soundFifoLevels[fifo] = _soundFifoBytes[fifo].Count;
    }

    private void RunTriggeredTransfers(DmaStartTiming timing)
    {
        for (var channel = 0; channel < 4; channel++)
        {
            if (IsEnabled(_channels[channel].Control) && StartTiming(_channels[channel].Control) == timing)
            {
                RunTransfer(channel);
            }
        }
    }

    private void ClockSoundFifo(uint fifoAddress, int timer, long cycle)
    {
        var fifo = fifoAddress == IoRegisters.FIFO_A ? 0 : 1;
        if (_soundFifoBytes[fifo].Count > 0)
        {
            var sample = unchecked((sbyte)_soundFifoBytes[fifo].Dequeue());
            SoundFifoSampleClocked?.Invoke(fifo, sample);
            SoundFifoSampleClockedDetailed?.Invoke(new SoundFifoSampleClock(fifo, sample, timer, cycle));
            _soundFifoLevels[fifo] = _soundFifoBytes[fifo].Count;
        }

        if (_soundFifoLevels[fifo] <= 16)
        {
            RunSoundFifoTransfers(fifoAddress);
        }
    }

    private void RunSoundFifoTransfers(uint fifoAddress)
    {
        for (var channel = 1; channel <= 2; channel++)
        {
            var state = _channels[channel];
            if (!IsEnabled(state.Control)
                || StartTiming(state.Control) != DmaStartTiming.Special
                || (state.InitialDestination & ~3u) != fifoAddress)
            {
                continue;
            }

            RunTransfer(channel, forcedWordTransfer: true, forcedCount: 4, forcedDestination: fifoAddress);
        }
    }

    private void RunTransfer(int channel, bool? forcedWordTransfer = null, int? forcedCount = null, uint? forcedDestination = null, bool forceDisableAfterTransfer = false)
    {
        var state = _channels[channel];
        var wordTransfer = forcedWordTransfer ?? (state.Control & IoRegisters.DmaWord) != 0;
        var unitSize = wordTransfer ? 4u : 2u;
        var count = forcedCount ?? EffectiveCount(channel, state.Count);
        var source = AlignTransferAddress(state.Source, unitSize);
        var destination = AlignTransferAddress(forcedDestination ?? state.Destination, unitSize);
        HintEepromTransferSize(source, destination, unitSize, count);
        TransferStarted?.Invoke(new DmaTransferTrace(
            channel,
            StartTiming(state.Control).ToString(),
            source,
            destination,
            count,
            wordTransfer,
            state.Control,
            _soundFifoLevels[0],
            _soundFifoLevels[1]));

        _transferDepth++;
        var transferCycles = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (wordTransfer)
                {
                    transferCycles += TransferAccessCycles(source, destination, 4);
                    _bus.Write32(destination, _bus.Read32(source));
                }
                else
                {
                    transferCycles += TransferAccessCycles(source, destination, 2);
                    _bus.Write16(destination, _bus.Read16(source));
                }

                source = AdjustAddress(source, SourceControl(state.Control), unitSize, isDestination: false);
                destination = forcedDestination.HasValue
                    ? AlignTransferAddress(forcedDestination.Value, unitSize)
                    : AdjustAddress(destination, DestinationControl(state.Control), unitSize, isDestination: true);
            }
        }
        finally
        {
            _transferDepth--;
        }

        _pendingCycles += Math.Max(1, transferCycles);
        state.Source = source;
        state.Destination = destination;

        if ((state.Control & IoRegisters.DmaIrq) != 0)
        {
            _bus.RequestInterrupt(InterruptBits[channel]);
        }

        var repeat = (state.Control & IoRegisters.DmaRepeat) != 0;
        var timing = StartTiming(state.Control);
        if (!repeat || timing == DmaStartTiming.Immediate || forceDisableAfterTransfer)
        {
            state.Control = (ushort)(state.Control & ~IoRegisters.DmaEnable);
            _bus.PokeIo16(ControlRegisters[channel], state.Control);
        }
        else if (DestinationControl(state.Control) == DmaAddressControl.Reload)
        {
            state.Destination = state.InitialDestination;
        }
    }

    private static int EffectiveCount(int channel, ushort count)
    {
        if (count != 0)
        {
            return count;
        }

        return channel == 3 ? 0x1_0000 : 0x4000;
    }

    private static uint AdjustAddress(uint address, DmaAddressControl control, uint unitSize, bool isDestination) => control switch
    {
        DmaAddressControl.Increment => address + unitSize,
        DmaAddressControl.Decrement => address - unitSize,
        DmaAddressControl.Fixed => address,
        DmaAddressControl.Reload when isDestination => address + unitSize,
        DmaAddressControl.Reload => address + unitSize,
        _ => address + unitSize
    };

    private static uint AlignTransferAddress(uint address, uint unitSize)
        => unitSize == 4 ? address & ~3u : address & ~1u;

    private int TransferAccessCycles(uint source, uint destination, int bytes)
        => Math.Max(1, _bus.GetCpuAccessCycles(source, bytes, sequential: false))
            + Math.Max(1, _bus.GetCpuAccessCycles(destination, bytes, sequential: false));

    private void HintEepromTransferSize(uint source, uint destination, uint unitSize, int count)
    {
        if (unitSize != 2)
        {
            return;
        }

        if (IsEepromAddress(source) || IsEepromAddress(destination))
        {
            _bus.HintEepromTransferBitCount(count);
        }
    }

    private static bool IsEepromAddress(uint address)
        => address is >= 0x0D00_0000 and <= GbaMemoryMap.GamePakRomEnd;

    private static bool IsEnabled(ushort control) => (control & IoRegisters.DmaEnable) != 0;

    private static DmaStartTiming StartTiming(ushort control) => (DmaStartTiming)((control & IoRegisters.DmaTimingMask) >> 12);

    private static DmaAddressControl DestinationControl(ushort control) => (DmaAddressControl)((control & IoRegisters.DmaDestControlMask) >> 5);

    private static DmaAddressControl SourceControl(ushort control) => (DmaAddressControl)((control & IoRegisters.DmaSourceControlMask) >> 7);

    private static bool Overlaps(uint writeAddress, int writeBytes, uint registerAddress, int registerBytes)
        => writeAddress < registerAddress + registerBytes && registerAddress < writeAddress + writeBytes;

    private sealed class DmaChannel
    {
        public uint Source { get; set; }

        public uint Destination { get; set; }

        public uint InitialDestination { get; set; }

        public ushort Count { get; set; }

        public ushort Control { get; set; }
    }

    private enum DmaStartTiming
    {
        Immediate = 0,
        VBlank = 1,
        HBlank = 2,
        Special = 3
    }

    private enum DmaAddressControl
    {
        Increment = 0,
        Decrement = 1,
        Fixed = 2,
        Reload = 3
    }

    public readonly record struct DmaTransferTrace(
        int Channel,
        string Timing,
        uint Source,
        uint Destination,
        int Count,
        bool WordTransfer,
        ushort Control,
        int FifoALevel,
        int FifoBLevel);
}

public readonly record struct SoundFifoSampleClock(int Fifo, sbyte Sample, int Timer, long Cycle);
