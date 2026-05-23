using Gba.Core;
using Gba.Core.Cpu;
using Gba.Core.Memory;

namespace Gba.Tests;

public sealed class TimerControllerTests
{
    [Fact]
    public void EnabledTimerCountsSchedulerCyclesWithPrescaler()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(12);

        Assert.Equal(12, gba.Bus.Read16(IoRegisters.TM0CNT_L));
    }

    [Fact]
    public void TimerUsesConfiguredPrescaler()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable | 1); // /64

        gba.Scheduler.Advance(63);
        Assert.Equal(0, gba.Bus.Read16(IoRegisters.TM0CNT_L));

        gba.Scheduler.Advance(1);
        Assert.Equal(1, gba.Bus.Read16(IoRegisters.TM0CNT_L));
    }

    [Fact]
    public void TimerOverflowReloadsAndRequestsInterrupt()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFE);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable | IoRegisters.TimerIrq);

        gba.Scheduler.Advance(2);

        Assert.Equal(0xFFFE, gba.Bus.Read16(IoRegisters.TM0CNT_L));
        Assert.Equal(IoRegisters.InterruptTimer0, gba.Bus.InterruptFlags & IoRegisters.InterruptTimer0);
    }

    [Fact]
    public void CascadeTimerIncrementsOnPreviousOverflow()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM1CNT_L, 0);
        gba.Bus.Write16(IoRegisters.TM1CNT_H, IoRegisters.TimerEnable | IoRegisters.TimerCascade);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);

        Assert.Equal(1, gba.Bus.Read16(IoRegisters.TM1CNT_L));
    }

    [Fact]
    public void CascadeTimerOverflowReloadsAndRequestsInterrupt()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM1CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM1CNT_H, IoRegisters.TimerEnable | IoRegisters.TimerCascade | IoRegisters.TimerIrq);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable);

        gba.Scheduler.Advance(1);

        Assert.Equal(0xFFFF, gba.Bus.Read16(IoRegisters.TM1CNT_L));
        Assert.Equal(IoRegisters.InterruptTimer1, gba.Bus.InterruptFlags & IoRegisters.InterruptTimer1);
    }

    [Fact]
    public void TimerInterruptCanEnterCpuIrq()
    {
        var gba = new GbaSystem();
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Cpu.SetIrqEnabled(true);
        gba.Bus.InterruptEnable = IoRegisters.InterruptTimer0;
        gba.Bus.InterruptMasterEnable = true;
        gba.Bus.Write16(IoRegisters.TM0CNT_L, 0xFFFF);
        gba.Bus.Write16(IoRegisters.TM0CNT_H, IoRegisters.TimerEnable | IoRegisters.TimerIrq);

        gba.Scheduler.Advance(1);
        gba.Step();

        Assert.Equal(CpuMode.Irq, gba.Cpu.Mode);
        Assert.Equal(0x18u, gba.Cpu.Pc);
    }
}

