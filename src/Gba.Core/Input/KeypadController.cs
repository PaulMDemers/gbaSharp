using Gba.Core.Memory;

namespace Gba.Core.Input;

public sealed class KeypadController
{
    private const ushort KeyMask = 0x03FF;

    private readonly MemoryBus _bus;
    private GbaKey _pressedKeys;

    public KeypadController(MemoryBus bus)
    {
        _bus = bus;
        _bus.AddIoWriteObserver(OnIoWrite);
    }

    public GbaKey PressedKeys => _pressedKeys;

    public void Reset()
    {
        _pressedKeys = 0;
        UpdateKeyInput();
        _bus.PokeIo16(IoRegisters.KEYCNT, 0);
    }

    public void SetPressedKeys(GbaKey keys)
    {
        _pressedKeys = keys & (GbaKey)KeyMask;
        UpdateKeyInput();
        EvaluateKeyInterrupt();
    }

    public void Press(GbaKey keys) => SetPressedKeys(_pressedKeys | keys);

    public void Release(GbaKey keys) => SetPressedKeys(_pressedKeys & ~keys);

    private void OnIoWrite(uint address, int bytes)
    {
        if (Overlaps(address, bytes, IoRegisters.KEYINPUT, 2))
        {
            UpdateKeyInput();
        }

        if (Overlaps(address, bytes, IoRegisters.KEYCNT, 2))
        {
            EvaluateKeyInterrupt();
        }
    }

    private void UpdateKeyInput()
    {
        var value = (ushort)(KeyMask & ~(ushort)_pressedKeys);
        _bus.PokeIo16(IoRegisters.KEYINPUT, value);
    }

    private void EvaluateKeyInterrupt()
    {
        var keyControl = _bus.PeekIo16(IoRegisters.KEYCNT);
        if ((keyControl & (1 << 14)) == 0)
        {
            return;
        }

        var selectedKeys = keyControl & KeyMask;
        if (selectedKeys == 0)
        {
            return;
        }

        var pressed = (ushort)_pressedKeys & selectedKeys;
        var andCondition = (keyControl & (1 << 15)) != 0;
        var matched = andCondition ? pressed == selectedKeys : pressed != 0;
        if (matched)
        {
            _bus.RequestInterrupt(IoRegisters.InterruptKeypad);
        }
    }

    private static bool Overlaps(uint writeAddress, int writeBytes, uint registerAddress, int registerBytes)
        => writeAddress < registerAddress + registerBytes && registerAddress < writeAddress + writeBytes;
}

