using System.Diagnostics;
using Gba.Core.Memory;

namespace Gba.Core.Cpu;

public sealed class Arm7Tdmi
{
    private const uint NoBiosIrqBiosReturnAddress = 0x0000_0138;
    private const int NoBiosIrqWrapperCycles = 76;
    private readonly MemoryBus _bus;
    private readonly uint[] _registers = new uint[16];
    private readonly Dictionary<CpuMode, BankedRegisters> _bankedRegisters = [];
    private readonly Dictionary<CpuMode, uint> _savedProgramStatusRegisters = [];
    private readonly uint[] _sharedHighRegisters = new uint[5];
    private readonly uint[] _fiqHighRegisters = new uint[5];
    private bool _noBiosIrqActive;
    private uint _noBiosIrqReturnPc;
    private uint _noBiosIrqExitLr;
    private uint _noBiosIrqReturnCpsr;
    private bool _noBiosIrqReturnThumb;
    private uint _noBiosIrqFramePointer;
    private ushort _noBiosIrqPendingInterrupts;
    private readonly uint[] _noBiosIrqSavedRegisters = new uint[6];
    private readonly Stack<NoBiosIrqContext> _noBiosIrqStack = new();
    private bool _hleInterruptWaitActive;
    private ushort _hleInterruptWaitFlags;
    private bool _halted;
    private bool _stopped;
    private uint _lastInstructionFetchEnd = uint.MaxValue;
    private int _lastInstructionFetchCyclesExtra;
    private bool _armPrefetchValid;
    private uint _armPrefetchAddress;
    private uint _armPrefetchInstruction;
    private bool _thumbPrefetchValid;
    private uint _thumbPrefetchAddress;
    private ushort _thumbPrefetchInstruction;

    public Arm7Tdmi(MemoryBus bus)
    {
        _bus = bus;
        _bus.PowerDownRequested += EnterPowerDown;
        Reset(useBios: false);
    }

    public CpuMode Mode { get; private set; }

    public bool ThumbState { get; private set; }

    public bool IrqDisabled { get; private set; }

    public uint Cpsr { get; private set; }

    public event Action<uint, uint>? SoftwareInterruptCalled;

    public event Action<uint, uint, ushort, ushort, bool>? InterruptEntered;

    public event Action<uint, uint, ushort, ushort, bool>? InterruptReturned;

    public Func<int>? VBlankWaitCycleProvider { get; set; }

    public Func<int>? InterruptWaitCycleProvider { get; set; }

    public bool IsHalted => _halted;

    public bool IsStopped => _stopped;

    public bool NegativeFlag { get; private set; }

    public bool ZeroFlag { get; private set; }

    public bool CarryFlag { get; private set; }

    public bool OverflowFlag { get; private set; }

    public uint this[int register]
    {
        get
        {
            ValidateRegister(register);
            return _registers[register];
        }
        set
        {
            ValidateRegister(register);
            _registers[register] = value;
            if (register == 15)
            {
                InvalidateInstructionPrefetch();
            }
        }
    }

    public uint Pc
    {
        get => _registers[15];
        private set => _registers[15] = value;
    }

    public void Reset(bool useBios)
    {
        Array.Clear(_registers);
        Array.Clear(_sharedHighRegisters);
        Array.Clear(_fiqHighRegisters);
        _bankedRegisters.Clear();
        _savedProgramStatusRegisters.Clear();
        _noBiosIrqActive = false;
        _noBiosIrqReturnPc = 0;
        _noBiosIrqExitLr = 0;
        _noBiosIrqReturnCpsr = 0;
        _noBiosIrqReturnThumb = false;
        _noBiosIrqFramePointer = 0;
        _noBiosIrqPendingInterrupts = 0;
        _noBiosIrqStack.Clear();
        _hleInterruptWaitActive = false;
        _hleInterruptWaitFlags = 0;
        _halted = false;
        _stopped = false;
        InvalidateInstructionPrefetch();
        Mode = CpuMode.System;
        ThumbState = false;
        IrqDisabled = true;
        NegativeFlag = false;
        ZeroFlag = false;
        CarryFlag = false;
        OverflowFlag = false;
        if (useBios)
        {
            SwitchMode(CpuMode.Supervisor);
            Pc = GbaMemoryMap.BiosStart;
        }
        else
        {
            Mode = CpuMode.System;
            IrqDisabled = false;
            _registers[13] = 0x0300_7F00;
            _registers[14] = GbaMemoryMap.RomEntryPoint;
            _bankedRegisters[CpuMode.Irq] = new BankedRegisters(0x0300_7FA0, 0);
            _bankedRegisters[CpuMode.Supervisor] = new BankedRegisters(0x0300_7FE0, 0);
            Pc = GbaMemoryMap.RomEntryPoint;
            Cpsr = BuildCpsr();
        }
    }

    public void SetIrqEnabled(bool enabled)
    {
        IrqDisabled = !enabled;
        Cpsr = BuildCpsr();
    }

    public int Step()
    {
        if (_halted || _stopped)
        {
            if (!TryWakeFromPowerDown())
            {
                return _stopped ? 1 : GetHleInterruptWaitCycles();
            }
        }

        if (_hleInterruptWaitActive && !_noBiosIrqActive)
        {
            if (TryCompleteHleInterruptWait())
            {
                return 3;
            }

            if (TryEnterPendingInterrupt(out var waitInterruptCycles))
            {
                return waitInterruptCycles;
            }

            return GetHleInterruptWaitCycles();
        }

        if (TryEnterPendingInterrupt(out var interruptCycles))
        {
            return interruptCycles;
        }

        if (ThumbState)
        {
            var pc = Pc;
            var instruction = Fetch16();
            try
            {
                return ExecuteThumb(instruction) + _lastInstructionFetchCyclesExtra;
            }
            catch (NotSupportedException ex)
            {
                throw new NotSupportedException($"{ex.Message} at PC=0x{pc:X8}", ex);
            }
        }

        var armPc = Pc;
        var armInstruction = Fetch32();
        try
        {
            return ExecuteArm(armInstruction) + _lastInstructionFetchCyclesExtra;
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException($"{ex.Message} at PC=0x{armPc:X8}", ex);
        }
    }

    public bool TryEnterPendingInterrupt() => TryEnterPendingInterrupt(out _);

    private bool TryEnterPendingInterrupt(out int cycles)
    {
        cycles = 3;
        var pendingInterrupts = (ushort)(_bus.InterruptEnable & _bus.InterruptFlags);
        if (IrqDisabled || !_bus.InterruptMasterEnable || pendingInterrupts == 0)
        {
            return false;
        }

        var noBiosLinkRegister = ThumbState ? Pc + 2 : Pc + 4;
        var returnCpsr = Cpsr;
        if (!_bus.HasBios)
        {
            if (!_noBiosIrqActive && IsExecutingNoBiosIrqHandler())
            {
                return false;
            }

            if (_noBiosIrqActive)
            {
                _noBiosIrqStack.Push(CaptureNoBiosIrqContext());
            }

            _noBiosIrqActive = true;
            _noBiosIrqReturnPc = Pc;
            _noBiosIrqExitLr = noBiosLinkRegister;
            _noBiosIrqReturnCpsr = returnCpsr;
            _noBiosIrqReturnThumb = ThumbState;
            _noBiosIrqPendingInterrupts = pendingInterrupts;
            SaveNoBiosIrqRegisters();
            EnterException(CpuMode.Irq, vector: 0x18, noBiosLinkRegister, disableIrq: true);
            PushNoBiosIrqFrame();
            _registers[14] = NoBiosIrqBiosReturnAddress;
            var handler = _bus.Read32(0x0300_7FFC);
            InterruptEntered?.Invoke(_noBiosIrqReturnPc, handler, _bus.InterruptEnable, _bus.InterruptFlags, _bus.InterruptMasterEnable);
            if (handler != 0)
            {
                _registers[0] = 0x0400_0000;
                BranchToAddress(handler);
            }

            cycles = NoBiosIrqWrapperCycles;
            return true;
        }

        var returnPc = Pc;
        EnterException(CpuMode.Irq, vector: 0x18, linkRegister: Pc + 4, disableIrq: true);
        InterruptEntered?.Invoke(returnPc, 0x18, _bus.InterruptEnable, _bus.InterruptFlags, _bus.InterruptMasterEnable);
        return true;
    }

    public ushort Fetch16()
    {
        var biosFetch = Pc < GbaMemoryMap.BiosSize;
        var address = Pc;
        _bus.SetBiosAccessible(biosFetch);
        var instruction = _thumbPrefetchValid && _thumbPrefetchAddress == address
            ? _thumbPrefetchInstruction
            : _bus.Read16(address);
        _bus.SetOpenBus((uint)(instruction | (instruction << 16)));
        if (biosFetch)
        {
            _bus.SetBiosOpenBus((uint)(instruction | (instruction << 16)));
        }

        PrefetchThumbInstruction(address + 2);
        CaptureInstructionFetchCycles(address, 2);
        Pc += 2;
        return instruction;
    }

    public uint Fetch32()
    {
        var biosFetch = Pc < GbaMemoryMap.BiosSize;
        var address = Pc;
        _bus.SetBiosAccessible(biosFetch);
        var instruction = _armPrefetchValid && _armPrefetchAddress == address
            ? _armPrefetchInstruction
            : _bus.Read32(address);
        _bus.SetOpenBus(instruction);
        if (biosFetch)
        {
            _bus.SetBiosOpenBus(instruction);
        }

        PrefetchArmInstruction(address + 4);
        CaptureInstructionFetchCycles(address, 4);
        Pc += 4;
        return instruction;
    }

    private int ExecuteArm(uint instruction)
    {
        if (!ConditionPassed((int)(instruction >> 28)))
        {
            return 1;
        }

        if ((instruction & 0x0FFF_FFF0) == 0x012F_FF10)
        {
            BranchExchange((int)(instruction & 0xF));
            return 3;
        }

        if ((instruction & 0x0FBF_0FFF) == 0x010F_0000)
        {
            return ExecuteArmMrs(instruction);
        }

        if ((instruction & 0x0DB0_F000) == 0x0120_F000)
        {
            return ExecuteArmMsr(instruction);
        }

        if ((instruction & 0x0FC0_00F0) == 0x0000_0090)
        {
            return ExecuteArmMultiply(instruction);
        }

        if ((instruction & 0x0F80_00F0) == 0x0080_0090
            || ((instruction & (1u << 20)) == 0 && (instruction & 0x0F80_00F0) == 0x0080_00F0))
        {
            return ExecuteArmLongMultiply(instruction);
        }

        if ((instruction & 0x0FB0_0FF0) == 0x0100_0090)
        {
            return ExecuteArmSingleDataSwap(instruction);
        }

        if ((instruction & 0x0E00_0090) == 0x0000_0090)
        {
            return ExecuteArmHalfwordDataTransfer(instruction);
        }

        if ((instruction & 0x0F00_0000) == 0x0F00_0000)
        {
            return ExecuteSoftwareInterrupt((instruction >> 16) & 0xFF);
        }

        if ((instruction & 0x0E00_0000) == 0x0A00_0000)
        {
            if ((instruction & (1u << 24)) != 0)
            {
                _registers[14] = Pc;
            }

            var signedOffset = SignExtend24(instruction & 0x00FF_FFFF) << 2;
            Pc = (uint)((int)Pc + 4 + signedOffset);
            return 3;
        }

        if ((instruction & 0x0C00_0000) == 0x0400_0000)
        {
            return ExecuteArmSingleDataTransfer(instruction);
        }

        if ((instruction & 0x0E00_0000) == 0x0800_0000)
        {
            return ExecuteArmBlockDataTransfer(instruction);
        }

        if ((instruction & 0x0C00_0000) == 0)
        {
            return ExecuteArmDataProcessing(instruction);
        }

        throw new NotSupportedException($"ARM instruction 0x{instruction:X8} is not implemented yet.");
    }

    private int ExecuteArmMultiply(uint instruction)
    {
        var accumulate = (instruction & (1u << 21)) != 0;
        var setFlags = (instruction & (1u << 20)) != 0;
        var rd = (int)((instruction >> 16) & 0xF);
        var rn = (int)((instruction >> 12) & 0xF);
        var rs = (int)((instruction >> 8) & 0xF);
        var rm = (int)(instruction & 0xF);
        var result = _registers[rm] * _registers[rs];

        if (accumulate)
        {
            result += _registers[rn];
        }

        WriteRegister(rd, result);
        if (setFlags)
        {
            SetNzFlags(result);
        }

        var internalCycles = MultiplyInternalCycles(_registers[rs]);
        return (accumulate ? 2 : 1) + internalCycles;
    }

    private int ExecuteArmLongMultiply(uint instruction)
    {
        var signed = (instruction & (1u << 22)) != 0;
        var accumulate = (instruction & (1u << 21)) != 0;
        var setFlags = (instruction & (1u << 20)) != 0;
        var rdHi = (int)((instruction >> 16) & 0xF);
        var rdLo = (int)((instruction >> 12) & 0xF);
        var rs = (int)((instruction >> 8) & 0xF);
        var rm = (int)(instruction & 0xF);

        ulong result;
        if (signed)
        {
            result = unchecked((ulong)((long)(int)_registers[rm] * (long)(int)_registers[rs]));
        }
        else
        {
            result = (ulong)_registers[rm] * _registers[rs];
        }

        if (accumulate)
        {
            result += ((ulong)_registers[rdHi] << 32) | _registers[rdLo];
        }

        _registers[rdLo] = (uint)result;
        _registers[rdHi] = (uint)(result >> 32);

        if (setFlags)
        {
            NegativeFlag = (result & 0x8000_0000_0000_0000UL) != 0;
            ZeroFlag = result == 0;
            Cpsr = BuildCpsr();
        }

        var internalCycles = MultiplyInternalCycles(_registers[rs]);
        return (accumulate ? 3 : 2) + internalCycles;
    }

    private static int MultiplyInternalCycles(uint value)
    {
        if ((value & 0xFFFF_FF00u) is 0x0000_0000u or 0xFFFF_FF00u)
        {
            return 1;
        }

        if ((value & 0xFFFF_0000u) is 0x0000_0000u or 0xFFFF_0000u)
        {
            return 2;
        }

        if ((value & 0xFF00_0000u) is 0x0000_0000u or 0xFF00_0000u)
        {
            return 3;
        }

        return 4;
    }

    private int ExecuteArmMrs(uint instruction)
    {
        var readSpsr = (instruction & (1u << 22)) != 0;
        var rd = (int)((instruction >> 12) & 0xF);
        WriteRegister(rd, readSpsr ? GetSpsr(Mode) : Cpsr);
        return 1;
    }

    private int ExecuteArmMsr(uint instruction)
    {
        var writeSpsr = (instruction & (1u << 22)) != 0;
        var fieldMask = (int)((instruction >> 16) & 0xF);
        var immediate = (instruction & (1u << 25)) != 0;
        var operand = immediate
            ? DecodeArmShifterOperand(instruction, immediate: true, updateCarry: false).Value
            : _registers[instruction & 0xF];
        var mask = BuildPsrWriteMask(fieldMask);

        if (writeSpsr)
        {
            SetSpsr(Mode, (GetSpsr(Mode) & ~mask) | (operand & mask));
        }
        else
        {
            ApplyCpsr((Cpsr & ~mask) | (operand & mask));
        }

        return 1;
    }

    private int ExecuteArmDataProcessing(uint instruction)
    {
        var immediate = (instruction & (1u << 25)) != 0;
        var opcode = (int)((instruction >> 21) & 0xF);
        var setFlags = (instruction & (1u << 20)) != 0;
        var rn = (int)((instruction >> 16) & 0xF);
        var rd = (int)((instruction >> 12) & 0xF);
        var registerShift = !immediate && (instruction & (1u << 4)) != 0;
        var operand1 = registerShift && rn == 15 ? Pc + 8u : ReadRegisterWithPipeline(rn);
        var shifter = DecodeArmShifterOperand(instruction, immediate, updateCarry: setFlags);
        var operand2 = shifter.Value;
        if (setFlags
            && rd == 15
            && opcode is >= 0x8 and <= 0xB
            && Mode is not CpuMode.User and not CpuMode.System)
        {
            var spsr = GetSpsr(Mode);
            ApplyCpsr(spsr);
            return 1;
        }

        switch (opcode)
        {
            case 0x0:
            {
                var result = operand1 & operand2;
                WriteDataProcessingResult(rd, result, setFlags, () => SetLogicFlags(result, shifter.CarryOut));
                break;
            }

            case 0x1:
            {
                var result = operand1 ^ operand2;
                WriteDataProcessingResult(rd, result, setFlags, () => SetLogicFlags(result, shifter.CarryOut));
                break;
            }

            case 0x2:
            {
                var result = operand1 - operand2;
                WriteDataProcessingResult(rd, result, setFlags, () => SetSubFlags(operand1, operand2, result));
                break;
            }

            case 0x3:
            {
                var result = operand2 - operand1;
                WriteDataProcessingResult(rd, result, setFlags, () => SetSubFlags(operand2, operand1, result));
                break;
            }

            case 0x4:
            {
                var result = operand1 + operand2;
                WriteDataProcessingResult(rd, result, setFlags, () => SetAddFlags(operand1, operand2, result));
                break;
            }

            case 0x5:
            {
                var carry = CarryFlag ? 1u : 0u;
                var result = operand1 + operand2 + carry;
                WriteDataProcessingResult(rd, result, setFlags, () => SetAddWithCarryFlags(operand1, operand2, carry, result));
                break;
            }

            case 0x6:
            {
                var borrow = CarryFlag ? 0u : 1u;
                var result = operand1 - operand2 - borrow;
                WriteDataProcessingResult(rd, result, setFlags, () => SetSubWithBorrowFlags(operand1, operand2, borrow, result));
                break;
            }

            case 0x7:
            {
                var borrow = CarryFlag ? 0u : 1u;
                var result = operand2 - operand1 - borrow;
                WriteDataProcessingResult(rd, result, setFlags, () => SetSubWithBorrowFlags(operand2, operand1, borrow, result));
                break;
            }

            case 0x8:
                SetLogicFlags(operand1 & operand2, shifter.CarryOut);
                break;

            case 0x9:
                SetLogicFlags(operand1 ^ operand2, shifter.CarryOut);
                break;

            case 0xA:
            {
                var result = operand1 - operand2;
                SetSubFlags(operand1, operand2, result);
                break;
            }

            case 0xB:
            {
                var result = operand1 + operand2;
                SetAddFlags(operand1, operand2, result);
                break;
            }

            case 0xC:
            {
                var result = operand1 | operand2;
                WriteDataProcessingResult(rd, result, setFlags, () => SetLogicFlags(result, shifter.CarryOut));
                break;
            }

            case 0xD:
                WriteDataProcessingResult(rd, operand2, setFlags, () => SetLogicFlags(operand2, shifter.CarryOut));
                break;

            case 0xE:
            {
                var result = operand1 & ~operand2;
                WriteDataProcessingResult(rd, result, setFlags, () => SetLogicFlags(result, shifter.CarryOut));
                break;
            }

            case 0xF:
            {
                var result = ~operand2;
                WriteDataProcessingResult(rd, result, setFlags, () => SetLogicFlags(result, shifter.CarryOut));
                break;
            }

            default:
                throw new NotSupportedException($"ARM data-processing opcode 0x{opcode:X} is not implemented yet.");
        }

        return 1;
    }

    private int ExecuteArmSingleDataTransfer(uint instruction)
    {
        var immediateOffset = (instruction & (1u << 25)) == 0;
        var preIndex = (instruction & (1u << 24)) != 0;
        var addOffset = (instruction & (1u << 23)) != 0;
        var byteTransfer = (instruction & (1u << 22)) != 0;
        var writeBack = (instruction & (1u << 21)) != 0;
        var load = (instruction & (1u << 20)) != 0;
        var rn = (int)((instruction >> 16) & 0xF);
        var rd = (int)((instruction >> 12) & 0xF);
        var baseAddress = ReadRegisterWithPipeline(rn);
        var offset = immediateOffset
            ? instruction & 0xFFF
            : DecodeArmShifterOperand(instruction, immediate: false, updateCarry: false).Value;
        var effectiveAddress = preIndex
            ? addOffset ? baseAddress + offset : baseAddress - offset
            : baseAddress;
        var writeBackAddress = addOffset ? baseAddress + offset : baseAddress - offset;

        var accessBytes = byteTransfer ? 1 : 4;
        var accessCyclesExtra = AccessCyclesExtra(effectiveAddress, accessBytes);
        if (load)
        {
            var value = byteTransfer ? _bus.Read8(effectiveAddress) : _bus.Read32(effectiveAddress);
            WriteRegister(rd, value);
        }
        else if (byteTransfer)
        {
            _bus.Write8(effectiveAddress, (byte)ReadArmStoreRegister(rd));
        }
        else
        {
            _bus.Write32(effectiveAddress, ReadArmStoreRegister(rd));
        }

        if ((!preIndex || writeBack) && !(load && rn == rd))
        {
            WriteRegister(rn, writeBackAddress);
        }

        return (load ? 3 : 2) + accessCyclesExtra;
    }

    private int ExecuteArmHalfwordDataTransfer(uint instruction)
    {
        var preIndex = (instruction & (1u << 24)) != 0;
        var addOffset = (instruction & (1u << 23)) != 0;
        var immediateOffset = (instruction & (1u << 22)) != 0;
        var writeBack = (instruction & (1u << 21)) != 0;
        var load = (instruction & (1u << 20)) != 0;
        var rn = (int)((instruction >> 16) & 0xF);
        var rd = (int)((instruction >> 12) & 0xF);
        var transferKind = (int)((instruction >> 5) & 0x3);
        var baseAddress = ReadRegisterWithPipeline(rn);
        var offset = immediateOffset
            ? ((instruction >> 4) & 0xF0) | (instruction & 0xF)
            : _registers[instruction & 0xF];
        var effectiveAddress = preIndex
            ? addOffset ? baseAddress + offset : baseAddress - offset
            : baseAddress;
        var writeBackAddress = addOffset ? baseAddress + offset : baseAddress - offset;

        var accessBytes = transferKind is 1 or 3 ? 2 : 1;
        var accessCyclesExtra = AccessCyclesExtra(effectiveAddress, accessBytes);
        if (load)
        {
            var value = transferKind switch
            {
                1 => ReadCpuHalfword(effectiveAddress),
                2 => SignExtend8(_bus.Read8(effectiveAddress)),
                3 => ReadCpuSignedHalfword(effectiveAddress),
                _ => throw new NotSupportedException($"ARM halfword transfer kind {transferKind} is not supported for load.")
            };
            WriteRegister(rd, value);
        }
        else
        {
            if (transferKind != 1)
            {
                return EnterUndefinedInstructionException();
            }

            _bus.Write16(effectiveAddress, (ushort)ReadArmStoreRegister(rd));
        }

        if ((!preIndex || writeBack) && !(load && rn == rd))
        {
            WriteRegister(rn, writeBackAddress);
        }

        return (load ? 3 : 2) + accessCyclesExtra;
    }

    private int EnterUndefinedInstructionException()
    {
        return EnterException(CpuMode.Undefined, vector: 0x04, linkRegister: Pc, disableIrq: false);
    }

    private int ExecuteArmSingleDataSwap(uint instruction)
    {
        var byteTransfer = (instruction & (1u << 22)) != 0;
        var rn = (int)((instruction >> 16) & 0xF);
        var rd = (int)((instruction >> 12) & 0xF);
        var rm = (int)(instruction & 0xF);
        var address = ReadRegisterWithPipeline(rn);
        var storeValue = ReadArmStoreRegister(rm);

        var accessBytes = byteTransfer ? 1 : 4;
        var accessCyclesExtra = AccessCyclesExtra(address, accessBytes) * 2;
        if (byteTransfer)
        {
            var loaded = _bus.Read8(address);
            _bus.Write8(address, (byte)storeValue);
            WriteRegister(rd, loaded);
        }
        else
        {
            var loaded = _bus.Read32(address);
            _bus.Write32(address, storeValue);
            WriteRegister(rd, loaded);
        }

        return 4 + accessCyclesExtra;
    }

    private int ExecuteArmBlockDataTransfer(uint instruction)
    {
        var preIndex = (instruction & (1u << 24)) != 0;
        var addOffset = (instruction & (1u << 23)) != 0;
        var psrOrUserTransfer = (instruction & (1u << 22)) != 0;
        var writeBack = (instruction & (1u << 21)) != 0;
        var load = (instruction & (1u << 20)) != 0;
        var rn = (int)((instruction >> 16) & 0xF);
        var registerList = (int)(instruction & 0xFFFF);
        var transfers = registerList == 0 ? 16 : CountBits(registerList);
        var userModeTransfer = psrOrUserTransfer && (registerList & (1 << 15)) == 0;
        var baseAddress = _registers[rn];
        var startAddress = addOffset
            ? baseAddress + (preIndex ? 4u : 0u)
            : baseAddress - (uint)(transfers * 4) + (preIndex ? 0u : 4u);
        var writeBackAddress = addOffset
            ? baseAddress + (uint)(transfers * 4)
            : baseAddress - (uint)(transfers * 4);
        var address = startAddress & ~3u;
        var accessCyclesExtra = 0;
        var transferIndex = 0;
        uint? deferredPcAndSLoad = null;

        if (registerList == 0)
        {
            accessCyclesExtra += AccessCyclesExtra(address, 4);
            if (load)
            {
                var value = _bus.Read32(address);
                if (psrOrUserTransfer && Mode is not CpuMode.User and not CpuMode.System)
                {
                    deferredPcAndSLoad = value;
                }
                else
                {
                    WriteRegister(15, value);
                }
            }
            else
            {
                _bus.Write32(address, ReadArmStoreRegister(15));
            }
        }
        else
        {
            for (var register = 0; register < 16; register++)
            {
                if ((registerList & (1 << register)) == 0)
                {
                    continue;
                }

                if (load)
                {
                    accessCyclesExtra += AccessCyclesExtra(address, 4, transferIndex > 0);
                    if (userModeTransfer)
                    {
                        WriteUserRegister(register, _bus.Read32(address));
                    }
                    else if (register == 15 && psrOrUserTransfer && Mode is not CpuMode.User and not CpuMode.System)
                    {
                        deferredPcAndSLoad = _bus.Read32(address);
                    }
                    else
                    {
                        WriteRegister(register, _bus.Read32(address));
                    }
                }
                else
                {
                    accessCyclesExtra += AccessCyclesExtra(address, 4, transferIndex > 0);
                    var value = userModeTransfer ? ReadUserRegister(register) : ReadArmStoreRegister(register);
                    if (writeBack && register == rn && transferIndex > 0)
                    {
                        value = writeBackAddress;
                    }

                    _bus.Write32(address, value);
                }

                address += 4;
                transferIndex++;
            }
        }

        if (writeBack && !(load && (registerList & (1 << rn)) != 0))
        {
            WriteRegister(rn, writeBackAddress);
        }

        if (deferredPcAndSLoad.HasValue)
        {
            RestoreCpsrFromSpsrAndWritePc(deferredPcAndSLoad.Value);
        }

        return (load ? 2 : 1) + transfers + accessCyclesExtra;
    }

    private int ExecuteThumb(ushort instruction)
    {
        if ((instruction & 0xF800) == 0x2000)
        {
            var rd = (instruction >> 8) & 0x7;
            var value = (uint)(instruction & 0xFF);
            WriteRegister(rd, value);
            SetNzFlags(value);
            return 1;
        }

        if ((instruction & 0xF800) == 0x2800)
        {
            var rn = (instruction >> 8) & 0x7;
            var operand1 = _registers[rn];
            var operand2 = (uint)(instruction & 0xFF);
            SetSubFlags(operand1, operand2, operand1 - operand2);
            return 1;
        }

        if ((instruction & 0xF800) == 0x3000)
        {
            var rd = (instruction >> 8) & 0x7;
            var operand1 = _registers[rd];
            var operand2 = (uint)(instruction & 0xFF);
            var result = operand1 + operand2;
            WriteRegister(rd, result);
            SetAddFlags(operand1, operand2, result);
            return 1;
        }

        if ((instruction & 0xF800) == 0x3800)
        {
            var rd = (instruction >> 8) & 0x7;
            var operand1 = _registers[rd];
            var operand2 = (uint)(instruction & 0xFF);
            var result = operand1 - operand2;
            WriteRegister(rd, result);
            SetSubFlags(operand1, operand2, result);
            return 1;
        }

        if ((instruction & 0xF800) is 0x1800 or 0x1A00)
        {
            return ExecuteThumbAddSubtract(instruction);
        }

        if ((instruction & 0xE000) == 0x0000)
        {
            return ExecuteThumbMoveShiftedRegister(instruction);
        }

        if ((instruction & 0xFC00) == 0x4000)
        {
            return ExecuteThumbAlu(instruction);
        }

        if ((instruction & 0xFC00) == 0x4400)
        {
            return ExecuteThumbHighRegisterOrBranchExchange(instruction);
        }

        if ((instruction & 0xF200) == 0x5200)
        {
            return ExecuteThumbLoadStoreSignExtended(instruction);
        }

        if ((instruction & 0xF000) == 0x5000)
        {
            return ExecuteThumbLoadStoreRegisterOffset(instruction);
        }

        if ((instruction & 0xE000) == 0x6000 || (instruction & 0xF000) == 0x8000)
        {
            return ExecuteThumbLoadStoreImmediateOffset(instruction);
        }

        if ((instruction & 0xF800) == 0x4800)
        {
            var rd = (instruction >> 8) & 0x7;
            var address = ((Pc + 2) & ~2u) + (uint)((instruction & 0xFF) << 2);
            WriteRegister(rd, _bus.Read32(address));
            return 3 + AccessCyclesExtra(address, 4);
        }

        if ((instruction & 0xF000) == 0x9000)
        {
            var rd = (instruction >> 8) & 0x7;
            var address = _registers[13] + (uint)((instruction & 0xFF) << 2);
            if ((instruction & (1 << 11)) == 0)
            {
                _bus.Write32(address, _registers[rd]);
                return 2 + AccessCyclesExtra(address, 4);
            }

            WriteRegister(rd, _bus.Read32(address));
            return 3 + AccessCyclesExtra(address, 4);
        }

        if ((instruction & 0xF000) == 0xA000)
        {
            var rd = (instruction >> 8) & 0x7;
            var offset = (uint)((instruction & 0xFF) << 2);
            WriteRegister(rd, ((instruction & (1 << 11)) == 0 ? (Pc + 2) & ~2u : _registers[13]) + offset);
            return 1;
        }

        if ((instruction & 0xFF00) == 0xB000)
        {
            var offset = (uint)((instruction & 0x7F) << 2);
            _registers[13] = (instruction & (1 << 7)) == 0 ? _registers[13] + offset : _registers[13] - offset;
            return 1;
        }

        if ((instruction & 0xF600) == 0xB400)
        {
            return ExecuteThumbPushPop(instruction);
        }

        if ((instruction & 0xF000) == 0xC000)
        {
            return ExecuteThumbLoadStoreMultiple(instruction);
        }

        if ((instruction & 0xF000) == 0xD000 && (instruction & 0x0F00) != 0x0F00)
        {
            var condition = (instruction >> 8) & 0xF;
            if (ConditionPassed(condition))
            {
                Pc = (uint)((int)Pc + 2 + (sbyte)(instruction & 0xFF) * 2);
                return 3;
            }

            return 1;
        }

        if ((instruction & 0xFF00) == 0xDF00)
        {
            return ExecuteSoftwareInterrupt((uint)(instruction & 0xFF));
        }

        if ((instruction & 0xFF87) == 0x4700)
        {
            BranchExchange((instruction >> 3) & 0xF);
            return 3;
        }

        if ((instruction & 0xF800) == 0xE000)
        {
            var offset = SignExtend11((uint)(instruction & 0x7FF)) << 1;
            Pc = (uint)((int)Pc + 2 + offset);
            return 3;
        }

        if ((instruction & 0xF800) == 0xF000)
        {
            _registers[14] = (uint)((int)Pc + 2 + (SignExtend11((uint)(instruction & 0x7FF)) << 12));
            return 1;
        }

        if ((instruction & 0xF800) == 0xF800)
        {
            var target = _registers[14] + (uint)((instruction & 0x7FF) << 1);
            _registers[14] = Pc | 1u;
            Pc = target & ~1u;
            ThumbState = true;
            Cpsr = BuildCpsr();
            return 4;
        }

        throw new NotSupportedException($"Thumb instruction 0x{instruction:X4} is not implemented yet.");
    }

    private int ExecuteThumbMoveShiftedRegister(ushort instruction)
    {
        var op = (instruction >> 11) & 0x3;
        var offset = (instruction >> 6) & 0x1F;
        var rs = (instruction >> 3) & 0x7;
        var rd = instruction & 0x7;
        var value = _registers[rs];
        uint result;

        switch (op)
        {
            case 0:
                result = offset == 0 ? value : value << offset;
                CarryFlag = offset == 0 ? CarryFlag : (value & (1u << (32 - offset))) != 0;
                break;

            case 1:
                result = offset == 0 ? 0 : value >> offset;
                CarryFlag = offset == 0 ? (value & 0x8000_0000) != 0 : (value & (1u << ((int)offset - 1))) != 0;
                break;

            case 2:
                result = offset == 0
                    ? ((value & 0x8000_0000) != 0 ? 0xFFFF_FFFF : 0)
                    : (uint)((int)value >> (int)offset);
                CarryFlag = offset == 0 ? (value & 0x8000_0000) != 0 : (value & (1u << ((int)offset - 1))) != 0;
                break;

            default:
                throw new NotSupportedException($"Thumb shifted-register op {op} is not implemented.");
        }

        WriteRegister(rd, result);
        SetNzFlags(result);
        return 1;
    }

    private int ExecuteThumbAddSubtract(ushort instruction)
    {
        var immediate = (instruction & (1 << 10)) != 0;
        var subtract = (instruction & (1 << 9)) != 0;
        var operand = immediate ? (uint)((instruction >> 6) & 0x7) : _registers[(instruction >> 6) & 0x7];
        var rs = (instruction >> 3) & 0x7;
        var rd = instruction & 0x7;
        var left = _registers[rs];
        var result = subtract ? left - operand : left + operand;

        WriteRegister(rd, result);
        if (subtract)
        {
            SetSubFlags(left, operand, result);
        }
        else
        {
            SetAddFlags(left, operand, result);
        }

        return 1;
    }

    private int ExecuteThumbAlu(ushort instruction)
    {
        var op = (instruction >> 6) & 0xF;
        var rs = (instruction >> 3) & 0x7;
        var rd = instruction & 0x7;
        var left = _registers[rd];
        var right = _registers[rs];

        switch (op)
        {
            case 0x0:
                WriteRegister(rd, left & right);
                SetNzFlags(_registers[rd]);
                break;

            case 0x1:
                WriteRegister(rd, left ^ right);
                SetNzFlags(_registers[rd]);
                break;

            case 0x2:
            {
                var result = ShiftLogicalLeft(left, (int)(right & 0xFF), updateCarry: true);
                CarryFlag = result.CarryOut;
                WriteRegister(rd, result.Value);
                SetNzFlags(_registers[rd]);
                break;
            }

            case 0x3:
            {
                var result = ShiftLogicalRight(left, (int)(right & 0xFF), updateCarry: true);
                CarryFlag = result.CarryOut;
                WriteRegister(rd, result.Value);
                SetNzFlags(_registers[rd]);
                break;
            }

            case 0x4:
            {
                var result = ShiftArithmeticRight(left, (int)(right & 0xFF), updateCarry: true);
                CarryFlag = result.CarryOut;
                WriteRegister(rd, result.Value);
                SetNzFlags(_registers[rd]);
                break;
            }

            case 0x5:
            {
                var carry = CarryFlag ? 1u : 0u;
                var result = left + right + carry;
                WriteRegister(rd, result);
                SetAddWithCarryFlags(left, right, carry, result);
                break;
            }

            case 0x6:
            {
                var borrow = CarryFlag ? 0u : 1u;
                var result = left - right - borrow;
                WriteRegister(rd, result);
                SetSubWithBorrowFlags(left, right, borrow, result);
                break;
            }

            case 0x7:
            {
                var result = ShiftRotateRight(left, (int)(right & 0xFF), updateCarry: true);
                CarryFlag = result.CarryOut;
                WriteRegister(rd, result.Value);
                SetNzFlags(_registers[rd]);
                break;
            }

            case 0x8:
                SetNzFlags(left & right);
                break;

            case 0x9:
            {
                var result = 0u - right;
                WriteRegister(rd, result);
                SetSubFlags(0, right, result);
                break;
            }

            case 0xA:
            {
                var result = left - right;
                SetSubFlags(left, right, result);
                break;
            }

            case 0xB:
            {
                var result = left + right;
                SetAddFlags(left, right, result);
                break;
            }

            case 0xC:
                WriteRegister(rd, left | right);
                SetNzFlags(_registers[rd]);
                break;

            case 0xD:
            {
                var result = left * right;
                WriteRegister(rd, result);
                SetNzFlags(result);
                return 2;
            }

            case 0xE:
                WriteRegister(rd, left & ~right);
                SetNzFlags(_registers[rd]);
                break;

            case 0xF:
                WriteRegister(rd, ~right);
                SetNzFlags(_registers[rd]);
                break;

            default:
                throw new NotSupportedException($"Thumb ALU opcode 0x{op:X} is not implemented yet.");
        }

        return 1;
    }

    private int ExecuteThumbHighRegisterOrBranchExchange(ushort instruction)
    {
        var op = (instruction >> 8) & 0x3;
        var highDestination = (instruction & (1 << 7)) != 0;
        var highSource = (instruction & (1 << 6)) != 0;
        var rs = ((instruction >> 3) & 0x7) | (highSource ? 8 : 0);
        var rd = (instruction & 0x7) | (highDestination ? 8 : 0);
        var left = ReadRegisterWithPipeline(rd);
        var right = ReadRegisterWithPipeline(rs);

        switch (op)
        {
            case 0:
                WriteRegister(rd, left + right);
                return 1;

            case 1:
                SetSubFlags(left, right, left - right);
                return 1;

            case 2:
                WriteRegister(rd, right);
                return 1;

            case 3:
                BranchExchange(rs);
                return 3;

            default:
                throw new UnreachableException();
        }
    }

    private int ExecuteThumbLoadStoreRegisterOffset(ushort instruction)
    {
        var load = (instruction & (1 << 11)) != 0;
        var byteTransfer = (instruction & (1 << 10)) != 0;
        var ro = (instruction >> 6) & 0x7;
        var rb = (instruction >> 3) & 0x7;
        var rd = instruction & 0x7;
        var address = _registers[rb] + _registers[ro];

        if (load)
        {
            var accessBytes = byteTransfer ? 1 : 4;
            WriteRegister(rd, byteTransfer ? _bus.Read8(address) : _bus.Read32(address));
            return 3 + AccessCyclesExtra(address, accessBytes);
        }

        if (byteTransfer)
        {
            _bus.Write8(address, (byte)_registers[rd]);
        }
        else
        {
            _bus.Write32(address, _registers[rd]);
        }

        return 2 + AccessCyclesExtra(address, byteTransfer ? 1 : 4);
    }

    private int ExecuteThumbLoadStoreSignExtended(ushort instruction)
    {
        var op = (instruction >> 10) & 0x3;
        var ro = (instruction >> 6) & 0x7;
        var rb = (instruction >> 3) & 0x7;
        var rd = instruction & 0x7;
        var address = _registers[rb] + _registers[ro];

        switch (op)
        {
            case 0:
                _bus.Write16(address, (ushort)_registers[rd]);
                return 2 + AccessCyclesExtra(address, 2);

            case 1:
                WriteRegister(rd, SignExtend8(_bus.Read8(address)));
                return 3 + AccessCyclesExtra(address, 1);

            case 2:
                WriteRegister(rd, ReadCpuHalfword(address));
                return 3 + AccessCyclesExtra(address, 2);

            case 3:
                WriteRegister(rd, ReadCpuSignedHalfword(address));
                return 3 + AccessCyclesExtra(address, 2);

            default:
                throw new UnreachableException();
        }
    }

    private int ExecuteThumbLoadStoreImmediateOffset(ushort instruction)
    {
        var halfwordTransfer = (instruction & 0xF000) == 0x8000;
        var load = (instruction & (1 << 11)) != 0;
        var byteTransfer = !halfwordTransfer && (instruction & (1 << 12)) != 0;
        var offset5 = (instruction >> 6) & 0x1F;
        var rb = (instruction >> 3) & 0x7;
        var rd = instruction & 0x7;
        var scale = halfwordTransfer ? 1 : byteTransfer ? 0 : 2;
        var address = _registers[rb] + (uint)(offset5 << scale);

        if (load)
        {
            var value = halfwordTransfer ? ReadCpuHalfword(address) : byteTransfer ? _bus.Read8(address) : _bus.Read32(address);
            WriteRegister(rd, value);
            return 3 + AccessCyclesExtra(address, halfwordTransfer ? 2 : byteTransfer ? 1 : 4);
        }

        if (halfwordTransfer)
        {
            _bus.Write16(address, (ushort)_registers[rd]);
        }
        else if (byteTransfer)
        {
            _bus.Write8(address, (byte)_registers[rd]);
        }
        else
        {
            _bus.Write32(address, _registers[rd]);
        }

        return 2 + AccessCyclesExtra(address, halfwordTransfer ? 2 : byteTransfer ? 1 : 4);
    }

    private int ExecuteThumbPushPop(ushort instruction)
    {
        var pop = (instruction & (1 << 11)) != 0;
        var includeSpecial = (instruction & (1 << 8)) != 0;
        var registerList = instruction & 0xFF;
        var transfers = CountBits(registerList) + (includeSpecial ? 1 : 0);
        var accessCyclesExtra = 0;
        var transferIndex = 0;

        if (pop)
        {
            for (var register = 0; register < 8; register++)
            {
                if ((registerList & (1 << register)) == 0)
                {
                    continue;
                }

                accessCyclesExtra += AccessCyclesExtra(_registers[13], 4, transferIndex > 0);
                WriteRegister(register, _bus.Read32(_registers[13]));
                _registers[13] += 4;
                transferIndex++;
            }

            if (includeSpecial)
            {
                accessCyclesExtra += AccessCyclesExtra(_registers[13], 4, transferIndex > 0);
                WriteRegister(15, _bus.Read32(_registers[13]) & ~1u);
                _registers[13] += 4;
            }
        }
        else
        {
            _registers[13] -= (uint)(transfers * 4);
            var address = _registers[13];
            for (var register = 0; register < 8; register++)
            {
                if ((registerList & (1 << register)) == 0)
                {
                    continue;
                }

                accessCyclesExtra += AccessCyclesExtra(address, 4, transferIndex > 0);
                _bus.Write32(address, _registers[register]);
                address += 4;
                transferIndex++;
            }

            if (includeSpecial)
            {
                accessCyclesExtra += AccessCyclesExtra(address, 4, transferIndex > 0);
                _bus.Write32(address, _registers[14]);
            }
        }

        return (pop ? 3 + transfers : 2 + transfers) + accessCyclesExtra;
    }

    private int ExecuteThumbLoadStoreMultiple(ushort instruction)
    {
        var load = (instruction & (1 << 11)) != 0;
        var rb = (instruction >> 8) & 0x7;
        var registerList = instruction & 0xFF;
        var address = _registers[rb] & ~3u;
        var transfers = CountBits(registerList);
        var writeBackAddress = _registers[rb] + (uint)(transfers * 4);
        var accessCyclesExtra = 0;
        var transferIndex = 0;

        if (registerList == 0)
        {
            accessCyclesExtra += AccessCyclesExtra(address, 4);
            if (load)
            {
                WriteRegister(15, _bus.Read32(address));
            }
            else
            {
                _bus.Write32(address, Pc + 4);
            }

            _registers[rb] += 0x40;
            return (load ? 5 : 4) + accessCyclesExtra;
        }

        for (var register = 0; register < 8; register++)
        {
            if ((registerList & (1 << register)) == 0)
            {
                continue;
            }

            if (load)
            {
                accessCyclesExtra += AccessCyclesExtra(address, 4, transferIndex > 0);
                WriteRegister(register, _bus.Read32(address));
            }
            else
            {
                accessCyclesExtra += AccessCyclesExtra(address, 4, transferIndex > 0);
                var value = register == rb && transferIndex > 0
                    ? writeBackAddress
                    : _registers[register];
                _bus.Write32(address, value);
            }

            address += 4;
            transferIndex++;
        }

        if (!load || (registerList & (1 << (int)rb)) == 0)
        {
            _registers[rb] = writeBackAddress;
        }

        return (load ? 3 + transfers : 2 + transfers) + accessCyclesExtra;
    }

    private bool ConditionPassed(int condition) => condition switch
    {
        0x0 => ZeroFlag,
        0x1 => !ZeroFlag,
        0x2 => CarryFlag,
        0x3 => !CarryFlag,
        0x4 => NegativeFlag,
        0x5 => !NegativeFlag,
        0x6 => OverflowFlag,
        0x7 => !OverflowFlag,
        0x8 => CarryFlag && !ZeroFlag,
        0x9 => !CarryFlag || ZeroFlag,
        0xA => NegativeFlag == OverflowFlag,
        0xB => NegativeFlag != OverflowFlag,
        0xC => !ZeroFlag && NegativeFlag == OverflowFlag,
        0xD => ZeroFlag || NegativeFlag != OverflowFlag,
        0xE => true,
        _ => false
    };

    private uint ReadRegisterWithPipeline(int register)
    {
        ValidateRegister(register);
        return register == 15 ? Pc + (ThumbState ? 2u : 4u) : _registers[register];
    }

    private uint ReadArmStoreRegister(int register)
    {
        ValidateRegister(register);
        return register == 15 ? Pc + 8u : _registers[register];
    }

    private void CaptureInstructionFetchCycles(uint address, int bytes)
    {
        var sequential = address == _lastInstructionFetchEnd;
        _lastInstructionFetchCyclesExtra = AccessCyclesExtra(address, bytes, sequential);
        _lastInstructionFetchEnd = address + (uint)bytes;
    }

    private void PrefetchArmInstruction(uint address)
    {
        var biosFetch = address < GbaMemoryMap.BiosSize;
        _bus.SetBiosAccessible(biosFetch);
        _armPrefetchAddress = address;
        _armPrefetchInstruction = _bus.Read32(address);
        _armPrefetchValid = true;
        _thumbPrefetchValid = false;
    }

    private void PrefetchThumbInstruction(uint address)
    {
        var biosFetch = address < GbaMemoryMap.BiosSize;
        _bus.SetBiosAccessible(biosFetch);
        _thumbPrefetchAddress = address;
        _thumbPrefetchInstruction = _bus.Read16(address);
        _thumbPrefetchValid = true;
        _armPrefetchValid = false;
    }

    private void InvalidateInstructionPrefetch()
    {
        _armPrefetchValid = false;
        _thumbPrefetchValid = false;
        _lastInstructionFetchEnd = uint.MaxValue;
        _lastInstructionFetchCyclesExtra = 0;
    }

    private int AccessCyclesExtra(uint address, int bytes, bool sequential = false)
        => Math.Max(0, _bus.GetCpuAccessCycles(address, bytes, sequential) - 1);

    private void WriteRegister(int register, uint value)
    {
        ValidateRegister(register);
        if (register == 15)
        {
            if (TryCompleteNoBiosIrqReturn(value))
            {
                return;
            }

            Pc = ThumbState ? value & ~1u : value & ~3u;
            InvalidateInstructionPrefetch();
            return;
        }

        _registers[register] = value;
    }

    private bool TryCompleteNoBiosIrqReturn(uint target)
    {
        if (!_noBiosIrqActive
            || Mode != CpuMode.Irq
            || !IsNoBiosIrqReturnTarget(target))
        {
            return false;
        }

        CompleteNoBiosIrqReturn(target);
        return true;
    }

    private uint ReadUserRegister(int register)
    {
        ValidateRegister(register);
        if (Mode == CpuMode.Fiq && register is >= 8 and <= 12)
        {
            return _sharedHighRegisters[register - 8];
        }

        if (register is 13 or 14 && Mode is not CpuMode.User and not CpuMode.System)
        {
            return _bankedRegisters.GetValueOrDefault(CpuMode.User).Get(register);
        }

        return register == 15 ? ReadRegisterWithPipeline(register) : _registers[register];
    }

    private void WriteUserRegister(int register, uint value)
    {
        ValidateRegister(register);
        if (Mode == CpuMode.Fiq && register is >= 8 and <= 12)
        {
            _sharedHighRegisters[register - 8] = value;
            return;
        }

        if (register is 13 or 14 && Mode is not CpuMode.User and not CpuMode.System)
        {
            var banked = _bankedRegisters.GetValueOrDefault(CpuMode.User);
            _bankedRegisters[CpuMode.User] = register == 13
                ? banked with { Sp = value }
                : banked with { Lr = value };
            return;
        }

        WriteRegister(register, value);
    }

    private void BranchExchange(int register)
    {
        var address = ReadRegisterWithPipeline(register);
        if (TryCompleteNoBiosIrqReturn(address))
        {
            return;
        }

        BranchToAddress(address);
    }

    private bool IsNoBiosIrqReturnTarget(uint address)
    {
        var aligned = address & ~1u;
        return aligned == NoBiosIrqBiosReturnAddress
            || aligned == NoBiosIrqBiosReturnAddress - 4
            || aligned == (_noBiosIrqExitLr & ~1u)
            || aligned == (_noBiosIrqReturnPc & ~1u);
    }

    private bool IsExecutingNoBiosIrqHandler()
    {
        const uint iwramEnd = GbaMemoryMap.IwramStart + GbaMemoryMap.IwramSize;
        var handler = _bus.Read32(0x0300_7FFC) & ~1u;
        if (handler < GbaMemoryMap.IwramStart || handler >= iwramEnd)
        {
            return false;
        }

        var pc = Pc & ~1u;
        return pc >= handler && pc < Math.Min(handler + 0x800u, iwramEnd);
    }

    private void CompleteNoBiosIrqReturn(uint target)
    {
        var originalReturnPc = _noBiosIrqReturnPc;
        var framePointer = _noBiosIrqFramePointer;
        var r0 = _bus.Read32(framePointer);
        var r1 = _bus.Read32(framePointer + 4);
        var r2 = _bus.Read32(framePointer + 8);
        var r3 = _bus.Read32(framePointer + 12);
        var r12 = _bus.Read32(framePointer + 16);
        var savedLinkRegister = _bus.Read32(framePointer + 20);
        var returnPc = savedLinkRegister - (_noBiosIrqReturnThumb ? 2u : 4u);
        var returnCpsr = _noBiosIrqReturnCpsr;
        var pendingInterrupts = _noBiosIrqPendingInterrupts;
        _registers[13] = framePointer + 24;
        _noBiosIrqActive = false;
        if (pendingInterrupts != 0)
        {
            _bus.Write16(IoRegisters.IF, pendingInterrupts);
        }

        ApplyCpsr(returnCpsr);
        _registers[0] = r0;
        _registers[1] = r1;
        _registers[2] = r2;
        _registers[3] = r3;
        _registers[12] = r12;
        Pc = ThumbState ? returnPc & ~1u : returnPc & ~3u;
        Cpsr = BuildCpsr();
        if (_noBiosIrqStack.Count > 0)
        {
            RestoreNoBiosIrqContext(_noBiosIrqStack.Pop());
        }

        InterruptReturned?.Invoke(originalReturnPc, target, _bus.InterruptEnable, _bus.InterruptFlags, _bus.InterruptMasterEnable);
    }

    private void BranchToAddress(uint address)
    {
        ThumbState = (address & 1) != 0;
        Pc = ThumbState ? address & ~1u : address & ~3u;
        Cpsr = BuildCpsr();
        InvalidateInstructionPrefetch();
    }

    private void SaveNoBiosIrqRegisters()
    {
        _noBiosIrqSavedRegisters[0] = _registers[0];
        _noBiosIrqSavedRegisters[1] = _registers[1];
        _noBiosIrqSavedRegisters[2] = _registers[2];
        _noBiosIrqSavedRegisters[3] = _registers[3];
        _noBiosIrqSavedRegisters[4] = _registers[12];
        _noBiosIrqSavedRegisters[5] = _registers[14];
    }

    private void PushNoBiosIrqFrame()
    {
        _registers[13] -= 24;
        _noBiosIrqFramePointer = _registers[13];
        _bus.Write32(_noBiosIrqFramePointer, _noBiosIrqSavedRegisters[0]);
        _bus.Write32(_noBiosIrqFramePointer + 4, _noBiosIrqSavedRegisters[1]);
        _bus.Write32(_noBiosIrqFramePointer + 8, _noBiosIrqSavedRegisters[2]);
        _bus.Write32(_noBiosIrqFramePointer + 12, _noBiosIrqSavedRegisters[3]);
        _bus.Write32(_noBiosIrqFramePointer + 16, _noBiosIrqSavedRegisters[4]);
        _bus.Write32(_noBiosIrqFramePointer + 20, _noBiosIrqExitLr);
    }

    private void RestoreNoBiosIrqRegisters()
    {
        _registers[0] = _noBiosIrqSavedRegisters[0];
        _registers[1] = _noBiosIrqSavedRegisters[1];
        _registers[2] = _noBiosIrqSavedRegisters[2];
        _registers[3] = _noBiosIrqSavedRegisters[3];
        _registers[12] = _noBiosIrqSavedRegisters[4];
        _registers[14] = _noBiosIrqSavedRegisters[5];
    }

    private NoBiosIrqContext CaptureNoBiosIrqContext()
        => new(
            _noBiosIrqReturnPc,
            _noBiosIrqExitLr,
            _noBiosIrqReturnCpsr,
            _noBiosIrqReturnThumb,
            _noBiosIrqFramePointer,
            _noBiosIrqPendingInterrupts,
            _registers[13],
            _registers[14],
            _noBiosIrqSavedRegisters[0],
            _noBiosIrqSavedRegisters[1],
            _noBiosIrqSavedRegisters[2],
            _noBiosIrqSavedRegisters[3],
            _noBiosIrqSavedRegisters[4],
            _noBiosIrqSavedRegisters[5]);

    private void RestoreNoBiosIrqContext(NoBiosIrqContext context)
    {
        _noBiosIrqActive = true;
        _noBiosIrqReturnPc = context.ReturnPc;
        _noBiosIrqExitLr = context.ExitLr;
        _noBiosIrqReturnCpsr = context.ReturnCpsr;
        _noBiosIrqReturnThumb = context.ReturnThumb;
        _noBiosIrqFramePointer = context.FramePointer;
        _noBiosIrqPendingInterrupts = context.PendingInterrupts;
        SetSpsr(CpuMode.Irq, context.ReturnCpsr);
        _registers[13] = context.IrqSp;
        _registers[14] = context.IrqLr;
        _noBiosIrqSavedRegisters[0] = context.R0;
        _noBiosIrqSavedRegisters[1] = context.R1;
        _noBiosIrqSavedRegisters[2] = context.R2;
        _noBiosIrqSavedRegisters[3] = context.R3;
        _noBiosIrqSavedRegisters[4] = context.R12;
        _noBiosIrqSavedRegisters[5] = context.R14;
    }

    private readonly record struct NoBiosIrqContext(
        uint ReturnPc,
        uint ExitLr,
        uint ReturnCpsr,
        bool ReturnThumb,
        uint FramePointer,
        ushort PendingInterrupts,
        uint IrqSp,
        uint IrqLr,
        uint R0,
        uint R1,
        uint R2,
        uint R3,
        uint R12,
        uint R14);

    private int EnterException(CpuMode mode, uint vector, uint linkRegister, bool disableIrq)
    {
        _savedProgramStatusRegisters[mode] = Cpsr;
        SwitchMode(mode);
        ThumbState = false;
        if (disableIrq)
        {
            IrqDisabled = true;
        }

        _registers[14] = linkRegister;
        Pc = vector;
        Cpsr = BuildCpsr();
        InvalidateInstructionPrefetch();
        return 3;
    }

    private int ExecuteSoftwareInterrupt(uint number)
    {
        SoftwareInterruptCalled?.Invoke(number, Pc - (ThumbState ? 2u : 4u));

        if (_bus.HasBios)
        {
            return EnterException(CpuMode.Supervisor, vector: 0x08, linkRegister: Pc, disableIrq: true);
        }

        return ExecuteHleSwi(number);
    }

    private int ExecuteHleSwi(uint number)
    {
        switch (number)
        {
            case 0x00: // SoftReset
                HleSoftReset();
                return 3;

            case 0x01: // RegisterRamReset
                _bus.RegisterRamReset(_registers[0]);
                return 3;

            case 0x02: // Halt
                EnterPowerDown(stop: false);
                return 3;

            case 0x03: // Stop
                EnterPowerDown(stop: true);
                return 3;

            case 0x04: // IntrWait
                return HleIntrWait(_registers[0] != 0, (ushort)_registers[1]);

            case 0x05: // VBlankIntrWait
                return HleIntrWait(clearOldFlags: true, IoRegisters.InterruptVBlank);

            case 0x06: // Div
            {
                var numerator = unchecked((int)_registers[0]);
                var denominator = unchecked((int)_registers[1]);
                if (denominator == 0)
                {
                    _registers[0] = 0;
                    _registers[1] = (uint)numerator;
                    _registers[3] = 0;
                    return 3;
                }

                var quotient = numerator / denominator;
                var remainder = numerator % denominator;
                _registers[0] = unchecked((uint)quotient);
                _registers[1] = unchecked((uint)remainder);
                _registers[3] = unchecked((uint)Math.Abs(quotient));
                return 3;
            }

            case 0x07: // DivArm
            {
                var denominator = unchecked((int)_registers[0]);
                var numerator = unchecked((int)_registers[1]);
                if (denominator == 0)
                {
                    _registers[0] = 0;
                    _registers[1] = (uint)numerator;
                    _registers[3] = 0;
                    return 3;
                }

                var quotient = numerator / denominator;
                var remainder = numerator % denominator;
                _registers[0] = unchecked((uint)quotient);
                _registers[1] = unchecked((uint)remainder);
                _registers[3] = unchecked((uint)Math.Abs(quotient));
                return 3;
            }

            case 0x08: // Sqrt
                _registers[0] = (uint)Math.Sqrt(_registers[0]);
                return 3;

            case 0x09: // ArcTan
                HleArcTan();
                return 3;

            case 0x0A: // ArcTan2
                HleArcTan2();
                return 3;

            case 0x0B: // CpuSet
                return HleCpuSet(fast: false);

            case 0x0C: // CpuFastSet
                return HleCpuSet(fast: true);

            case 0x0D: // GetBiosChecksum
                _registers[0] = 0xBAAE_187Fu;
                _registers[1] = 1;
                _registers[3] = GbaMemoryMap.BiosSize;
                return 3;

            case 0x0E: // BgAffineSet
                HleBgAffineSet();
                return 3;

            case 0x0F: // ObjAffineSet
                HleObjAffineSet();
                return 3;

            case 0x11: // LZ77UnCompWram
                HleLz77Uncomp(vram: false);
                return 3;

            case 0x12: // LZ77UnCompVram
                HleLz77Uncomp(vram: true);
                return 3;

            case 0x13: // HuffUnComp
                HleHuffUncomp();
                return 3;

            case 0x10: // BitUnPack
                HleBitUnpack();
                return 3;

            case 0x14: // RLUnCompWram
                HleRlUncomp(vram: false);
                return 3;

            case 0x15: // RLUnCompVram
                HleRlUncomp(vram: true);
                return 3;

            case 0x16: // Diff8bitUnFilterWram
                HleDiff8bitUnfilter(vram: false);
                return 3;

            case 0x17: // Diff8bitUnFilterVram
                HleDiff8bitUnfilter(vram: true);
                return 3;

            case 0x18: // Diff16bitUnFilter
                HleDiff16bitUnfilter();
                return 3;

            case 0x19: // SoundBias
                HleSoundBias();
                return 3;

            case 0x1F: // MidiKey2Freq
                HleMidiKey2Freq();
                return 3;

            default:
                return 3;
        }
    }

    private void HleSoftReset()
    {
        var returnToEwram = _bus.Read8(0x0300_7FFA) != 0;
        for (var address = 0x0300_7E00u; address < 0x0300_8000u; address += 4)
        {
            _bus.Write32(address, 0);
        }

        Array.Clear(_registers, 0, 13);
        _registers[13] = 0x0300_7F00;
        _registers[14] = 0;
        _bankedRegisters[CpuMode.Irq] = new BankedRegisters(0x0300_7FA0, 0);
        _bankedRegisters[CpuMode.Supervisor] = new BankedRegisters(0x0300_7FE0, 0);
        _savedProgramStatusRegisters[CpuMode.Irq] = 0;
        _savedProgramStatusRegisters[CpuMode.Supervisor] = 0;
        _noBiosIrqActive = false;
        _noBiosIrqStack.Clear();
        _hleInterruptWaitActive = false;
        _hleInterruptWaitFlags = 0;
        Mode = CpuMode.System;
        ThumbState = false;
        IrqDisabled = false;
        NegativeFlag = false;
        ZeroFlag = false;
        CarryFlag = false;
        OverflowFlag = false;
        Pc = returnToEwram ? GbaMemoryMap.EwramStart : GbaMemoryMap.GamePakRomStart;
        Cpsr = BuildCpsr();
    }

    private void HleArcTan()
    {
        var tangent = unchecked((short)(_registers[0] & 0xFFFF)) / 16384.0;
        _registers[0] = (uint)((int)Math.Round(Math.Atan(tangent) * (32768.0 / Math.PI)) & 0xFFFF);
    }

    private void HleArcTan2()
    {
        var x = unchecked((short)(_registers[0] & 0xFFFF)) / 16384.0;
        var y = unchecked((short)(_registers[1] & 0xFFFF)) / 16384.0;
        var angle = Math.Atan2(y, x);
        if (angle < 0)
        {
            angle += Math.Tau;
        }

        _registers[0] = (uint)((int)Math.Round(angle * (65536.0 / Math.Tau)) & 0xFFFF);
    }

    private void HleMidiKey2Freq()
    {
        var waveData = _registers[0];
        var midiKey = _registers[1] & 0xFF;
        var fineAdjust = _registers[2] & 0xFF;
        var baseFrequency = _bus.Read32(waveData + 4);
        var divisor = Math.Pow(2.0, (180.0 - midiKey - (fineAdjust / 256.0)) / 12.0);
        _registers[0] = divisor == 0 ? 0 : (uint)(baseFrequency / divisor);
    }

    private void HleSoundBias()
    {
        var current = _bus.PeekIo16(IoRegisters.SOUNDBIAS);
        var target = (_registers[0] & 1) == 0 ? 0u : 0x200u;
        _bus.PokeIo16(IoRegisters.SOUNDBIAS, (ushort)(((uint)current & 0xFC00u) | target));
    }

    private int HleCpuSet(bool fast)
    {
        var source = _registers[0];
        var destination = _registers[1];
        var control = _registers[2];
        var fill = (control & (1u << 24)) != 0;
        var word = fast || (control & (1u << 26)) != 0;
        var units = control & 0x1F_FFFF;
        if (fast && units != 0)
        {
            units = (units + 7) & ~7u;
        }

        var transferBytes = units * (word ? 4u : 2u);
        if (SourceTouchesBios(source, transferBytes))
        {
            return 3;
        }

        var cycles = 3;
        if (word)
        {
            source &= ~3u;
            destination &= ~3u;
            var value = 0u;
            var finalSource = source;
            if (fill)
            {
                value = _bus.Read32(source);
                cycles += GetCpuSetAccessCycles(source, 4);
            }

            for (var i = 0; i < units; i++)
            {
                if (!fill)
                {
                    value = _bus.Read32(source);
                    cycles += GetCpuSetAccessCycles(source, 4);
                    source += 4;
                    finalSource = source;
                }

                _bus.Write32(destination, value);
                cycles += GetCpuSetAccessCycles(destination, 4);
                destination += 4;
            }

            _registers[0] = fill ? finalSource : source;
            _registers[1] = destination;
            _registers[3] = value;
        }
        else
        {
            source &= ~1u;
            destination &= ~1u;
            var value = 0;
            var finalSource = source;
            if (fill)
            {
                value = _bus.Read16(source);
                cycles += GetCpuSetAccessCycles(source, 2);
            }

            for (var i = 0; i < units; i++)
            {
                if (!fill)
                {
                    value = _bus.Read16(source);
                    cycles += GetCpuSetAccessCycles(source, 2);
                    source += 2;
                    finalSource = source;
                }

                _bus.Write16(destination, (ushort)value);
                cycles += GetCpuSetAccessCycles(destination, 2);
                destination += 2;
            }

            _registers[0] = fill ? finalSource : source;
            _registers[1] = destination;
            _registers[3] = (uint)(ushort)value;
        }

        return cycles;
    }

    private int GetCpuSetAccessCycles(uint address, int bytes)
        => Math.Max(1, _bus.GetCpuAccessCycles(address, bytes, sequential: false));

    private static bool SourceTouchesBios(uint source, uint byteCount)
    {
        if (byteCount == 0)
        {
            return false;
        }

        if (source < GbaMemoryMap.BiosSize)
        {
            return true;
        }

        var last = source + byteCount - 1;
        return last < source && last < GbaMemoryMap.BiosSize;
    }

    private int HleIntrWait(bool clearOldFlags, ushort flags)
    {
        _bus.InterruptMasterEnable = true;
        var biosInterruptFlags = _bus.BiosInterruptFlags;
        if (clearOldFlags)
        {
            biosInterruptFlags = (ushort)(biosInterruptFlags & ~flags);
            _bus.BiosInterruptFlags = biosInterruptFlags;
            _bus.Write16(IoRegisters.IF, flags);
        }

        if ((biosInterruptFlags & flags) != 0)
        {
            _bus.BiosInterruptFlags = (ushort)(biosInterruptFlags & ~flags);
            return 3;
        }

        _hleInterruptWaitActive = flags != 0;
        _hleInterruptWaitFlags = flags;
        return GetHleInterruptWaitCycles();
    }

    private bool TryCompleteHleInterruptWait()
    {
        var flags = _hleInterruptWaitFlags;
        var biosInterruptFlags = _bus.BiosInterruptFlags;
        if (flags == 0 || (biosInterruptFlags & flags) == 0)
        {
            return false;
        }

        _bus.BiosInterruptFlags = (ushort)(biosInterruptFlags & ~flags);
        _hleInterruptWaitActive = false;
        _hleInterruptWaitFlags = 0;
        return true;
    }

    private int GetHleInterruptWaitCycles()
    {
        const int maxWaitChunkCycles = 1024;
        var cycles = InterruptWaitCycleProvider?.Invoke() ?? maxWaitChunkCycles;
        if (cycles < 0)
        {
            return maxWaitChunkCycles;
        }

        return Math.Min(cycles, maxWaitChunkCycles);
    }

    private void EnterPowerDown(bool stop)
    {
        _halted = !stop;
        _stopped = stop;
    }

    private bool TryWakeFromPowerDown()
    {
        var pendingInterrupts = (ushort)(_bus.InterruptEnable & _bus.InterruptFlags);
        if (_stopped)
        {
            const ushort stopWakeInterrupts = IoRegisters.InterruptSerial
                | IoRegisters.InterruptKeypad
                | IoRegisters.InterruptGamePak;
            pendingInterrupts &= stopWakeInterrupts;
        }

        if (pendingInterrupts == 0)
        {
            return false;
        }

        _halted = false;
        _stopped = false;
        return true;
    }

    private void HleBgAffineSet()
    {
        var source = _registers[0];
        var destination = _registers[1];
        var count = _registers[2];

        for (var i = 0u; i < count; i++)
        {
            var textureCenterX = unchecked((int)_bus.Read32(source));
            var textureCenterY = unchecked((int)_bus.Read32(source + 4));
            var screenCenterX = ReadSigned16(source + 8);
            var screenCenterY = ReadSigned16(source + 10);
            var scaleX = ReadSigned16(source + 12);
            var scaleY = ReadSigned16(source + 14);
            var angle = _bus.Read16(source + 16);
            var (pa, pb, pc, pd) = CalculateAffineParameters(scaleX, scaleY, angle);
            var startX = textureCenterX - (pa * screenCenterX + pb * screenCenterY);
            var startY = textureCenterY - (pc * screenCenterX + pd * screenCenterY);

            _bus.Write16(destination, unchecked((ushort)pa));
            _bus.Write16(destination + 2, unchecked((ushort)pb));
            _bus.Write16(destination + 4, unchecked((ushort)pc));
            _bus.Write16(destination + 6, unchecked((ushort)pd));
            _bus.Write32(destination + 8, unchecked((uint)startX));
            _bus.Write32(destination + 12, unchecked((uint)startY));

            source += 20;
            destination += 16;
        }
    }

    private void HleObjAffineSet()
    {
        var source = _registers[0];
        var destination = _registers[1];
        var count = _registers[2];
        var offset = _registers[3];

        for (var i = 0u; i < count; i++)
        {
            var scaleX = ReadSigned16(source);
            var scaleY = ReadSigned16(source + 2);
            var angle = _bus.Read16(source + 4);
            var (pa, pb, pc, pd) = CalculateAffineParameters(scaleX, scaleY, angle);

            _bus.Write16(destination, unchecked((ushort)pa));
            _bus.Write16(destination + offset, unchecked((ushort)pb));
            _bus.Write16(destination + offset * 2, unchecked((ushort)pc));
            _bus.Write16(destination + offset * 3, unchecked((ushort)pd));

            source += 8;
            destination += offset * 4;
        }
    }

    private (int Pa, int Pb, int Pc, int Pd) CalculateAffineParameters(int scaleX, int scaleY, ushort angle)
    {
        var radians = (angle >> 8) * (Math.Tau / 256.0);
        var cosine = (int)Math.Round(Math.Cos(radians) * 256.0);
        var sine = (int)Math.Round(Math.Sin(radians) * 256.0);
        return (
            (scaleX * cosine) >> 8,
            -(scaleX * sine) >> 8,
            (scaleY * sine) >> 8,
            (scaleY * cosine) >> 8);
    }

    private int ReadSigned16(uint address)
        => unchecked((short)_bus.Read16(address));

    private uint ReadCpuHalfword(uint address)
    {
        var value = _bus.Read16(address & ~1u);
        return (address & 1) == 0
            ? value
            : ((uint)value >> 8) | ((uint)value << 24);
    }

    private uint ReadCpuSignedHalfword(uint address)
    {
        return (address & 1) == 0
            ? SignExtend16(_bus.Read16(address))
            : SignExtend8(_bus.Read8(address));
    }

    private void HleLz77Uncomp(bool vram)
    {
        var source = _registers[0];
        var destination = _registers[1];
        var header = _bus.Read32(source);
        if ((header & 0xFF) != 0x10)
        {
            return;
        }

        source += 4;
        var length = header >> 8;
        var written = 0u;
        byte flags = 0;
        var mask = 0;

        while (written < length)
        {
            if (mask == 0)
            {
                flags = _bus.Read8(source++);
                mask = 0x80;
            }

            if ((flags & mask) == 0)
            {
                WriteDecompressedByte(destination, written++, _bus.Read8(source++), vram);
            }
            else
            {
                var first = _bus.Read8(source++);
                var second = _bus.Read8(source++);
                var count = (uint)((first >> 4) + 3);
                var displacement = (uint)(((first & 0xF) << 8) | second) + 1;
                for (var i = 0u; i < count && written < length; i++)
                {
                    var value = ReadDecompressedByte(destination, written - displacement, vram);
                    WriteDecompressedByte(destination, written++, value, vram);
                }
            }

            mask >>= 1;
        }
    }

    private byte ReadDecompressedByte(uint destination, uint offset, bool vram)
        => _bus.Read8(destination + offset);

    private void WriteDecompressedByte(uint destination, uint offset, byte value, bool vram)
    {
        if (!vram)
        {
            _bus.Write8(destination + offset, value);
            return;
        }

        var address = destination + (offset & ~1u);
        var current = _bus.Read16(address);
        var merged = (offset & 1) == 0
            ? (ushort)((current & 0xFF00) | value)
            : (ushort)((current & 0x00FF) | (value << 8));
        _bus.Write16(address, merged);
    }

    private void HleRlUncomp(bool vram)
    {
        var source = _registers[0];
        var destination = _registers[1];
        var header = _bus.Read32(source);
        source += 4;
        var length = header >> 8;
        var written = 0u;

        while (written < length)
        {
            var flag = _bus.Read8(source++);
            if ((flag & 0x80) == 0)
            {
                var count = (uint)((flag & 0x7F) + 1);
                for (var i = 0u; i < count && written < length; i++)
                {
                    WriteDecompressedByte(destination, written++, _bus.Read8(source++), vram);
                }
            }
            else
            {
                var count = (uint)((flag & 0x7F) + 3);
                var value = _bus.Read8(source++);
                for (var i = 0u; i < count && written < length; i++)
                {
                    WriteDecompressedByte(destination, written++, value, vram);
                }
            }
        }
    }

    private void HleHuffUncomp()
    {
        var source = _registers[0] & ~3u;
        var destination = _registers[1];
        var header = _bus.Read32(source);
        var remaining = (int)(header >> 8);
        var unitBits = (int)(header & 0xF);
        if (unitBits == 0)
        {
            unitBits = 8;
        }

        if (unitBits == 1 || 32 % unitBits != 0)
        {
            return;
        }

        var unitMask = unitBits == 32 ? 0xFFFF_FFFFu : (1u << unitBits) - 1;
        var treeSize = (_bus.Read8(source + 4) << 1) + 1;
        var treeBase = source + 5;
        source += 5 + (uint)treeSize;

        var nodePointer = treeBase;
        var node = _bus.Read8(nodePointer);
        var output = 0u;
        var outputBits = 0;

        while (remaining > 0)
        {
            var bitstream = _bus.Read32(source);
            source += 4;

            for (var bitsRemaining = 32; bitsRemaining > 0 && remaining > 0; bitsRemaining--, bitstream <<= 1)
            {
                var next = (nodePointer & ~1u) + (uint)((node & 0x3F) * 2 + 2);
                int value;
                if ((bitstream & 0x8000_0000) != 0)
                {
                    if ((node & 0x40) == 0)
                    {
                        nodePointer = next + 1;
                        node = _bus.Read8(nodePointer);
                        continue;
                    }

                    value = _bus.Read8(next + 1);
                }
                else
                {
                    if ((node & 0x80) == 0)
                    {
                        nodePointer = next;
                        node = _bus.Read8(nodePointer);
                        continue;
                    }

                    value = _bus.Read8(next);
                }

                output |= ((uint)value & unitMask) << outputBits;
                outputBits += unitBits;
                nodePointer = treeBase;
                node = _bus.Read8(nodePointer);

                if (outputBits == 32)
                {
                    _bus.Write32(destination, output);
                    destination += 4;
                    remaining -= 4;
                    output = 0;
                    outputBits = 0;
                }
            }
        }

        _registers[0] = source;
        _registers[1] = destination;
    }

    private void HleBitUnpack()
    {
        var source = _registers[0];
        var destination = _registers[1];
        var info = _registers[2];
        var sourceLength = _bus.Read16(info);
        var sourceWidth = _bus.Read8(info + 2);
        var destinationWidth = _bus.Read8(info + 3);
        var offsetControl = _bus.Read32(info + 4);
        var offset = offsetControl & 0x7FFF_FFFF;
        var addOffsetToZero = (offsetControl & 0x8000_0000) != 0;
        var destinationMask = destinationWidth == 32 ? 0xFFFF_FFFFu : (1u << destinationWidth) - 1;
        var destinationWord = 0u;
        var destinationBits = 0;

        for (var i = 0; i < sourceLength; i++)
        {
            var sourceByte = _bus.Read8(source++);
            for (var bit = 0; bit < 8; bit += sourceWidth)
            {
                var value = (uint)((sourceByte >> bit) & ((1 << sourceWidth) - 1));
                if (value != 0 || addOffsetToZero)
                {
                    value += offset;
                }

                destinationWord |= (value & destinationMask) << destinationBits;
                destinationBits += destinationWidth;
                if (destinationBits == 32)
                {
                    _bus.Write32(destination, destinationWord);
                    destination += 4;
                    destinationWord = 0;
                    destinationBits = 0;
                }
            }
        }

        if (destinationBits != 0)
        {
            _bus.Write32(destination, destinationWord);
        }
    }

    private void HleDiff8bitUnfilter(bool vram)
    {
        var source = _registers[0];
        var destination = _registers[1];
        var header = _bus.Read32(source);
        source += 4;
        var length = header >> 8;
        if (length == 0)
        {
            return;
        }

        var value = _bus.Read8(source++);
        WriteDecompressedByte(destination, 0, value, vram);
        for (var written = 1u; written < length; written++)
        {
            value = unchecked((byte)(value + _bus.Read8(source++)));
            WriteDecompressedByte(destination, written, value, vram);
        }
    }

    private void HleDiff16bitUnfilter()
    {
        var source = _registers[0];
        var destination = _registers[1];
        var header = _bus.Read32(source);
        source += 4;
        var length = header >> 8;
        if (length == 0)
        {
            return;
        }

        var value = _bus.Read16(source);
        source += 2;
        _bus.Write16(destination, value);
        for (var written = 2u; written < length; written += 2)
        {
            value = unchecked((ushort)(value + _bus.Read16(source)));
            source += 2;
            _bus.Write16(destination + written, value);
        }
    }

    private uint GetSpsr(CpuMode mode)
        => _savedProgramStatusRegisters.GetValueOrDefault(mode, Cpsr);

    private void SetSpsr(CpuMode mode, uint value)
    {
        if (mode is CpuMode.User or CpuMode.System)
        {
            return;
        }

        _savedProgramStatusRegisters[mode] = value;
    }

    private void ApplyCpsr(uint value)
    {
        NegativeFlag = (value & (1u << 31)) != 0;
        ZeroFlag = (value & (1u << 30)) != 0;
        CarryFlag = (value & (1u << 29)) != 0;
        OverflowFlag = (value & (1u << 28)) != 0;
        ThumbState = (value & (1u << 5)) != 0;
        IrqDisabled = (value & (1u << 7)) != 0;

        var modeValue = (CpuMode)(value & 0x1F);
        if (Enum.IsDefined(modeValue))
        {
            SwitchMode(modeValue);
        }
        else
        {
            Cpsr = BuildCpsr();
        }
    }

    private void SwitchMode(CpuMode mode)
    {
        if (Mode == mode)
        {
            Cpsr = BuildCpsr();
            return;
        }

        SaveBankedRegisters(Mode);
        SwitchFiqHighRegisterBank(Mode, mode);
        Mode = mode;
        RestoreBankedRegisters(mode);
        Cpsr = BuildCpsr();
    }

    private void SwitchFiqHighRegisterBank(CpuMode currentMode, CpuMode nextMode)
    {
        if (currentMode == CpuMode.Fiq && nextMode != CpuMode.Fiq)
        {
            SaveHighRegisters(_fiqHighRegisters);
            RestoreHighRegisters(_sharedHighRegisters);
        }
        else if (currentMode != CpuMode.Fiq && nextMode == CpuMode.Fiq)
        {
            SaveHighRegisters(_sharedHighRegisters);
            RestoreHighRegisters(_fiqHighRegisters);
        }
    }

    private void SaveHighRegisters(uint[] bank)
    {
        for (var register = 8; register <= 12; register++)
        {
            bank[register - 8] = _registers[register];
        }
    }

    private void RestoreHighRegisters(uint[] bank)
    {
        for (var register = 8; register <= 12; register++)
        {
            _registers[register] = bank[register - 8];
        }
    }

    private void SaveBankedRegisters(CpuMode mode)
    {
        _bankedRegisters[GetSpLrBank(mode)] = new BankedRegisters(_registers[13], _registers[14]);
    }

    private void RestoreBankedRegisters(CpuMode mode)
    {
        if (_bankedRegisters.TryGetValue(GetSpLrBank(mode), out var banked))
        {
            _registers[13] = banked.Sp;
            _registers[14] = banked.Lr;
        }
        else
        {
            _registers[13] = 0;
            _registers[14] = 0;
        }
    }

    private void SetNzFlags(uint result)
    {
        NegativeFlag = (result & 0x8000_0000) != 0;
        ZeroFlag = result == 0;
        Cpsr = BuildCpsr();
    }

    private void SetLogicFlags(uint result, bool carryOut)
    {
        NegativeFlag = (result & 0x8000_0000) != 0;
        ZeroFlag = result == 0;
        CarryFlag = carryOut;
        Cpsr = BuildCpsr();
    }

    private void SetAddFlags(uint left, uint right, uint result)
    {
        SetAddWithCarryFlags(left, right, 0, result);
    }

    private void SetAddWithCarryFlags(uint left, uint right, uint carry, uint result)
    {
        SetNzFlags(result);
        CarryFlag = (ulong)left + right + carry > uint.MaxValue;
        OverflowFlag = ((~(left ^ right) & (left ^ result)) & 0x8000_0000) != 0;
        Cpsr = BuildCpsr();
    }

    private void SetSubFlags(uint left, uint right, uint result)
    {
        SetSubWithBorrowFlags(left, right, 0, result);
    }

    private void SetSubWithBorrowFlags(uint left, uint right, uint borrow, uint result)
    {
        SetNzFlags(result);
        CarryFlag = (ulong)left >= (ulong)right + borrow;
        OverflowFlag = (((left ^ right) & (left ^ result)) & 0x8000_0000) != 0;
        Cpsr = BuildCpsr();
    }

    private void WriteDataProcessingResult(int destinationRegister, uint result, bool setFlags, Action updateFlags)
    {
        if (setFlags && ShouldRestoreCpsrFromSpsrOnPcWrite(destinationRegister))
        {
            RestoreCpsrFromSpsrAndWritePc(result);
            return;
        }

        WriteRegister(destinationRegister, result);
        if (setFlags)
        {
            updateFlags();
        }
    }

    private bool ShouldRestoreCpsrFromSpsrOnPcWrite(int destinationRegister)
        => destinationRegister == 15 && Mode is not CpuMode.User and not CpuMode.System;

    private void RestoreCpsrFromSpsrAndWritePc(uint target)
    {
        if (TryCompleteNoBiosIrqReturn(target))
        {
            return;
        }

        var spsr = GetSpsr(Mode);
        ApplyCpsr(spsr);
        WriteRegister(15, target);
    }

    private uint BuildCpsr()
    {
        uint cpsr = (uint)Mode;
        if (NegativeFlag)
        {
            cpsr |= 1u << 31;
        }

        if (ZeroFlag)
        {
            cpsr |= 1u << 30;
        }

        if (CarryFlag)
        {
            cpsr |= 1u << 29;
        }

        if (OverflowFlag)
        {
            cpsr |= 1u << 28;
        }

        if (ThumbState)
        {
            cpsr |= 1u << 5;
        }

        if (IrqDisabled)
        {
            cpsr |= 1u << 7;
        }

        return cpsr;
    }

    private static int SignExtend24(uint value)
    {
        if ((value & 0x0080_0000) != 0)
        {
            value |= 0xFF00_0000;
        }

        return unchecked((int)value);
    }

    private static int SignExtend11(uint value)
    {
        if ((value & 0x400) != 0)
        {
            value |= 0xFFFF_F800;
        }

        return unchecked((short)value);
    }

    private static uint RotateRight(uint value, int bits) => (value >> bits) | (value << (32 - bits));

    private static uint SignExtend8(byte value) => unchecked((uint)(sbyte)value);

    private static uint SignExtend16(ushort value) => unchecked((uint)(short)value);

    private static uint BuildPsrWriteMask(int fieldMask)
    {
        uint mask = 0;
        if ((fieldMask & 0x1) != 0)
        {
            mask |= 0x0000_00FF;
        }

        if ((fieldMask & 0x2) != 0)
        {
            mask |= 0x0000_FF00;
        }

        if ((fieldMask & 0x4) != 0)
        {
            mask |= 0x00FF_0000;
        }

        if ((fieldMask & 0x8) != 0)
        {
            mask |= 0xFF00_0000;
        }

        return mask;
    }

    private static int CountBits(int value)
    {
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }

    private ShifterResult DecodeArmShifterOperand(uint instruction, bool immediate, bool updateCarry)
    {
        if (immediate)
        {
            var rotate = (int)((instruction >> 8) & 0xF) * 2;
            if (rotate == 0)
            {
                return new ShifterResult(instruction & 0xFF, CarryFlag);
            }

            var rotatedImmediate = RotateRight(instruction & 0xFF, rotate);
            return new ShifterResult(rotatedImmediate, (rotatedImmediate & 0x8000_0000) != 0);
        }

        var rm = (int)(instruction & 0xF);
        var type = (int)((instruction >> 5) & 0x3);
        var registerShift = (instruction & (1u << 4)) != 0;
        var value = registerShift && rm == 15 ? Pc + 8u : ReadRegisterWithPipeline(rm);
        if (!registerShift)
        {
            var amount = (int)((instruction >> 7) & 0x1F);
            return type switch
            {
                0 => ShiftLogicalLeft(value, amount, updateCarry),
                1 => ShiftLogicalRight(value, amount == 0 ? 32 : amount, updateCarry),
                2 => ShiftArithmeticRight(value, amount == 0 ? 32 : amount, updateCarry),
                3 => amount == 0 ? RotateRightExtended(value, updateCarry) : ShiftRotateRight(value, amount, updateCarry),
                _ => throw new UnreachableException()
            };
        }

        var rs = (int)((instruction >> 8) & 0xF);
        var registerAmount = (int)((rs == 15 ? Pc + 8u : ReadRegisterWithPipeline(rs)) & 0xFF);
        return type switch
        {
            0 => ShiftLogicalLeft(value, registerAmount, updateCarry),
            1 => ShiftLogicalRight(value, registerAmount, updateCarry),
            2 => ShiftArithmeticRight(value, registerAmount, updateCarry),
            3 => ShiftRotateRight(value, registerAmount, updateCarry),
            _ => throw new UnreachableException()
        };
    }

    private ShifterResult ShiftLogicalLeft(uint value, int amount, bool updateCarry)
    {
        if (amount == 0)
        {
            return new ShifterResult(value, CarryFlag);
        }

        if (amount < 32)
        {
            return new ShifterResult(value << amount, updateCarry ? (value & (1u << (32 - amount))) != 0 : CarryFlag);
        }

        if (amount == 32)
        {
            return new ShifterResult(0, updateCarry ? (value & 1) != 0 : CarryFlag);
        }

        return new ShifterResult(0, updateCarry ? false : CarryFlag);
    }

    private ShifterResult ShiftLogicalRight(uint value, int amount, bool updateCarry)
    {
        if (amount == 0)
        {
            return new ShifterResult(value, CarryFlag);
        }

        if (amount < 32)
        {
            return new ShifterResult(value >> amount, updateCarry ? (value & (1u << (amount - 1))) != 0 : CarryFlag);
        }

        if (amount == 32)
        {
            return new ShifterResult(0, updateCarry ? (value & 0x8000_0000) != 0 : CarryFlag);
        }

        return new ShifterResult(0, updateCarry ? false : CarryFlag);
    }

    private ShifterResult ShiftArithmeticRight(uint value, int amount, bool updateCarry)
    {
        if (amount == 0)
        {
            return new ShifterResult(value, CarryFlag);
        }

        if (amount < 32)
        {
            return new ShifterResult((uint)((int)value >> amount), updateCarry ? (value & (1u << (amount - 1))) != 0 : CarryFlag);
        }

        var result = (value & 0x8000_0000) != 0 ? 0xFFFF_FFFF : 0;
        return new ShifterResult(result, updateCarry ? (value & 0x8000_0000) != 0 : CarryFlag);
    }

    private ShifterResult ShiftRotateRight(uint value, int amount, bool updateCarry)
    {
        if (amount == 0)
        {
            return new ShifterResult(value, CarryFlag);
        }

        var rotate = amount & 31;
        var result = rotate == 0 ? value : RotateRight(value, rotate);
        return new ShifterResult(result, updateCarry ? (result & 0x8000_0000) != 0 : CarryFlag);
    }

    private ShifterResult RotateRightExtended(uint value, bool updateCarry)
    {
        var result = (CarryFlag ? 0x8000_0000u : 0) | (value >> 1);
        return new ShifterResult(result, updateCarry ? (value & 1) != 0 : CarryFlag);
    }

    private readonly record struct ShifterResult(uint Value, bool CarryOut);

    private readonly record struct BankedRegisters(uint Sp, uint Lr)
    {
        public uint Get(int register) => register switch
        {
            13 => Sp,
            14 => Lr,
            _ => throw new ArgumentOutOfRangeException(nameof(register), register, "Only SP and LR are banked.")
        };
    }

    private static CpuMode GetSpLrBank(CpuMode mode)
        => mode is CpuMode.User or CpuMode.System ? CpuMode.User : mode;

    private static void ValidateRegister(int register)
    {
        if ((uint)register >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(register), "ARM7TDMI has 16 visible registers.");
        }
    }
}
