using Gba.Core.Cartridges;

namespace Gba.Core.Memory;

public readonly record struct EepromTrace(string Operation, int Address, ulong Data, int AddressBits, int PendingBits);

public sealed class MemoryBus
{
    private const uint BiosInterruptFlagsAddress = 0x0300_7FF8;
    private const uint RubySapphireMapMusicStateAddress = 0x0300_06D8;
    private const uint RubySapphireBgmStatusAddress = 0x0300_7384;
    private const uint InitialBiosOpenBus = 0;
    private const uint PostStartupBiosOpenBus = 0;
    private const byte LegendsPostStartupBiosByteC3 = 0xE1;
    private const ushort InitialRemoteControl = 0x8000;
    private static readonly int[,] MultiplayerSerialTransferCycles =
    {
        { 31976, 63427, 94884, 125829 },
        { 8378, 16241, 24104, 31457 },
        { 5750, 10998, 16241, 20972 },
        { 3140, 5755, 8376, 10486 }
    };
    private readonly byte[] _bios;
    private readonly byte[] _ewram;
    private readonly byte[] _iwram;
    private readonly byte[] _io;
    private readonly byte[] _palette;
    private readonly byte[] _vram;
    private readonly byte[] _oam;
    private readonly byte[] _sram;
    private readonly List<Action<uint, int>> _ioReadObservers = [];
    private readonly List<Action<uint, int>> _ioWriteObservers = [];
    private readonly List<Action<uint, int, uint>> _memoryReadObservers = [];
    private readonly List<Action<uint, int>> _memoryWriteObservers = [];
    private readonly CartridgeRtc _rtc = new();
    private CartridgeHardware _cartridgeHardware;
    private byte[] _rom = [];
    private SaveType _saveType;
    private FlashCommandState _flashCommandState;
    private bool _flashIdMode;
    private int _flashBank;
    private bool _rubyTitleMplayStatusGuard;
    private bool _legendsBiosOpenBusGuard;
    private byte _gpioData;
    private byte _gpioDirection;
    private byte _gpioControl;
    private bool _hasGpio;
    private byte _solarCounter;
    private byte _solarLevel = 0x50;
    private bool _solarClock;
    private ushort _gyroSensorValue = 0x06C0;
    private ushort _gyroShiftValue;
    private int _gyroShiftIndex;
    private bool _gyroClock;
    private bool _gyroStart;
    private ushort _tiltX = 0x0800;
    private ushort _tiltY = 0x0800;
    private bool _tiltReady = true;
    private bool _cartridgeRumbleEnabled;
    private int _serialTransferCyclesRemaining;
    private uint _openBus;
    private uint _biosOpenBus = InitialBiosOpenBus;
    private readonly byte[] _eeprom = new byte[8 * 1024];
    private readonly List<int> _eepromInputBits = [];
    private readonly Queue<int> _eepromOutputBits = [];
    private int? _eepromAddressBits;

    public MemoryBus()
        : this(null)
    {
    }

    public MemoryBus(byte[]? bios)
    {
        _bios = bios is null ? new byte[GbaMemoryMap.BiosSize] : ValidateSize(bios, GbaMemoryMap.BiosSize, nameof(bios));
        _ewram = new byte[GbaMemoryMap.EwramSize];
        _iwram = new byte[GbaMemoryMap.IwramSize];
        _io = new byte[GbaMemoryMap.IoSize];
        _palette = new byte[GbaMemoryMap.PaletteSize];
        _vram = new byte[GbaMemoryMap.VramSize];
        _oam = new byte[GbaMemoryMap.OamSize];
        _sram = new byte[GbaMemoryMap.SramSize];
        HasBios = bios is not null && bios.Any(value => value != 0);
    }

    public bool HasBios { get; }

    public bool BiosAccessible { get; private set; }

    public ReadOnlySpan<byte> Rom => _rom;

    public SaveType SaveType => _saveType;

    public bool CartridgeRumbleEnabled => _cartridgeRumbleEnabled;

    public event Action<EepromTrace>? EepromAccessed;

    public event Action<ushort, ushort>? InterruptRequested;

    public event Action? SoundIoReset;

    public int SaveDataSize => _saveType switch
    {
        SaveType.Sram => 32 * 1024,
        SaveType.Eeprom => _eeprom.Length,
        SaveType.Flash64K => 64 * 1024,
        SaveType.Flash128K => GbaMemoryMap.SramSize,
        _ => 0
    };

    public ReadOnlySpan<byte> PaletteRam => _palette;

    public ReadOnlySpan<byte> VideoRam => _vram;

    public ReadOnlySpan<byte> ObjectAttributeMemory => _oam;

    public ushort DisplayControl
    {
        get => PeekIo16(IoRegisters.DISPCNT);
        set => PokeIo16(IoRegisters.DISPCNT, value);
    }

    public ushort DisplayStatus
    {
        get => PeekIo16(IoRegisters.DISPSTAT);
        set => WriteDisplayStatusControl(value);
    }

    public ushort VerticalCount
    {
        get => PeekIo16(IoRegisters.VCOUNT);
        set => PokeIo16(IoRegisters.VCOUNT, value);
    }

    public ushort InterruptEnable
    {
        get => PeekIo16(IoRegisters.IE);
        set => PokeIo16(IoRegisters.IE, value);
    }

    public ushort InterruptFlags
    {
        get => PeekIo16(IoRegisters.IF);
        set => PokeIo16(IoRegisters.IF, value);
    }

    public bool InterruptMasterEnable
    {
        get => (PeekIo16(IoRegisters.IME) & 1) != 0;
        set => PokeIo16(IoRegisters.IME, (ushort)(value ? 1 : 0));
    }

    public ushort BiosInterruptFlags
    {
        get => Read16(BiosInterruptFlagsAddress);
        set => Write16(BiosInterruptFlagsAddress, value);
    }

    public byte PostFlag
    {
        get => Read8(IoRegisters.POSTFLG);
        set => Write8(IoRegisters.POSTFLG, value);
    }

    public byte DisplayVCountSetting => (byte)(DisplayStatus >> 8);

    public ushort PeekIo16(uint address)
    {
        var offset = MapIoRegisterOffset(address);
        if (offset < 0 || offset + 1 >= _io.Length)
        {
            return 0xFFFF;
        }

        return (ushort)(_io[offset] | (_io[offset + 1] << 8));
    }

    public uint PeekIo32(uint address)
    {
        var offset = (int)(address - GbaMemoryMap.IoStart);
        return (uint)(_io[offset]
            | (_io[offset + 1] << 8)
            | (_io[offset + 2] << 16)
            | (_io[offset + 3] << 24));
    }

    public void PokeIo16(uint address, ushort value)
    {
        var offset = MapIoRegisterOffset(address);
        if (offset < 0 || offset + 1 >= _io.Length)
        {
            return;
        }

        _io[offset] = (byte)value;
        _io[offset + 1] = (byte)(value >> 8);
    }

    public void AddIoWriteObserver(Action<uint, int> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ioWriteObservers.Add(observer);
    }

    public void AddIoReadObserver(Action<uint, int> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ioReadObservers.Add(observer);
    }

    public void AddMemoryReadObserver(Action<uint, int, uint> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _memoryReadObservers.Add(observer);
    }

    public void AddMemoryWriteObserver(Action<uint, int> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _memoryWriteObservers.Add(observer);
    }

    public void LoadCartridge(Cartridge cartridge)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        _rom = cartridge.Rom;
        _saveType = cartridge.SaveType;
        _flashCommandState = FlashCommandState.None;
        _flashIdMode = false;
        _flashBank = 0;
        _rubyTitleMplayStatusGuard = cartridge.Header.GameCode is "AXVE" or "AXPE";
        _legendsBiosOpenBusGuard = cartridge.Header.GameCode is "A2LE";
        _cartridgeHardware = DetectCartridgeHardware(cartridge.Header);
        _gpioData = 0;
        _gpioDirection = 0;
        _gpioControl = 0;
        _solarCounter = 0;
        _solarClock = false;
        _gyroShiftValue = 0;
        _gyroShiftIndex = 16;
        _gyroClock = false;
        _gyroStart = false;
        _tiltReady = true;
        _cartridgeRumbleEnabled = false;
        _serialTransferCyclesRemaining = 0;
        _openBus = 0;
        _biosOpenBus = HasBios ? InitialBiosOpenBus : PostStartupBiosOpenBus;
        BiosAccessible = false;
        _eepromInputBits.Clear();
        _eepromOutputBits.Clear();
        _eepromAddressBits = null;
        ResetIoRegisters();
        _hasGpio = ContainsAscii(_rom, "SIIRTC") || _cartridgeHardware.HasFlag(CartridgeHardware.Gpio);
        _rtc.Reset();
        if (_saveType != SaveType.None)
        {
            Array.Fill(_sram, (byte)0xFF);
            Array.Fill(_eeprom, (byte)0xFF);
        }
    }

    public ReadOnlySpan<byte> ExportSaveData()
    {
        return _saveType == SaveType.Eeprom
            ? _eeprom
            : _sram.AsSpan(0, SaveDataSize);
    }

    public void LoadSaveData(ReadOnlySpan<byte> data)
    {
        var size = SaveDataSize;
        if (size == 0)
        {
            if (!data.IsEmpty)
            {
                throw new ArgumentException("This cartridge does not use external save memory.", nameof(data));
            }

            return;
        }

        if (data.Length > size)
        {
            throw new ArgumentException($"Save data is too large for {_saveType}: expected at most {size} bytes, got {data.Length}.", nameof(data));
        }

        var target = _saveType == SaveType.Eeprom ? _eeprom : _sram.AsSpan(0, size);
        data.CopyTo(target);
    }

    public void SetSolarSensorLevel(byte level) => _solarLevel = level;

    public void SetGyroSensorValue(ushort value) => _gyroSensorValue = (ushort)(value & 0x0FFF);

    public void SetTiltSensor(ushort x, ushort y)
    {
        _tiltX = (ushort)(x & 0x0FFF);
        _tiltY = (ushort)(y & 0x0FFF);
        _tiltReady = true;
    }

    public void RegisterRamReset(uint flags)
    {
        if ((flags & (1u << 0)) != 0)
        {
            Array.Clear(_ewram);
        }

        if ((flags & (1u << 1)) != 0)
        {
            Array.Clear(_iwram, 0, GbaMemoryMap.IwramSize - 0x200);
        }

        if ((flags & (1u << 2)) != 0)
        {
            Array.Clear(_palette);
        }

        if ((flags & (1u << 3)) != 0)
        {
            Array.Clear(_vram);
        }

        if ((flags & (1u << 4)) != 0)
        {
            Array.Clear(_oam);
        }

        // BIOS RegisterRamReset bit 5 resets serial I/O registers, not Game Pak save memory.
        if ((flags & (1u << 5)) != 0)
        {
            ResetSerialIo();
        }

        if ((flags & (1u << 6)) != 0)
        {
            ResetSoundIo();
        }

        if ((flags & (1u << 7)) != 0)
        {
            ResetOtherIo();
        }
    }

    public void Advance(int cycles)
    {
        if (_serialTransferCyclesRemaining <= 0)
        {
            return;
        }

        _serialTransferCyclesRemaining -= cycles;
        if (_serialTransferCyclesRemaining <= 0)
        {
            CompleteMultiplayerSerialTransfer();
        }
    }

    public byte Read8(uint address)
    {
        if (address < GbaMemoryMap.BiosSize && (!HasBios || !BiosAccessible))
        {
            if (_legendsBiosOpenBusGuard && address == 0x0000_00C3)
            {
                return LegendsPostStartupBiosByteC3;
            }

            return (byte)(_biosOpenBus >> (int)((address & 3) * 8));
        }

        if (IsGpioAddress(address))
        {
            return ReadGpio(address);
        }

        if (IsTiltSensorAddress(address))
        {
            return ReadTiltSensor(address);
        }

        if (GetRegion(address) == MemoryRegion.GamePakSram && IsFlashSave)
        {
            var flashValue = ReadFlash(address);
            NotifyMemoryRead(address, 1, flashValue);
            return flashValue;
        }

        if (GetRegion(address) == MemoryRegion.Io)
        {
            NotifyIoRead(address, 1);
            return ReadIo8(address);
        }

        var mapping = Map(address);
        if (mapping.Region == MemoryRegion.Unmapped || mapping.Buffer.Length == 0)
        {
            return 0xFF;
        }

        var value = mapping.Buffer[mapping.Offset];
        if (ShouldClearRubySapphirePausedBgmTrackMask(address))
        {
            // Pokemon Ruby/Sapphire can poll IsBGMStopped during map fades while
            // our partial M4A path leaves a paused BGM track bit latched.
            return 0;
        }

        if (_rubyTitleMplayStatusGuard && address == RubySapphireBgmStatusAddress && value == 0 && _iwram[0x7387] == 0x80)
        {
            // Pokemon Ruby/Sapphire's title screen uses the M4A BGM low track mask
            // as an attract-loop timer. Our partial audio core currently lets M4A
            // collapse that mask while the player status high bit remains active.
            return 1;
        }

        NotifyMemoryRead(address, 1, value);
        return value;
    }

    private bool ShouldClearRubySapphirePausedBgmTrackMask(uint address)
    {
        if (!_rubyTitleMplayStatusGuard || address is < RubySapphireBgmStatusAddress or > RubySapphireBgmStatusAddress + 1)
        {
            return false;
        }

        var mapMusicState = _iwram[RubySapphireMapMusicStateAddress - GbaMemoryMap.IwramStart];
        if (mapMusicState is not (5 or 6 or 7))
        {
            return false;
        }

        return (_iwram[RubySapphireBgmStatusAddress + 3 - GbaMemoryMap.IwramStart] & 0x80) != 0;
    }

    public ushort Read16(uint address)
    {
        var aligned = address & ~1u;
        if (aligned < GbaMemoryMap.BiosSize && (!HasBios || !BiosAccessible))
        {
            return (ushort)(Read8(aligned) | (Read8(aligned + 1) << 8));
        }

        if (IsEepromAddress(aligned))
        {
            return (ushort)ReadEepromBit();
        }

        if (RequiresBytewiseCartridgeHardwareRead(aligned, 2))
        {
            return (ushort)(Read8(aligned) | (Read8(aligned + 1) << 8));
        }

        var mapping = Map(aligned);
        if (mapping.Region == MemoryRegion.Io)
        {
            NotifyIoRead(aligned, 2);
            return ReadIo16(aligned);
        }

        if (mapping.Region == MemoryRegion.Unmapped || mapping.Buffer.Length == 0)
        {
            return 0xFFFF;
        }

        if (mapping.Region == MemoryRegion.GamePakSram && IsFlashSave)
        {
            return (ushort)(Read8(aligned) | (Read8(aligned + 1) << 8));
        }

        if (RequiresBytewiseRead(aligned, 2, mapping))
        {
            return (ushort)(Read8(aligned) | (Read8(aligned + 1) << 8));
        }

        var nextOffset = MapSequentialOffset(aligned, mapping, 1);
        var value = (ushort)(mapping.Buffer[mapping.Offset] | (mapping.Buffer[nextOffset] << 8));
        if (mapping.Region is not MemoryRegion.Bios)
        {
            NotifyMemoryRead(aligned, 2, value);
        }

        return value;
    }

    public uint Read32(uint address)
    {
        var aligned = address & ~3u;
        if (aligned < GbaMemoryMap.BiosSize && (!HasBios || !BiosAccessible))
        {
            var biosOpenBusValue = (uint)(Read8(aligned)
                | (Read8(aligned + 1) << 8)
                | (Read8(aligned + 2) << 16)
                | (Read8(aligned + 3) << 24));
            var biosOpenBusRotate = (int)((address & 3) * 8);
            return biosOpenBusRotate == 0 ? biosOpenBusValue : RotateRight(biosOpenBusValue, biosOpenBusRotate);
        }

        if (RequiresBytewiseCartridgeHardwareRead(aligned, 4))
        {
            var hardwareValue = (uint)(Read8(aligned)
                | (Read8(aligned + 1) << 8)
                | (Read8(aligned + 2) << 16)
                | (Read8(aligned + 3) << 24));
            var hardwareRotate = (int)((address & 3) * 8);
            return hardwareRotate == 0 ? hardwareValue : RotateRight(hardwareValue, hardwareRotate);
        }

        var mapping = Map(aligned);
        if (mapping.Region == MemoryRegion.Io)
        {
            NotifyIoRead(aligned, 4);
            return (uint)(ReadIo16(aligned) | (ReadIo16(aligned + 2) << 16));
        }

        if (mapping.Region == MemoryRegion.Unmapped || mapping.Buffer.Length == 0)
        {
            return 0xFFFF_FFFF;
        }

        if (mapping.Region == MemoryRegion.GamePakSram && IsFlashSave)
        {
            var flashValue = (uint)(Read8(aligned)
                | (Read8(aligned + 1) << 8)
                | (Read8(aligned + 2) << 16)
                | (Read8(aligned + 3) << 24));
            var flashRotate = (int)((address & 3) * 8);
            return flashRotate == 0 ? flashValue : RotateRight(flashValue, flashRotate);
        }

        if (RequiresBytewiseRead(aligned, 4, mapping))
        {
            var bytewiseValue = (uint)(Read8(aligned)
                | (Read8(aligned + 1) << 8)
                | (Read8(aligned + 2) << 16)
                | (Read8(aligned + 3) << 24));
            var bytewiseRotate = (int)((address & 3) * 8);
            return bytewiseRotate == 0 ? bytewiseValue : RotateRight(bytewiseValue, bytewiseRotate);
        }

        var value = (uint)(mapping.Buffer[mapping.Offset]
            | (mapping.Buffer[MapSequentialOffset(aligned, mapping, 1)] << 8)
            | (mapping.Buffer[MapSequentialOffset(aligned, mapping, 2)] << 16)
            | (mapping.Buffer[MapSequentialOffset(aligned, mapping, 3)] << 24));
        if (mapping.Region is not MemoryRegion.Bios)
        {
            NotifyMemoryRead(aligned, 4, value);
        }

        var rotate = (int)((address & 3) * 8);
        return rotate == 0 ? value : RotateRight(value, rotate);
    }

    public void Write8(uint address, byte value)
    {
        if (IsGpioAddress(address))
        {
            WriteGpio(address, value);
            return;
        }

        if (IsTiltSensorAddress(address))
        {
            WriteTiltSensor(address, value);
            return;
        }

        if (address == IoRegisters.IF || address == IoRegisters.IF + 1)
        {
            var clearMask = address == IoRegisters.IF ? value : value << 8;
            InterruptFlags = (ushort)(InterruptFlags & ~clearMask);
            return;
        }

        if (address == IoRegisters.DISPSTAT)
        {
            var current = PeekIo16(IoRegisters.DISPSTAT);
            PokeIo16(IoRegisters.DISPSTAT, (ushort)((current & 0x0007) | (current & 0xFF00) | (value & 0xF8)));
            NotifyIoWrite(address, 1);
            return;
        }

        if (address == IoRegisters.DISPSTAT + 1)
        {
            var current = PeekIo16(IoRegisters.DISPSTAT);
            PokeIo16(IoRegisters.DISPSTAT, (ushort)((current & 0x00FF) | (value << 8)));
            NotifyIoWrite(address, 1);
            return;
        }

        var mapping = Map(address);
        if (mapping.Region == MemoryRegion.GamePakSram && IsFlashSave)
        {
            WriteFlash(address, value);
            NotifyMemoryWrite(address, 1);
            return;
        }

        if (mapping.Region is MemoryRegion.Unmapped or MemoryRegion.Bios or MemoryRegion.GamePakRom || mapping.Buffer.Length == 0)
        {
            return;
        }

        if (mapping.Region == MemoryRegion.Palette)
        {
            var aligned = address & ~1u;
            var low = Map(aligned);
            var high = Map(aligned + 1);
            low.Buffer[low.Offset] = value;
            high.Buffer[high.Offset] = value;
        }
        else if (mapping.Region is MemoryRegion.Vram or MemoryRegion.Oam)
        {
            return;
        }
        else if (mapping.Region == MemoryRegion.Io)
        {
            WriteIo8(address, value);
        }
        else
        {
            mapping.Buffer[mapping.Offset] = value;
        }

        NotifyMemoryWrite(address, 1);
        NotifyIoWrite(address, 1);
    }

    public void Write16(uint address, ushort value)
    {
        if (IsEepromAddress(address & ~1u))
        {
            WriteEepromBit(value & 1);
            return;
        }

        if ((address & ~1u) == IoRegisters.IF)
        {
            InterruptFlags = (ushort)(InterruptFlags & ~value);
            return;
        }

        var aligned = address & ~1u;
        if (aligned == IoRegisters.DISPSTAT)
        {
            WriteDisplayStatusControl(value);
            NotifyIoWrite(aligned, 2);
            return;
        }

        if (GetRegion(aligned) == MemoryRegion.Io)
        {
            WriteIo16(aligned, value);
            NotifyIoWrite(aligned, 2);
            return;
        }

        var mapping = Map(aligned);
        if (mapping.Region is MemoryRegion.Palette or MemoryRegion.Vram or MemoryRegion.Oam)
        {
            var next = Map(aligned + 1);
            mapping.Buffer[mapping.Offset] = (byte)value;
            next.Buffer[next.Offset] = (byte)(value >> 8);
            NotifyMemoryWrite(aligned, 2);
            return;
        }

        if (mapping.Region is MemoryRegion.Ewram or MemoryRegion.Iwram)
        {
            var nextOffset = MapSequentialOffset(aligned, mapping, 1);
            mapping.Buffer[mapping.Offset] = (byte)value;
            mapping.Buffer[nextOffset] = (byte)(value >> 8);
            NotifyMemoryWrite(aligned, 2);
            return;
        }

        Write8(aligned, (byte)value);
        Write8(aligned + 1, (byte)(value >> 8));
    }

    public void Write32(uint address, uint value)
    {
        var aligned = address & ~3u;
        if (GetRegion(aligned) == MemoryRegion.Io)
        {
            Write16(aligned, (ushort)value);
            Write16(aligned + 2, (ushort)(value >> 16));
            return;
        }

        var mapping = Map(aligned);
        if (mapping.Region is MemoryRegion.Palette or MemoryRegion.Vram or MemoryRegion.Oam)
        {
            Write16(aligned, (ushort)value);
            Write16(aligned + 2, (ushort)(value >> 16));
            return;
        }

        if (mapping.Region is MemoryRegion.Ewram or MemoryRegion.Iwram)
        {
            mapping.Buffer[mapping.Offset] = (byte)value;
            mapping.Buffer[MapSequentialOffset(aligned, mapping, 1)] = (byte)(value >> 8);
            mapping.Buffer[MapSequentialOffset(aligned, mapping, 2)] = (byte)(value >> 16);
            mapping.Buffer[MapSequentialOffset(aligned, mapping, 3)] = (byte)(value >> 24);
            NotifyMemoryWrite(aligned, 4);
            return;
        }

        Write8(aligned, (byte)value);
        Write8(aligned + 1, (byte)(value >> 8));
        Write8(aligned + 2, (byte)(value >> 16));
        Write8(aligned + 3, (byte)(value >> 24));
    }

    public void SetOpenBus(uint value)
    {
        _openBus = value;
    }

    public void SetBiosOpenBus(uint value)
    {
        _biosOpenBus = value;
    }

    public void SetBiosAccessible(bool accessible)
    {
        BiosAccessible = HasBios && accessible;
    }

    public MemoryRegion GetRegion(uint address) => Map(address).Region;

    public void RequestInterrupt(ushort interrupt)
    {
        InterruptFlags = (ushort)(InterruptFlags | interrupt);
        InterruptRequested?.Invoke(interrupt, InterruptFlags);
    }

    public int GetCpuAccessCycles(uint address, int bytes, bool sequential)
    {
        var high = address >> 24;
        if (high is 0x0E or 0x0F)
        {
            return SramWaitCycles();
        }

        if (high is < 0x08 or > 0x0D)
        {
            return 1;
        }

        var waitState = GamePakWaitState(address);
        if (bytes == 4)
        {
            return GamePakNonSequentialCycles(waitState) + GamePakSequentialCycles(waitState);
        }

        return sequential
            ? GamePakSequentialCycles(waitState)
            : GamePakNonSequentialCycles(waitState);
    }

    public void AcknowledgeBiosInterruptFlags(ushort interrupt)
    {
        BiosInterruptFlags = (ushort)(BiosInterruptFlags | interrupt);
    }

    public void SetDisplayStatusFlags(ushort setMask, ushort clearMask)
    {
        PokeIo16(IoRegisters.DISPSTAT, (ushort)((DisplayStatus | setMask) & ~clearMask));
    }

    private Mapping Map(uint address)
    {
        var high = address >> 24;
        return high switch
        {
            0x00 when address < GbaMemoryMap.BiosSize => new Mapping(MemoryRegion.Bios, _bios, (int)address),
            0x02 => new Mapping(MemoryRegion.Ewram, _ewram, Mirror(address - GbaMemoryMap.EwramStart, GbaMemoryMap.EwramSize)),
            0x03 => new Mapping(MemoryRegion.Iwram, _iwram, Mirror(address - GbaMemoryMap.IwramStart, GbaMemoryMap.IwramSize)),
            0x04 => MapIo(address),
            0x05 => new Mapping(MemoryRegion.Palette, _palette, Mirror(address - GbaMemoryMap.PaletteStart, GbaMemoryMap.PaletteSize)),
            0x06 => new Mapping(MemoryRegion.Vram, _vram, MapVramOffset(address)),
            0x07 => new Mapping(MemoryRegion.Oam, _oam, Mirror(address - GbaMemoryMap.OamStart, GbaMemoryMap.OamSize)),
            >= 0x08 and <= 0x0D => MapRom(address),
            >= 0x0E and <= 0x0F => new Mapping(MemoryRegion.GamePakSram, _sram, MapSaveOffset(address)),
            _ => Mapping.Unmapped
        };
    }

    private Mapping MapIo(uint address)
    {
        var offset = address - GbaMemoryMap.IoStart;
        if (offset < GbaMemoryMap.IoSize)
        {
            return new Mapping(MemoryRegion.Io, _io, (int)offset);
        }

        if ((offset & 0xFFFF) == 0x0800)
        {
            return new Mapping(MemoryRegion.Io, _io, 0);
        }

        return Mapping.Unmapped;
    }

    private static int MapSequentialOffset(uint alignedAddress, Mapping mapping, uint byteOffset)
    {
        var offset = mapping.Offset + (int)byteOffset;
        return mapping.Region switch
        {
            MemoryRegion.Ewram => offset & (GbaMemoryMap.EwramSize - 1),
            MemoryRegion.Iwram => offset & (GbaMemoryMap.IwramSize - 1),
            MemoryRegion.Palette => offset & (GbaMemoryMap.PaletteSize - 1),
            MemoryRegion.Oam => offset & (GbaMemoryMap.OamSize - 1),
            MemoryRegion.Vram => MapVramOffset(alignedAddress + byteOffset),
            MemoryRegion.GamePakRom => offset < mapping.Buffer.Length ? offset : -1,
            MemoryRegion.GamePakSram => offset & (GbaMemoryMap.SramSize - 1),
            _ => offset
        };
    }

    private bool RequiresBytewiseRead(uint alignedAddress, int bytes, Mapping mapping)
    {
        if (mapping.Region == MemoryRegion.GamePakRom && mapping.Offset + bytes > mapping.Buffer.Length)
        {
            return true;
        }

        if (!_rubyTitleMplayStatusGuard || mapping.Region != MemoryRegion.Iwram)
        {
            return false;
        }

        var endAddress = alignedAddress + (uint)bytes - 1;
        return alignedAddress <= RubySapphireBgmStatusAddress + 3 && endAddress >= RubySapphireBgmStatusAddress;
    }

    private bool RequiresBytewiseCartridgeHardwareRead(uint alignedAddress, int bytes)
    {
        for (var offset = 0u; offset < bytes; offset++)
        {
            var address = alignedAddress + offset;
            if (IsGpioAddress(address) || IsTiltSensorAddress(address))
            {
                return true;
            }
        }

        return false;
    }

    private static int MapIoRegisterOffset(uint address)
    {
        var offset = address - GbaMemoryMap.IoStart;
        if (offset < GbaMemoryMap.IoSize)
        {
            return (int)offset;
        }

        return (offset & 0xFFFF) == 0x0800 ? 0 : -1;
    }

    private void NotifyIoWrite(uint address, int bytes)
    {
        if (GetRegion(address) != MemoryRegion.Io)
        {
            return;
        }

        foreach (var observer in _ioWriteObservers)
        {
            observer(address, bytes);
        }
    }

    private void NotifyMemoryWrite(uint address, int bytes)
    {
        foreach (var observer in _memoryWriteObservers)
        {
            observer(address, bytes);
        }
    }

    private void NotifyMemoryRead(uint address, int bytes, uint value)
    {
        foreach (var observer in _memoryReadObservers)
        {
            observer(address, bytes, value);
        }
    }

    private void WriteDisplayStatusControl(ushort value)
    {
        var statusBits = (ushort)(PeekIo16(IoRegisters.DISPSTAT) & 0x0007);
        PokeIo16(IoRegisters.DISPSTAT, (ushort)(statusBits | (value & 0xFFF8)));
    }

    private byte ReadIo8(uint address)
    {
        var aligned = address & ~1u;
        if (aligned is IoRegisters.SIOCNT or IoRegisters.RCNT or IoRegisters.JOYCNT
            or IoRegisters.SIOMULTI0 or IoRegisters.SIOMULTI1 or IoRegisters.SIOMULTI2
            or IoRegisters.SIOMULTI3 or IoRegisters.SIOMLT_SEND)
        {
            var value = ReadIo16(aligned);
            return (byte)(address == aligned ? value : value >> 8);
        }

        var mapping = Map(address);
        return mapping.Buffer.Length == 0 ? (byte)0xFF : mapping.Buffer[mapping.Offset];
    }

    private ushort ReadIo16(uint address)
    {
        return address switch
        {
            IoRegisters.SIOCNT => ReadSerialControl(),
            IoRegisters.RCNT => ReadRemoteControl(),
            IoRegisters.JOYCNT => (ushort)(PeekIo16(address) & 0x0047),
            _ => PeekIo16(address)
        };
    }

    private void WriteIo16(uint address, ushort value)
    {
        switch (address)
        {
            case IoRegisters.SIOCNT:
                WriteSerialControl(value);
                break;

            case IoRegisters.RCNT:
                PokeIo16(IoRegisters.RCNT, (ushort)(value & 0xC1FF));
                break;

            case IoRegisters.JOYCNT:
                PokeIo16(IoRegisters.JOYCNT, (ushort)(PeekIo16(IoRegisters.JOYCNT) & ~(value & 0x0007) | (value & 0x0040)));
                break;

            default:
                PokeIo16(address, value);
                break;
        }
    }

    private void WriteIo8(uint address, byte value)
    {
        var aligned = address & ~1u;
        if (aligned is not (IoRegisters.SIOCNT or IoRegisters.RCNT or IoRegisters.JOYCNT))
        {
            var mapping = Map(address);
            mapping.Buffer[mapping.Offset] = value;
            return;
        }

        var current = PeekIo16(aligned);
        var merged = address == aligned
            ? (ushort)((current & 0xFF00) | value)
            : (ushort)((current & 0x00FF) | (value << 8));
        WriteIo16(aligned, merged);
    }

    private ushort ReadRemoteControl()
    {
        var control = PeekIo16(IoRegisters.RCNT);
        if ((control & 0x8000) == 0)
        {
            return (ushort)((control & 0xC1F0) | SerialLineState());
        }

        return (ushort)(control & 0xC1FF);
    }

    private ushort ReadSerialControl()
    {
        var control = PeekIo16(IoRegisters.SIOCNT);
        if ((PeekIo16(IoRegisters.RCNT) & 0x8000) != 0)
        {
            return control;
        }

        return SerialMode(control) switch
        {
            0b10 => control,
            0b11 => (ushort)((control & 0x7F8F) | 0x0020),
            _ => (ushort)((control & 0x5F8B) | 0x0004)
        };
    }

    private void WriteSerialControl(ushort value)
    {
        var oldControl = PeekIo16(IoRegisters.SIOCNT);
        var control = (ushort)(value & 0x7FFF);
        var mode = SerialMode(control);

        if (mode == 0b10)
        {
            control = (ushort)((control & 0x7F83) | (oldControl & 0x007C) | 0x000C);
            PokeIo16(IoRegisters.SIOCNT, control);
            PokeIo16(IoRegisters.RCNT, (ushort)(PeekIo16(IoRegisters.RCNT) | 0x0001));
            if ((value & 0x0080) != 0 && _serialTransferCyclesRemaining <= 0)
            {
                PokeIo16(IoRegisters.SIOMULTI0, 0xFFFF);
                PokeIo16(IoRegisters.SIOMULTI1, 0xFFFF);
                PokeIo16(IoRegisters.SIOMULTI2, 0xFFFF);
                PokeIo16(IoRegisters.SIOMULTI3, 0xFFFF);
                PokeIo16(IoRegisters.RCNT, (ushort)(PeekIo16(IoRegisters.RCNT) & ~0x0001));
                _serialTransferCyclesRemaining = MultiplayerTransferCycles(control);
            }

            return;
        }

        PokeIo16(IoRegisters.SIOCNT, control);

        if ((value & 0x0080) != 0 && mode is 0b00 or 0b01)
        {
            CompleteNormalSerialTransfer();
        }
    }

    private void CompleteMultiplayerSerialTransfer()
    {
        PokeIo16(IoRegisters.SIOMULTI0, 0);
        PokeIo16(IoRegisters.SIOMULTI1, 0);
        PokeIo16(IoRegisters.SIOMULTI2, 0);
        PokeIo16(IoRegisters.SIOMULTI3, 0);

        _serialTransferCyclesRemaining = 0;
        var control = (ushort)((PeekIo16(IoRegisters.SIOCNT) & ~0x0080) | 0x000C);
        PokeIo16(IoRegisters.SIOCNT, control);
        PokeIo16(IoRegisters.RCNT, (ushort)(PeekIo16(IoRegisters.RCNT) | 0x0001));
        if ((control & 0x4000) != 0)
        {
            RequestInterrupt(IoRegisters.InterruptSerial);
        }
    }

    private void CompleteNormalSerialTransfer()
    {
        var control = (ushort)(PeekIo16(IoRegisters.SIOCNT) & ~0x0080);
        var is32Bit = (control & 0x1000) != 0;
        if (is32Bit)
        {
            PokeIo16(IoRegisters.SIOMULTI0, 0xFFFF);
            PokeIo16(IoRegisters.SIOMULTI1, 0xFFFF);
        }
        else
        {
            PokeIo16(IoRegisters.SIOMLT_SEND, 0x00FF);
        }

        PokeIo16(IoRegisters.SIOCNT, control);
        if ((control & 0x4000) != 0)
        {
            RequestInterrupt(IoRegisters.InterruptSerial);
        }
    }

    private void ResetSerialIo()
    {
        PokeIo16(IoRegisters.SIOMULTI0, 0);
        PokeIo16(IoRegisters.SIOMULTI1, 0);
        PokeIo16(IoRegisters.SIOMULTI2, 0);
        PokeIo16(IoRegisters.SIOMULTI3, 0);
        PokeIo16(IoRegisters.SIOCNT, 0);
        PokeIo16(IoRegisters.SIOMLT_SEND, 0);
        PokeIo16(IoRegisters.RCNT, InitialRemoteControl);
        PokeIo16(IoRegisters.JOYCNT, 0);
        _serialTransferCyclesRemaining = 0;
    }

    private void ResetSoundIo()
    {
        PokeIo16(IoRegisters.SOUND1CNT_L, 0);
        PokeIo16(IoRegisters.SOUND1CNT_H, 0);
        PokeIo16(IoRegisters.SOUND1CNT_X, 0);
        PokeIo16(IoRegisters.SOUND2CNT_L, 0);
        PokeIo16(IoRegisters.SOUND2CNT_H, 0);
        PokeIo16(IoRegisters.SOUND3CNT_L, 0);
        PokeIo16(IoRegisters.SOUND3CNT_H, 0);
        PokeIo16(IoRegisters.SOUND3CNT_X, 0);
        for (var offset = 0u; offset < 0x10; offset += 2)
        {
            PokeIo16(IoRegisters.WAVE_RAM + offset, 0);
        }

        PokeIo16(IoRegisters.SOUNDCNT_L, 0);
        PokeIo16(IoRegisters.SOUNDCNT_H, 0);
        PokeIo16(IoRegisters.SOUNDCNT_X, 0);
        PokeIo16(IoRegisters.SOUNDBIAS, 0x0200);
        PokeIo32(IoRegisters.FIFO_A, 0);
        PokeIo32(IoRegisters.FIFO_B, 0);
        SoundIoReset?.Invoke();
    }

    private void ResetOtherIo()
    {
        PokeIo16(IoRegisters.DISPCNT, 0x0080);
        PokeIo16(IoRegisters.DISPSTAT, (ushort)(PeekIo16(IoRegisters.DISPSTAT) & 0x0007));
        PokeIo16(IoRegisters.BG0CNT, 0);
        PokeIo16(IoRegisters.BG1CNT, 0);
        PokeIo16(IoRegisters.BG2CNT, 0);
        PokeIo16(IoRegisters.BG3CNT, 0);
        PokeIo16(IoRegisters.BG0HOFS, 0);
        PokeIo16(IoRegisters.BG0VOFS, 0);
        PokeIo16(IoRegisters.BG1HOFS, 0);
        PokeIo16(IoRegisters.BG1VOFS, 0);
        PokeIo16(IoRegisters.BG2HOFS, 0);
        PokeIo16(IoRegisters.BG2VOFS, 0);
        PokeIo16(IoRegisters.BG3HOFS, 0);
        PokeIo16(IoRegisters.BG3VOFS, 0);
        PokeIo16(IoRegisters.BG2PA, 0x0100);
        PokeIo16(IoRegisters.BG2PB, 0);
        PokeIo16(IoRegisters.BG2PC, 0);
        PokeIo16(IoRegisters.BG2PD, 0x0100);
        PokeIo32(IoRegisters.BG2X, 0);
        PokeIo32(IoRegisters.BG2Y, 0);
        PokeIo16(IoRegisters.BG3PA, 0x0100);
        PokeIo16(IoRegisters.BG3PB, 0);
        PokeIo16(IoRegisters.BG3PC, 0);
        PokeIo16(IoRegisters.BG3PD, 0x0100);
        PokeIo32(IoRegisters.BG3X, 0);
        PokeIo32(IoRegisters.BG3Y, 0);
        PokeIo16(IoRegisters.WIN0H, 0);
        PokeIo16(IoRegisters.WIN1H, 0);
        PokeIo16(IoRegisters.WIN0V, 0);
        PokeIo16(IoRegisters.WIN1V, 0);
        PokeIo16(IoRegisters.WININ, 0);
        PokeIo16(IoRegisters.WINOUT, 0);
        PokeIo16(IoRegisters.MOSAIC, 0);
        PokeIo16(IoRegisters.BLDCNT, 0);
        PokeIo16(IoRegisters.BLDALPHA, 0);
        PokeIo16(IoRegisters.BLDY, 0);
        ResetDmaIo();
        ResetTimerIo();
        PokeIo16(IoRegisters.KEYCNT, 0);
        PokeIo16(IoRegisters.IE, 0);
        PokeIo16(IoRegisters.IF, 0);
        PokeIo16(IoRegisters.WAITCNT, 0);
        PokeIo16(IoRegisters.IME, 0);
        PokeIo16(IoRegisters.POSTFLG, 0);
    }

    private void ResetDmaIo()
    {
        PokeIo32AndNotify(IoRegisters.DMA0SAD, 0);
        PokeIo32AndNotify(IoRegisters.DMA0DAD, 0);
        PokeIo16AndNotify(IoRegisters.DMA0CNT_L, 0);
        PokeIo16AndNotify(IoRegisters.DMA0CNT_H, 0);
        PokeIo32AndNotify(IoRegisters.DMA1SAD, 0);
        PokeIo32AndNotify(IoRegisters.DMA1DAD, 0);
        PokeIo16AndNotify(IoRegisters.DMA1CNT_L, 0);
        PokeIo16AndNotify(IoRegisters.DMA1CNT_H, 0);
        PokeIo32AndNotify(IoRegisters.DMA2SAD, 0);
        PokeIo32AndNotify(IoRegisters.DMA2DAD, 0);
        PokeIo16AndNotify(IoRegisters.DMA2CNT_L, 0);
        PokeIo16AndNotify(IoRegisters.DMA2CNT_H, 0);
        PokeIo32AndNotify(IoRegisters.DMA3SAD, 0);
        PokeIo32AndNotify(IoRegisters.DMA3DAD, 0);
        PokeIo16AndNotify(IoRegisters.DMA3CNT_L, 0);
        PokeIo16AndNotify(IoRegisters.DMA3CNT_H, 0);
    }

    private void ResetTimerIo()
    {
        PokeIo16AndNotify(IoRegisters.TM0CNT_L, 0);
        PokeIo16AndNotify(IoRegisters.TM0CNT_H, 0);
        PokeIo16AndNotify(IoRegisters.TM1CNT_L, 0);
        PokeIo16AndNotify(IoRegisters.TM1CNT_H, 0);
        PokeIo16AndNotify(IoRegisters.TM2CNT_L, 0);
        PokeIo16AndNotify(IoRegisters.TM2CNT_H, 0);
        PokeIo16AndNotify(IoRegisters.TM3CNT_L, 0);
        PokeIo16AndNotify(IoRegisters.TM3CNT_H, 0);
    }

    private void PokeIo16AndNotify(uint address, ushort value)
    {
        PokeIo16(address, value);
        NotifyIoWrite(address, 2);
    }

    private void PokeIo32AndNotify(uint address, uint value)
    {
        PokeIo32(address, value);
        NotifyIoWrite(address, 4);
    }

    private void PokeIo32(uint address, uint value)
    {
        PokeIo16(address, (ushort)value);
        PokeIo16(address + 2, (ushort)(value >> 16));
    }

    private void ResetIoRegisters()
    {
        Array.Clear(_io);
        PokeIo16(IoRegisters.DISPCNT, 0x0080);
        PokeIo16(IoRegisters.RCNT, InitialRemoteControl);
        PokeIo16(IoRegisters.KEYINPUT, 0x03FF);
        PokeIo16(IoRegisters.SOUNDBIAS, 0x0200);
        PokeIo16(IoRegisters.BG2PA, 0x0100);
        PokeIo16(IoRegisters.BG2PD, 0x0100);
        PokeIo16(IoRegisters.BG3PA, 0x0100);
        PokeIo16(IoRegisters.BG3PD, 0x0100);
    }

    private ushort SerialLineState()
    {
        var mode = SerialMode(PeekIo16(IoRegisters.SIOCNT));
        return mode == 0b10
            ? (ushort)(PeekIo16(IoRegisters.RCNT) & 0x000F)
            : (ushort)0x000D;
    }

    private static int SerialMode(ushort control) => (control >> 12) & 0x3;

    private static int MultiplayerTransferCycles(ushort control)
        => MultiplayerSerialTransferCycles[control & 0x0003, 0];

    private int SramWaitCycles()
        => (PeekIo16(IoRegisters.WAITCNT) & 0x0003) switch
        {
            0 => 4,
            1 => 3,
            2 => 2,
            _ => 8
        };

    private int GamePakWaitState(uint address)
        => ((address >> 24) & 0xF) switch
        {
            0x8 or 0x9 => 0,
            0xA or 0xB => 1,
            _ => 2
        };

    private int GamePakNonSequentialCycles(int waitState)
    {
        var waitControl = PeekIo16(IoRegisters.WAITCNT);
        var shift = 2 + waitState * 3;
        return ((waitControl >> shift) & 0x3) switch
        {
            0 => 4,
            1 => 3,
            2 => 2,
            _ => 8
        };
    }

    private int GamePakSequentialCycles(int waitState)
    {
        var waitControl = PeekIo16(IoRegisters.WAITCNT);
        var fast = ((waitControl >> (4 + waitState * 3)) & 1) != 0;
        if (waitState == 0)
        {
            return fast ? 1 : 2;
        }

        return fast ? 1 : 4;
    }

    private void NotifyIoRead(uint address, int bytes)
    {
        foreach (var observer in _ioReadObservers)
        {
            observer(address, bytes);
        }
    }

    private Mapping MapRom(uint address)
    {
        if (_rom.Length == 0)
        {
            return Mapping.Unmapped;
        }

        var offset = (address - GbaMemoryMap.GamePakRomStart) & 0x01FF_FFFF;
        if (offset >= _rom.Length)
        {
            return Mapping.Unmapped;
        }

        return new Mapping(MemoryRegion.GamePakRom, _rom, (int)offset);
    }

    private bool IsGpioAddress(uint address)
    {
        if (!_hasGpio || _rom.Length == 0 || (address >> 24) is not (0x08 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D))
        {
            return false;
        }

        var offset = (address - GbaMemoryMap.GamePakRomStart) & 0x01FF_FFFF;
        return offset is >= 0xC4 and <= 0xC9;
    }

    private byte ReadGpio(uint address)
    {
        if ((_gpioControl & 1) == 0)
        {
            return 0;
        }

        var offset = (address - GbaMemoryMap.GamePakRomStart) & 0x01FF_FFFF;
        return offset switch
        {
            0xC4 => (byte)((_gpioData & _gpioDirection) | (ReadGpioInputPins() & ~_gpioDirection)),
            0xC6 => _gpioDirection,
            0xC8 => _gpioControl,
            _ => 0
        };
    }

    private void WriteGpio(uint address, byte value)
    {
        var offset = (address - GbaMemoryMap.GamePakRomStart) & 0x01FF_FFFF;
        switch (offset)
        {
            case 0xC4:
                _gpioData = (byte)(value & 0xF);
                _rtc.Write(_gpioData, _gpioDirection);
                WriteSolarSensor(_gpioData, _gpioDirection);
                WriteGyroSensor(_gpioData, _gpioDirection);
                _cartridgeRumbleEnabled = _cartridgeHardware.HasFlag(CartridgeHardware.Rumble)
                    && (_gpioDirection & 0x8) != 0
                    && (_gpioData & 0x8) != 0;
                break;

            case 0xC6:
                _gpioDirection = (byte)(value & 0xF);
                break;

            case 0xC8:
                _gpioControl = (byte)(value & 1);
                break;
        }
    }

    private byte ReadGpioInputPins()
    {
        var value = _rtc.Read();
        if (_cartridgeHardware.HasFlag(CartridgeHardware.Solar))
        {
            value = (byte)((value & ~0x8) | (ReadSolarSensorFlag() << 3));
        }

        if (_cartridgeHardware.HasFlag(CartridgeHardware.Gyro))
        {
            value = (byte)((value & ~0x4) | (ReadGyroSensorBit() << 2));
        }

        return (byte)(value & 0xF);
    }

    private int ReadSolarSensorFlag()
        => _solarCounter >= _solarLevel ? 1 : 0;

    private void WriteSolarSensor(byte data, byte direction)
    {
        if (!_cartridgeHardware.HasFlag(CartridgeHardware.Solar))
        {
            return;
        }

        var resetDriven = (direction & 0x2) != 0 && (data & 0x2) != 0;
        if (resetDriven)
        {
            _solarCounter = 0;
        }

        var clock = (direction & 0x1) != 0 && (data & 0x1) != 0;
        if (!_solarClock && clock && _solarCounter < byte.MaxValue)
        {
            _solarCounter++;
        }

        _solarClock = clock;
    }

    private int ReadGyroSensorBit()
        => _gyroShiftIndex < 16 ? (_gyroShiftValue >> (15 - _gyroShiftIndex)) & 1 : 0;

    private void WriteGyroSensor(byte data, byte direction)
    {
        if (!_cartridgeHardware.HasFlag(CartridgeHardware.Gyro))
        {
            return;
        }

        var start = (direction & 0x1) != 0 && (data & 0x1) != 0;
        if (!_gyroStart && start)
        {
            _gyroShiftValue = _gyroSensorValue;
            _gyroShiftIndex = 0;
        }

        _gyroStart = start;

        var clock = (direction & 0x2) != 0 && (data & 0x2) != 0;
        if (!_gyroClock && clock && _gyroShiftIndex < 16)
        {
            _gyroShiftIndex++;
        }

        _gyroClock = clock;
    }

    private bool IsTiltSensorAddress(uint address)
    {
        if (!_cartridgeHardware.HasFlag(CartridgeHardware.Tilt) || (address >> 24) is not (0x0E or 0x0F))
        {
            return false;
        }

        var offset = address & 0x000F_FFFF;
        return offset is 0x008000 or 0x008100 or 0x008200 or 0x008300 or 0x008400 or 0x008500;
    }

    private byte ReadTiltSensor(uint address)
    {
        var offset = address & 0x000F_FFFF;
        return offset switch
        {
            0x008200 => (byte)_tiltX,
            0x008300 => (byte)((_tiltX >> 8) | (_tiltReady ? 0x80 : 0)),
            0x008400 => (byte)_tiltY,
            0x008500 => (byte)((_tiltY >> 8) | (_tiltReady ? 0x80 : 0)),
            _ => 0xFF
        };
    }

    private void WriteTiltSensor(uint address, byte value)
    {
        var offset = address & 0x000F_FFFF;
        if ((offset == 0x008000 && value == 0x55) || (offset == 0x008100 && value == 0xAA))
        {
            _tiltReady = true;
        }
    }

    private static CartridgeHardware DetectCartridgeHardware(CartridgeHeader header)
    {
        var hardware = CartridgeHardware.None;
        if (header.GameCode is "U3IE" or "U3IP" or "U32E" or "U32P" or "U32J" or "U33J")
        {
            hardware |= CartridgeHardware.Gpio | CartridgeHardware.Rtc | CartridgeHardware.Solar;
        }

        if (header.GameCode is "RZWE" or "RZWP" or "RZWJ")
        {
            hardware |= CartridgeHardware.Gpio | CartridgeHardware.Gyro | CartridgeHardware.Rumble;
        }

        if (header.GameCode is "KYGE" or "KYGP" or "KYGJ")
        {
            hardware |= CartridgeHardware.Tilt;
        }

        if (header.GameCode is "V49E" or "V49P" or "V49J")
        {
            hardware |= CartridgeHardware.Rumble;
        }

        return hardware;
    }

    private bool IsEepromAddress(uint address)
        => _saveType == SaveType.Eeprom && address >= 0x0D00_0000 && address <= GbaMemoryMap.GamePakRomEnd;

    public void HintEepromTransferBitCount(int bitCount)
    {
        if (_saveType != SaveType.Eeprom)
        {
            return;
        }

        _eepromAddressBits = bitCount switch
        {
            9 or 73 => 6,
            17 or 81 => 14,
            _ => _eepromAddressBits
        };
    }

    private int ReadEepromBit()
        => _eepromOutputBits.Count == 0 ? 1 : _eepromOutputBits.Dequeue();

    private void WriteEepromBit(int bit)
    {
        _eepromInputBits.Add(bit & 1);
        var addressBits = EepromAddressBits;
        if (_eepromInputBits.Count < 2)
        {
            return;
        }

        var command = (_eepromInputBits[0] << 1) | _eepromInputBits[1];
        var readLength = 2 + addressBits + 1;
        var writeLength = 2 + addressBits + 64 + 1;

        if (command == 0b11 && _eepromInputBits.Count == readLength)
        {
            QueueEepromRead(ReadEepromAddress(addressBits));
            _eepromInputBits.Clear();
            return;
        }

        if (command == 0b10 && _eepromInputBits.Count == writeLength)
        {
            WriteEepromData(ReadEepromAddress(addressBits), addressBits);
            _eepromInputBits.Clear();
            return;
        }

        if (_eepromInputBits.Count > writeLength)
        {
            _eepromInputBits.Clear();
        }
    }

    private int EepromAddressBits => _eepromAddressBits ?? (_rom.Length >= 16 * 1024 * 1024 ? 14 : 6);

    private int ReadEepromAddress(int addressBits)
    {
        var address = 0;
        for (var i = 0; i < addressBits; i++)
        {
            address = (address << 1) | _eepromInputBits[2 + i];
        }

        return address;
    }

    private void QueueEepromRead(int address)
    {
        _eepromOutputBits.Clear();
        for (var i = 0; i < 4; i++)
        {
            _eepromOutputBits.Enqueue(0);
        }

        var offset = (address * 8) % _eeprom.Length;
        ulong data = 0;
        for (var byteIndex = 0; byteIndex < 8; byteIndex++)
        {
            var value = _eeprom[offset + byteIndex];
            data = (data << 8) | value;
            for (var bit = 7; bit >= 0; bit--)
            {
                _eepromOutputBits.Enqueue((value >> bit) & 1);
            }
        }

        EepromAccessed?.Invoke(new EepromTrace("read", address, data, EepromAddressBits, _eepromOutputBits.Count));
    }

    private void WriteEepromData(int address, int addressBits)
    {
        var offset = (address * 8) % _eeprom.Length;
        var bitIndex = 2 + addressBits;
        ulong data = 0;
        for (var byteIndex = 0; byteIndex < 8; byteIndex++)
        {
            var value = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value << 1) | _eepromInputBits[bitIndex++];
            }

            _eeprom[offset + byteIndex] = (byte)value;
            data = (data << 8) | (byte)value;
        }

        EepromAccessed?.Invoke(new EepromTrace("write", address, data, addressBits, _eepromOutputBits.Count));
    }

    private static int MapVramOffset(uint address)
    {
        var offset = (address - GbaMemoryMap.VramStart) & 0x1FFFF;
        if (offset >= GbaMemoryMap.VramSize)
        {
            offset -= 0x8000;
        }

        return (int)offset;
    }

    private bool IsFlashSave => _saveType is SaveType.Flash64K or SaveType.Flash128K;

    private byte ReadFlash(uint address)
    {
        var offset = MapSaveOffset(address);
        if (_flashIdMode)
        {
            if (_saveType == SaveType.Flash128K)
            {
                return (offset & 1) == 0 ? (byte)0x62 : (byte)0x13;
            }

            return (offset & 1) == 0 ? (byte)0xC2 : (byte)0x1C;
        }

        return _sram[offset];
    }

    private void WriteFlash(uint address, byte value)
    {
        var offset = MapSaveOffset(address);
        var commandAddress = (address - GbaMemoryMap.GamePakSramStart) & 0xFFFF;

        if (_flashCommandState == FlashCommandState.Program)
        {
            _sram[offset] = value;
            _flashCommandState = FlashCommandState.None;
            return;
        }

        if (_flashCommandState == FlashCommandState.BankSelect)
        {
            _flashBank = _saveType == SaveType.Flash128K ? value & 1 : 0;
            _flashCommandState = FlashCommandState.None;
            return;
        }

        switch (_flashCommandState, commandAddress, value)
        {
            case (FlashCommandState.None, 0x5555, 0xAA):
                _flashCommandState = FlashCommandState.Unlock1;
                break;

            case (FlashCommandState.Unlock1, 0x2AAA, 0x55):
                _flashCommandState = FlashCommandState.Unlock2;
                break;

            case (FlashCommandState.Unlock2, 0x5555, 0x90):
                _flashIdMode = true;
                _flashCommandState = FlashCommandState.None;
                break;

            case (FlashCommandState.Unlock2, 0x5555, 0xF0):
                _flashIdMode = false;
                _flashCommandState = FlashCommandState.None;
                break;

            case (FlashCommandState.Unlock2, 0x5555, 0xA0):
                _flashCommandState = FlashCommandState.Program;
                break;

            case (FlashCommandState.Unlock2, 0x5555, 0xB0):
                _flashCommandState = FlashCommandState.BankSelect;
                break;

            case (FlashCommandState.Unlock2, 0x5555, 0x80):
                _flashCommandState = FlashCommandState.EraseUnlock1;
                break;

            case (FlashCommandState.EraseUnlock1, 0x5555, 0xAA):
                _flashCommandState = FlashCommandState.EraseUnlock2;
                break;

            case (FlashCommandState.EraseUnlock2, 0x2AAA, 0x55):
                _flashCommandState = FlashCommandState.EraseCommand;
                break;

            case (FlashCommandState.EraseCommand, 0x5555, 0x10):
                Array.Fill(_sram, (byte)0xFF);
                _flashCommandState = FlashCommandState.None;
                break;

            case (FlashCommandState.EraseCommand, _, 0x30):
                Array.Fill(_sram, (byte)0xFF, offset & ~0xFFF, 0x1000);
                _flashCommandState = FlashCommandState.None;
                break;

            case (_, _, 0xF0):
                _flashIdMode = false;
                _flashCommandState = FlashCommandState.None;
                break;

            default:
                _flashCommandState = FlashCommandState.None;
                break;
        }
    }

    private int MapSaveOffset(uint address)
    {
        var size = _saveType == SaveType.Flash128K ? GbaMemoryMap.SramSize : 64 * 1024;
        var offset = Mirror(address - GbaMemoryMap.GamePakSramStart, size);
        return _saveType == SaveType.Flash128K ? (_flashBank * 64 * 1024 + (offset & 0xFFFF)) : offset;
    }

    private static int Mirror(uint offset, int size) => (int)(offset % (uint)size);

    private static uint RotateRight(uint value, int bits) => (value >> bits) | (value << (32 - bits));

    private static bool ContainsAscii(ReadOnlySpan<byte> source, string value)
    {
        Span<byte> needle = stackalloc byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            needle[i] = (byte)value[i];
        }

        return source.IndexOf(needle) >= 0;
    }

    private static byte[] ValidateSize(byte[] buffer, int expectedLength, string name)
    {
        if (buffer.Length != expectedLength)
        {
            throw new ArgumentException($"Expected {expectedLength} bytes, got {buffer.Length}.", name);
        }

        return buffer;
    }

    private readonly record struct Mapping(MemoryRegion Region, byte[] Buffer, int Offset)
    {
        public static Mapping Unmapped { get; } = new(MemoryRegion.Unmapped, [], 0);
    }

    private enum FlashCommandState
    {
        None,
        Unlock1,
        Unlock2,
        Program,
        BankSelect,
        EraseUnlock1,
        EraseUnlock2,
        EraseCommand
    }

    [Flags]
    private enum CartridgeHardware
    {
        None = 0,
        Gpio = 1 << 0,
        Rtc = 1 << 1,
        Solar = 1 << 2,
        Gyro = 1 << 3,
        Tilt = 1 << 4,
        Rumble = 1 << 5
    }

    private sealed class CartridgeRtc
    {
        private static readonly byte[] ParameterLengths = [0, 0, 7, 0, 1, 0, 3, 0];
        private readonly byte[] _buffer = new byte[7];
        private bool _sck;
        private byte _sio;
        private bool _cs;
        private byte _state = 0xFF;
        private int _shiftCount;
        private byte _data;
        private byte _register;
        private int _currentByte;
        private byte _control = 0x40;

        public void Reset()
        {
            _sck = false;
            _sio = 0;
            _cs = false;
            _state = 0xFF;
            _shiftCount = 0;
            _data = 0;
            _register = 0;
            _currentByte = 0;
            Array.Clear(_buffer);
            _control = 0x40;
        }

        public byte Read() => _cs ? (byte)(_sio << 1) : (byte)0;

        public void Write(byte data, byte mask)
        {
            var oldCs = _cs;
            var oldSck = _sck;
            if ((mask & 1) != 0)
            {
                _sck = (data & 1) != 0;
            }

            if ((mask & 2) != 0)
            {
                _sio = (byte)((data >> 1) & 1);
            }

            if ((mask & 4) != 0)
            {
                _cs = (data & 4) != 0;
            }

            if (!_cs)
            {
                return;
            }

            if (!oldCs)
            {
                _state = 0;
                _shiftCount = 0;
                _data = 0;
                _currentByte = 0;
                return;
            }

            if (!oldSck && _sck)
            {
                switch (_state)
                {
                    case 0:
                        ReceiveCommand();
                        break;

                    case 1:
                        ReceiveParameter();
                        break;

                    case 2:
                        SendRegisterData();
                        break;
                }
            }
        }

        private bool ReadSerialBit()
        {
            _data = (byte)((_data & ~(1 << _shiftCount)) | (_sio << _shiftCount));
            _shiftCount++;
            if (_shiftCount != 8)
            {
                return false;
            }

            _shiftCount = 0;
            return true;
        }

        private void ReceiveCommand()
        {
            if (!ReadSerialBit())
            {
                return;
            }

            _data = NormalizeCommand(_data);
            _register = (byte)((_data >> 4) & 0x7);
            _currentByte = 0;
            Array.Clear(_buffer);

            if ((_data & 0x80) == 0)
            {
                if (ParameterLengths[_register] > 0)
                {
                    _state = 1;
                }
                else
                {
                    WriteRegister();
                    _state = 0xFF;
                }
            }
            else
            {
                ReadRegister();
                _state = ParameterLengths[_register] > 0 ? (byte)2 : (byte)0xFF;
            }

            _data = 0;
        }

        private void ReceiveParameter()
        {
            if (_currentByte >= ParameterLengths[_register] || !ReadSerialBit())
            {
                return;
            }

            _buffer[_currentByte++] = _data;
            _data = 0;
            if (_currentByte == ParameterLengths[_register])
            {
                WriteRegister();
                _state = 0xFF;
            }
        }

        private void SendRegisterData()
        {
            _sio = (byte)(_buffer[_currentByte] & 1);
            _buffer[_currentByte] >>= 1;
            _shiftCount++;
            if (_shiftCount != 8)
            {
                return;
            }

            _shiftCount = 0;
            _currentByte++;
            if (_currentByte == ParameterLengths[_register])
            {
                _state = 0xFF;
            }
        }

        private void ReadRegister()
        {
            var now = DateTime.Now;
            switch (_register)
            {
                case 2:
                    _buffer[0] = Bcd((byte)(now.Year - 2000));
                    _buffer[1] = Bcd((byte)now.Month);
                    _buffer[2] = Bcd((byte)now.Day);
                    _buffer[3] = Bcd((byte)now.DayOfWeek);
                    _buffer[4] = Bcd(EncodeHour((byte)now.Hour));
                    _buffer[5] = Bcd((byte)now.Minute);
                    _buffer[6] = Bcd((byte)now.Second);
                    break;

                case 4:
                    _buffer[0] = _control;
                    _control &= 0x7F;
                    break;

                case 6:
                    _buffer[0] = Bcd(EncodeHour((byte)now.Hour));
                    _buffer[1] = Bcd((byte)now.Minute);
                    _buffer[2] = Bcd((byte)now.Second);
                    break;
            }
        }

        private void WriteRegister()
        {
            switch (_register)
            {
                case 0:
                    _control = 0;
                    break;

                case 4:
                    _control = (byte)(_buffer[0] & 0x7F);
                    break;
            }
        }

        private static byte NormalizeCommand(byte command)
        {
            if ((command >> 4) == 0b0110)
            {
                command = (byte)((command << 4) | (command >> 4));
                command = (byte)(((command & 0x33) << 2) | ((command & 0xCC) >> 2));
                command = (byte)(((command & 0x55) << 1) | ((command & 0xAA) >> 1));
            }

            return (command & 0xF) == 0b0110 ? command : (byte)0xF0;
        }

        private byte EncodeHour(byte hour)
            => (_control & 0x40) == 0 && hour >= 12 ? (byte)((hour - 12) | 0x80) : hour;

        private static byte Bcd(byte value) => (byte)(((value / 10) << 4) | (value % 10));
    }
}
