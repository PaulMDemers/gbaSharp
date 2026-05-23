using Gba.Core;
using Gba.Core.Cpu;
using Gba.Core.Input;
using Gba.Core.Memory;

namespace Gba.Tests;

public sealed class KeypadControllerTests
{
    [Fact]
    public void ResetShowsAllKeysReleased()
    {
        var gba = new GbaSystem();

        Assert.Equal(0x03FF, gba.Bus.Read16(IoRegisters.KEYINPUT) & 0x03FF);
    }

    [Fact]
    public void PressedKeysAreActiveLowInKeyInput()
    {
        var gba = new GbaSystem();

        gba.Keypad.SetPressedKeys(GbaKey.A | GbaKey.Start);

        Assert.Equal(0, gba.Bus.Read16(IoRegisters.KEYINPUT) & (ushort)GbaKey.A);
        Assert.Equal(0, gba.Bus.Read16(IoRegisters.KEYINPUT) & (ushort)GbaKey.Start);
        Assert.NotEqual(0, gba.Bus.Read16(IoRegisters.KEYINPUT) & (ushort)GbaKey.B);
    }

    [Fact]
    public void KeyControlOrModeRequestsInterruptWhenAnySelectedKeyPressed()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.KEYCNT, (ushort)((1 << 14) | (ushort)GbaKey.A | (ushort)GbaKey.B));

        gba.Keypad.Press(GbaKey.B);

        Assert.Equal(IoRegisters.InterruptKeypad, gba.Bus.InterruptFlags & IoRegisters.InterruptKeypad);
    }

    [Fact]
    public void KeyControlAndModeRequiresAllSelectedKeys()
    {
        var gba = new GbaSystem();
        gba.Bus.Write16(IoRegisters.KEYCNT, (ushort)((1 << 15) | (1 << 14) | (ushort)GbaKey.A | (ushort)GbaKey.B));

        gba.Keypad.Press(GbaKey.A);
        Assert.Equal(0, gba.Bus.InterruptFlags & IoRegisters.InterruptKeypad);

        gba.Keypad.Press(GbaKey.B);
        Assert.Equal(IoRegisters.InterruptKeypad, gba.Bus.InterruptFlags & IoRegisters.InterruptKeypad);
    }

    [Fact]
    public void KeypadInterruptCanEnterCpuIrq()
    {
        var gba = new GbaSystem();
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Cpu.SetIrqEnabled(true);
        gba.Bus.InterruptEnable = IoRegisters.InterruptKeypad;
        gba.Bus.InterruptMasterEnable = true;
        gba.Bus.Write16(IoRegisters.KEYCNT, (ushort)((1 << 14) | (ushort)GbaKey.Start));

        gba.Keypad.Press(GbaKey.Start);
        gba.Step();

        Assert.Equal(CpuMode.Irq, gba.Cpu.Mode);
        Assert.Equal(0x18u, gba.Cpu.Pc);
    }
}

