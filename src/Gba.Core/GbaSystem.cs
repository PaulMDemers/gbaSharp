using Gba.Core.Audio;
using Gba.Core.Cartridges;
using Gba.Core.Cpu;
using Gba.Core.Dma;
using Gba.Core.Input;
using Gba.Core.Memory;
using Gba.Core.Scheduling;
using Gba.Core.Timers;
using Gba.Core.Video;
using System.Diagnostics;

namespace Gba.Core;

public sealed class GbaSystem
{
    public GbaSystem(byte[]? bios = null)
    {
        Bus = new MemoryBus(bios);
        Cpu = new Arm7Tdmi(Bus);
        Scheduler = new Scheduler();
        Dma = new DmaController(Bus);
        Audio = new AudioController(Bus, Dma);
        Keypad = new KeypadController(Bus);
        Timers = new TimerController(Bus, Scheduler);
        Video = new VideoController(Bus, Scheduler);
        Video.VBlankStarted += Dma.NotifyVBlank;
        Video.HBlankStarted += Dma.NotifyHBlank;
        Video.DisplayStartDmaRequested += Dma.NotifyDisplayStart;
        Timers.TimerOverflowedAtCycle += Dma.NotifySoundTimerOverflow;
        Cpu.VBlankWaitCycleProvider = () => Video.CyclesUntilNextVBlankStart;
        Cpu.InterruptWaitCycleProvider = CyclesUntilNextSchedulerEvent;
        Dma.Reset();
        Audio.Reset();
        Keypad.Reset();
        Timers.Reset();
        Video.Reset();
    }

    public MemoryBus Bus { get; }

    public Arm7Tdmi Cpu { get; }

    public Scheduler Scheduler { get; }

    public DmaController Dma { get; }

    public AudioController Audio { get; }

    public KeypadController Keypad { get; }

    public VideoController Video { get; }

    public TimerController Timers { get; }

    public Cartridge? Cartridge { get; private set; }

    public void LoadCartridge(Cartridge cartridge)
    {
        ArgumentNullException.ThrowIfNull(cartridge);

        Cartridge = cartridge;
        Bus.LoadCartridge(cartridge);
        Cpu.Reset(useBios: Bus.HasBios);
        Scheduler.Reset();
        Dma.Reset();
        Audio.Reset();
        Keypad.Reset();
        Timers.Reset();
        if (!Bus.HasBios)
        {
            Bus.PostFlag = 1;
            Bus.PokeIo16(IoRegisters.WAITCNT, 0x4317);
            Video.ResetSkippedBiosHandoff(ReadNoBiosHandoffLineOverride(), ReadNoBiosHandoffCyclesOverride());
        }
        else
        {
            Video.Reset();
        }
        Cpu.VBlankWaitCycleProvider = () => Video.CyclesUntilNextVBlankStart;
        Cpu.InterruptWaitCycleProvider = CyclesUntilNextSchedulerEvent;
    }

    public int Step()
    {
        var cycles = Cpu.Step();
        Bus.Advance(cycles);
        Scheduler.Advance(cycles);
        cycles += DrainDmaCycles();
        return cycles;
    }

    public int Step(ref GbaStepProfile profile)
    {
        var start = Stopwatch.GetTimestamp();
        var cycles = Cpu.Step();
        var afterCpu = Stopwatch.GetTimestamp();
        Bus.Advance(cycles);
        var afterBus = Stopwatch.GetTimestamp();
        Scheduler.Advance(cycles);
        cycles += DrainDmaCycles();
        var afterScheduler = Stopwatch.GetTimestamp();

        profile.Steps++;
        profile.CpuTicks += afterCpu - start;
        profile.BusTicks += afterBus - afterCpu;
        profile.SchedulerTicks += afterScheduler - afterBus;
        return cycles;
    }

    private int DrainDmaCycles()
    {
        var total = 0;
        while (true)
        {
            var cycles = Dma.ConsumePendingCycles();
            if (cycles <= 0)
            {
                return total;
            }

            total += cycles;
            Bus.Advance(cycles);
            Scheduler.Advance(cycles);
        }
    }

    private int CyclesUntilNextSchedulerEvent()
    {
        var cycles = Scheduler.CyclesUntilNextEvent;
        return cycles > int.MaxValue ? int.MaxValue : (int)cycles;
    }

    private static int? ReadNoBiosHandoffLineOverride()
    {
        return ReadNoBiosHandoffOverride("GBASHARP_NO_BIOS_HANDOFF_LINE", min: 0, max: VideoController.TotalLines - 1);
    }

    private static int? ReadNoBiosHandoffCyclesOverride()
    {
        return ReadNoBiosHandoffOverride("GBASHARP_NO_BIOS_HANDOFF_CYCLES", min: 1, max: VideoController.CyclesPerScanline);
    }

    private static int? ReadNoBiosHandoffOverride(string name, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(value, out var parsed) || parsed < min || parsed > max)
        {
            return null;
        }

        return parsed;
    }
}

public struct GbaStepProfile
{
    public long Steps;
    public long CpuTicks;
    public long BusTicks;
    public long SchedulerTicks;

    public long TotalTicks => CpuTicks + BusTicks + SchedulerTicks;

    public double CpuPercent => Percent(CpuTicks);

    public double BusPercent => Percent(BusTicks);

    public double SchedulerPercent => Percent(SchedulerTicks);

    public double CpuMilliseconds => TicksToMilliseconds(CpuTicks);

    public double BusMilliseconds => TicksToMilliseconds(BusTicks);

    public double SchedulerMilliseconds => TicksToMilliseconds(SchedulerTicks);

    private double Percent(long ticks) => TotalTicks == 0 ? 0 : ticks * 100.0 / TotalTicks;

    private static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
