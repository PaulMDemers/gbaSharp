using Gba.Core.Memory;
using Gba.Core.Scheduling;

namespace Gba.Core.Timers;

public sealed class TimerController
{
    private static readonly int[] Prescalers = [1, 64, 256, 1024];
    private static readonly uint[] DataRegisters =
    [
        IoRegisters.TM0CNT_L,
        IoRegisters.TM1CNT_L,
        IoRegisters.TM2CNT_L,
        IoRegisters.TM3CNT_L
    ];
    private static readonly uint[] ControlRegisters =
    [
        IoRegisters.TM0CNT_H,
        IoRegisters.TM1CNT_H,
        IoRegisters.TM2CNT_H,
        IoRegisters.TM3CNT_H
    ];
    private static readonly ushort[] InterruptBits =
    [
        IoRegisters.InterruptTimer0,
        IoRegisters.InterruptTimer1,
        IoRegisters.InterruptTimer2,
        IoRegisters.InterruptTimer3
    ];

    private readonly MemoryBus _bus;
    private readonly Scheduler _scheduler;
    private readonly ushort[] _reload = new ushort[4];
    private readonly ushort[] _counter = new ushort[4];
    private readonly ushort[] _control = new ushort[4];
    private readonly long[] _lastUpdateCycle = new long[4];
    private readonly long[] _generation = new long[4];

    public TimerController(MemoryBus bus, Scheduler scheduler)
    {
        _bus = bus;
        _scheduler = scheduler;
        _bus.AddIoReadObserver(OnIoRead);
        _bus.AddIoWriteObserver(OnIoWrite);
    }

    public event Action<int>? TimerOverflowed;

    public void Reset()
    {
        Array.Clear(_reload);
        Array.Clear(_counter);
        Array.Clear(_control);
        Array.Clear(_lastUpdateCycle);
        Array.Clear(_generation);

        for (var timer = 0; timer < 4; timer++)
        {
            WriteCounter(timer, 0);
            WriteControl(timer, 0);
        }
    }

    private void OnIoRead(uint address, int bytes)
    {
        for (var timer = 0; timer < 4; timer++)
        {
            if (Overlaps(address, bytes, DataRegisters[timer], 2))
            {
                SyncCounter(timer);
            }
        }
    }

    private void OnIoWrite(uint address, int bytes)
    {
        for (var timer = 0; timer < 4; timer++)
        {
            if (Overlaps(address, bytes, DataRegisters[timer], 2))
            {
                _reload[timer] = ReadCounter(timer);
                if (!IsEnabled(timer))
                {
                    _counter[timer] = _reload[timer];
                }
            }

            if (Overlaps(address, bytes, ControlRegisters[timer], 2))
            {
                var wasEnabled = IsEnabled(timer);
                var wasCascade = IsCascade(timer);
                if (wasEnabled && !wasCascade)
                {
                    SyncCounter(timer);
                }

                _control[timer] = ReadControl(timer);
                var enabled = IsEnabled(timer);
                var cascade = IsCascade(timer);
                _generation[timer]++;

                if (enabled && !wasEnabled)
                {
                    _counter[timer] = _reload[timer];
                    WriteCounter(timer, _counter[timer]);
                    _lastUpdateCycle[timer] = _scheduler.Now;
                    ScheduleOverflow(timer);
                }
                else if (enabled && wasCascade && !cascade)
                {
                    _lastUpdateCycle[timer] = _scheduler.Now;
                    WriteCounter(timer, _counter[timer]);
                    ScheduleOverflow(timer);
                }
                else if (enabled && !cascade)
                {
                    SyncCounter(timer);
                    ScheduleOverflow(timer);
                }
            }
        }
    }

    private void SyncCounter(int timer)
    {
        if (!IsEnabled(timer) || IsCascade(timer))
        {
            WriteCounter(timer, _counter[timer]);
            return;
        }

        var elapsed = _scheduler.Now - _lastUpdateCycle[timer];
        if (elapsed <= 0)
        {
            WriteCounter(timer, _counter[timer]);
            return;
        }

        var ticks = elapsed / Prescaler(timer);
        if (ticks <= 0)
        {
            WriteCounter(timer, _counter[timer]);
            return;
        }

        _counter[timer] = (ushort)(_counter[timer] + ticks);
        _lastUpdateCycle[timer] += ticks * Prescaler(timer);
        WriteCounter(timer, _counter[timer]);
    }

    private void ScheduleOverflow(int timer)
    {
        if (!IsEnabled(timer) || IsCascade(timer))
        {
            return;
        }

        var generation = _generation[timer];
        var ticksUntilOverflow = 0x1_0000 - _counter[timer];
        var cyclesUntilOverflow = ticksUntilOverflow * Prescaler(timer);
        _scheduler.Schedule(cyclesUntilOverflow, () => OnOverflow(timer, generation));
    }

    private void OnOverflow(int timer, long generation)
    {
        if (generation != _generation[timer] || !IsEnabled(timer) || IsCascade(timer))
        {
            return;
        }

        Overflow(timer);
        _lastUpdateCycle[timer] = _scheduler.Now;
        ScheduleOverflow(timer);
    }

    private void Overflow(int timer)
    {
        _counter[timer] = _reload[timer];
        WriteCounter(timer, _counter[timer]);

        if ((_control[timer] & IoRegisters.TimerIrq) != 0)
        {
            _bus.RequestInterrupt(InterruptBits[timer]);
        }

        TimerOverflowed?.Invoke(timer);

        var next = timer + 1;
        if (next < 4 && IsEnabled(next) && IsCascade(next))
        {
            IncrementCascade(next);
        }
    }

    private void IncrementCascade(int timer)
    {
        _counter[timer]++;
        if (_counter[timer] == 0)
        {
            Overflow(timer);
            return;
        }

        WriteCounter(timer, _counter[timer]);
    }

    private bool IsEnabled(int timer) => (_control[timer] & IoRegisters.TimerEnable) != 0;

    private bool IsCascade(int timer) => timer > 0 && (_control[timer] & IoRegisters.TimerCascade) != 0;

    private int Prescaler(int timer) => Prescalers[_control[timer] & IoRegisters.TimerPrescalerMask];

    private ushort ReadCounter(int timer) => _bus.PeekIo16(DataRegisters[timer]);

    private ushort ReadControl(int timer) => _bus.PeekIo16(ControlRegisters[timer]);

    private void WriteCounter(int timer, ushort value) => WriteRaw16(DataRegisters[timer], value);

    private void WriteControl(int timer, ushort value) => WriteRaw16(ControlRegisters[timer], value);

    private void WriteRaw16(uint address, ushort value) => _bus.PokeIo16(address, value);

    private static bool Overlaps(uint writeAddress, int writeBytes, uint registerAddress, int registerBytes)
        => writeAddress < registerAddress + registerBytes && registerAddress < writeAddress + writeBytes;
}
