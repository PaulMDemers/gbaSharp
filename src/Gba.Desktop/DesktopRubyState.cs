using Gba.Core.Memory;

namespace Gba.Desktop;

internal sealed record DesktopRubyState(
    bool HasRom,
    bool IsRubyOrSapphire,
    string? GameCode,
    string? GameTitle,
    long EmulatedFrames,
    DesktopRubyPlayerPosition? SaveBlockPlayer,
    DesktopRubyTaskState Tasks,
    DesktopRubyMovementTask? MovementTask,
    DesktopRubyObjectEvent? PlayerObject,
    IReadOnlyList<DesktopRubyObjectEvent> ObjectEvents,
    DesktopRubyMapState Raw);

internal sealed record DesktopRubyPlayerPosition(
    int X,
    int Y,
    int MapGroup,
    int MapNumber,
    string MapId);

internal sealed record DesktopRubyTaskState(
    int ActiveCount,
    string FirstTaskSummary);

internal sealed record DesktopRubyMovementTask(
    int Id,
    ushort Flags,
    IReadOnlyList<byte> ObjectBytes,
    string Summary);

internal sealed record DesktopRubyObjectEvent(
    int Index,
    uint Address,
    byte LocalId,
    int MapGroup,
    int MapNumber,
    string MapId,
    int X,
    int Y,
    int PreviousX,
    int PreviousY,
    byte Flags0,
    byte Flags1,
    byte Flags2,
    int Facing,
    string FacingName,
    int Movement,
    byte Action,
    byte Meta,
    bool IsPlayer);

internal sealed record DesktopRubyMapState(
    string Global2034,
    string Global1100,
    string TaskTable,
    string Callback7Dec,
    IReadOnlyDictionary<string, string> Vars,
    string ScriptContext1,
    string ScriptContext2,
    string ScriptContext3);

internal static class DesktopRubyStateProbe
{
    private const uint SaveBlock1 = 0x0202_5734;
    private const uint VarsOffset = 0x1340;
    private const ushort VarsStart = 0x4000;
    private const uint TaskBase = 0x0300_4B20;
    private const uint ObjectBase = 0x0300_48AC;
    private const int TaskSlots = 16;
    private const int ObjectSlots = 16;
    private const uint RubyMovementTaskCallback = 0x080A_244D;

    public static DesktopRubyState Capture(Gba.Core.GbaSystem? gba, long emulatedFrames)
    {
        if (gba?.Cartridge is null)
        {
            return new DesktopRubyState(
                HasRom: false,
                IsRubyOrSapphire: false,
                GameCode: null,
                GameTitle: null,
                EmulatedFrames: emulatedFrames,
                SaveBlockPlayer: null,
                Tasks: new DesktopRubyTaskState(0, "none"),
                MovementTask: null,
                PlayerObject: null,
                ObjectEvents: [],
                Raw: EmptyRawState());
        }

        var gameCode = gba.Cartridge.Header.GameCode;
        var bus = gba.Bus;
        var objects = ReadObjectEvents(bus);
        var player = objects.FirstOrDefault(item => item.IsPlayer) ?? ScanPlayerObject(bus);
        var movementTaskId = FindTaskIdByFunc(bus, TaskBase, TaskSlots, RubyMovementTaskCallback);
        return new DesktopRubyState(
            HasRom: true,
            IsRubyOrSapphire: gameCode is "AXVE" or "AXPE",
            GameCode: gameCode,
            GameTitle: gba.Cartridge.Header.Title,
            EmulatedFrames: emulatedFrames,
            SaveBlockPlayer: ReadSaveBlockPlayer(bus),
            Tasks: new DesktopRubyTaskState(CountTaskSlots(bus, TaskBase, TaskSlots), SummarizeFirstTask(bus, TaskBase, TaskSlots)),
            MovementTask: movementTaskId >= 0 ? ReadMovementTask(bus, movementTaskId) : null,
            PlayerObject: player,
            ObjectEvents: objects,
            Raw: ReadRawState(bus));
    }

    private static DesktopRubyPlayerPosition ReadSaveBlockPlayer(MemoryBus bus)
    {
        var x = ReadS16(bus, SaveBlock1);
        var y = ReadS16(bus, SaveBlock1 + 2);
        var mapGroup = unchecked((sbyte)bus.Read8(SaveBlock1 + 4));
        var mapNumber = unchecked((sbyte)bus.Read8(SaveBlock1 + 5));
        return new DesktopRubyPlayerPosition(x, y, mapGroup, mapNumber, FormatMapId(mapGroup, mapNumber));
    }

    private static DesktopRubyMapState ReadRawState(MemoryBus bus)
        => new(
            Hex32(bus.Read32(0x0300_2034)),
            Hex32(bus.Read32(0x0300_1100)),
            Hex32(bus.Read32(TaskBase)),
            Hex32(bus.Read32(0x0300_7DEC)),
            new Dictionary<string, string>
            {
                ["0x4050"] = Hex16(ReadRubyVar(bus, 0x4050)),
                ["0x4060"] = Hex16(ReadRubyVar(bus, 0x4060)),
                ["0x4082"] = Hex16(ReadRubyVar(bus, 0x4082)),
                ["0x408C"] = Hex16(ReadRubyVar(bus, 0x408C)),
                ["0x408D"] = Hex16(ReadRubyVar(bus, 0x408D)),
                ["0x4092"] = Hex16(ReadRubyVar(bus, 0x4092))
            },
            Hex16(bus.Read16(0x0202_E8B6)),
            Hex16(bus.Read16(0x0202_E8B8)),
            Hex16(bus.Read16(0x0202_E8BA)));

    private static DesktopRubyMapState EmptyRawState()
        => new("0x00000000", "0x00000000", "0x00000000", "0x00000000", new Dictionary<string, string>(), "0x0000", "0x0000", "0x0000");

    private static IReadOnlyList<DesktopRubyObjectEvent> ReadObjectEvents(MemoryBus bus)
    {
        var objects = new List<DesktopRubyObjectEvent>();
        for (var i = 0; i < ObjectSlots; i++)
        {
            var address = ObjectBase + (uint)(i * 0x24);
            if (IsPlausibleObjectEvent(bus, address))
            {
                objects.Add(ReadObjectEvent(bus, address, i));
            }
        }

        return objects;
    }

    private static DesktopRubyObjectEvent? ScanPlayerObject(MemoryBus bus)
        => ScanPlayerObjectRange(bus, GbaMemoryMap.EwramStart, GbaMemoryMap.EwramSize)
        ?? ScanPlayerObjectRange(bus, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramSize);

    private static DesktopRubyObjectEvent? ScanPlayerObjectRange(MemoryBus bus, uint start, int size)
    {
        for (var address = start; address <= start + size - 0x24; address += 4)
        {
            if (IsPlausibleObjectEvent(bus, address) && bus.Read8(address + 8) == 0xFF)
            {
                return ReadObjectEvent(bus, address, 0);
            }
        }

        return null;
    }

    private static DesktopRubyObjectEvent ReadObjectEvent(MemoryBus bus, uint address, int index)
    {
        var mapGroup = bus.Read8(address + 0x0A);
        var mapNumber = bus.Read8(address + 9);
        var facingMovement = bus.Read8(address + 0x18);
        return new DesktopRubyObjectEvent(
            index,
            address,
            bus.Read8(address + 8),
            mapGroup,
            mapNumber,
            FormatMapId(mapGroup, mapNumber),
            ReadS16(bus, address + 0x10),
            ReadS16(bus, address + 0x12),
            ReadS16(bus, address + 0x14),
            ReadS16(bus, address + 0x16),
            bus.Read8(address),
            bus.Read8(address + 1),
            bus.Read8(address + 2),
            facingMovement & 0xF,
            FacingName(facingMovement & 0xF),
            facingMovement >> 4,
            bus.Read8(address + 0x1C),
            bus.Read8(address + 0x1E),
            bus.Read8(address + 8) == 0xFF || (bus.Read8(address + 2) & 1) != 0);
    }

    private static bool IsPlausibleObjectEvent(MemoryBus bus, uint address)
    {
        var flags0 = bus.Read8(address);
        if ((flags0 & 1) == 0 || flags0 == 0xFF)
        {
            return false;
        }

        var spriteId = bus.Read8(address + 4);
        var graphicsId = bus.Read8(address + 5);
        var currentX = ReadS16(bus, address + 0x10);
        var currentY = ReadS16(bus, address + 0x12);
        var previousX = ReadS16(bus, address + 0x14);
        var previousY = ReadS16(bus, address + 0x16);
        return spriteId < 128
            && graphicsId < 240
            && currentX is >= -16 and <= 256
            && currentY is >= -16 and <= 256
            && previousX is >= -16 and <= 256
            && previousY is >= -16 and <= 256;
    }

    private static DesktopRubyMovementTask ReadMovementTask(MemoryBus bus, int taskId)
    {
        var taskAddress = TaskBase + (uint)(taskId * 0x28);
        var objectBytes = new byte[16];
        for (var i = 0; i < objectBytes.Length; i++)
        {
            objectBytes[i] = bus.Read8(taskAddress + 0x0A + (uint)i);
        }

        var flags = bus.Read16(taskAddress + 8);
        var summary = $"#{taskId}:flags=0x{flags:X4};objects={string.Join(' ', objectBytes.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))}";
        return new DesktopRubyMovementTask(taskId, flags, objectBytes, summary);
    }

    private static int CountTaskSlots(MemoryBus bus, uint tableAddress, int slots)
    {
        var count = 0;
        for (var i = 0; i < slots; i++)
        {
            var taskAddress = tableAddress + (uint)(i * 0x28);
            if (bus.Read8(taskAddress + 4) != 0 && bus.Read32(taskAddress) != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static string SummarizeFirstTask(MemoryBus bus, uint tableAddress, int slots)
    {
        for (var i = 0; i < slots; i++)
        {
            var taskAddress = tableAddress + (uint)(i * 0x28);
            var callback = bus.Read32(taskAddress);
            if (bus.Read8(taskAddress + 4) == 0 || callback == 0)
            {
                continue;
            }

            return $"#{i}:cb=0x{callback:X8};prio={bus.Read8(taskAddress + 7)};d0=0x{bus.Read16(taskAddress + 8):X4};d1=0x{bus.Read16(taskAddress + 0xA):X4};d2=0x{bus.Read16(taskAddress + 0xC):X4};d3=0x{bus.Read16(taskAddress + 0xE):X4}";
        }

        return "none";
    }

    private static int FindTaskIdByFunc(MemoryBus bus, uint taskBaseAddress, int slots, uint callback)
    {
        for (var i = 0; i < slots; i++)
        {
            var taskAddress = taskBaseAddress + (uint)(i * 0x28);
            if (bus.Read8(taskAddress + 4) != 0 && bus.Read32(taskAddress) == callback)
            {
                return i;
            }
        }

        return -1;
    }

    private static ushort ReadRubyVar(MemoryBus bus, ushort id)
        => bus.Read16(SaveBlock1 + VarsOffset + (uint)((id - VarsStart) * 2));

    private static short ReadS16(MemoryBus bus, uint address) => unchecked((short)bus.Read16(address));

    private static string FormatMapId(int group, int number) => $"{group}.{number}";

    private static string FacingName(int facing)
        => facing switch
        {
            1 => "Down",
            2 => "Up",
            3 => "Left",
            4 => "Right",
            _ => "Unknown"
        };

    private static string Hex16(ushort value) => $"0x{value:X4}";

    private static string Hex32(uint value) => $"0x{value:X8}";
}
