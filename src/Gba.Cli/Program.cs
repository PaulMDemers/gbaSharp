using Gba.Core;
using Gba.Core.Audio;
using Gba.Core.Cartridges;
using Gba.Core.Input;
using Gba.Core.Memory;
using Gba.Core.Video;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    var rawArgs = args.ToList();
    var bios = ExtractBiosOption(rawArgs);
    var command = rawArgs[0].ToLowerInvariant();
    if (command == "compat")
    {
        return await RunCompatibility(rawArgs.Skip(1).ToArray(), bios);
    }

    if (command == "compat-summary")
    {
        return RunCompatibilitySummary(rawArgs.Skip(1).ToArray());
    }

    if (command == "save-probe")
    {
        return await RunSaveProbe(rawArgs.Skip(1).ToArray());
    }

    if (command is not ("run" or "test-rom" or "dump-frame" or "capture-frames" or "verify-frame" or "compare-bios"))
    {
        command = "info";
    }

    var romPath = command == "info" ? rawArgs[0] : rawArgs.ElementAtOrDefault(1);
    if (string.IsNullOrWhiteSpace(romPath))
    {
        PrintUsage();
        return 2;
    }

    var cartridge = await Cartridge.LoadFileAsync(romPath);
    if (command == "compare-bios")
    {
        if (bios is null)
        {
            throw new ArgumentException("compare-bios requires --bios gba_bios.bin.");
        }

        return CompareBios(cartridge, bios, rawArgs.Skip(2).ToArray());
    }

    var gba = new GbaSystem(bios);
    gba.LoadCartridge(cartridge);

    return command switch
    {
        "run" => Run(gba, rawArgs.Skip(2).ToArray()),
        "test-rom" => TestRom(gba, rawArgs.Skip(2).ToArray()),
        "dump-frame" => DumpFrame(gba, rawArgs.Skip(2).ToArray()),
        "capture-frames" => CaptureFrames(gba, rawArgs.Skip(2).ToArray()),
        "verify-frame" => VerifyFrame(gba, rawArgs.Skip(2).ToArray()),
        _ => PrintInfo(cartridge, gba, romPath)
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (NotSupportedException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}

static byte[]? ExtractBiosOption(List<string> args)
{
    for (var i = 0; i < args.Count; i++)
    {
        if (!string.Equals(args[i], "--bios", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (i + 1 >= args.Count)
        {
            throw new ArgumentException("--bios requires a file path.");
        }

        var path = args[i + 1];
        var bytes = File.ReadAllBytes(path);
        args.RemoveRange(i, 2);
        return bytes;
    }

    return null;
}

static async Task<int> RunSaveProbe(string[] args)
{
    var root = args.ElementAtOrDefault(0);
    if (string.IsNullOrWhiteSpace(root))
    {
        throw new ArgumentException("save-probe requires a ROM directory.");
    }

    int? limit = null;
    var startIndex = 1;
    SortedSet<int>? explicitIndexes = null;
    var outputPath = "save-probe.csv";
    var summaryOutputPath = "";

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedLimit):
                limit = parsedLimit;
                i++;
                break;

            case "--start-index" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedStartIndex):
                if (parsedStartIndex <= 0)
                {
                    throw new ArgumentException("--start-index must be one or greater.");
                }

                startIndex = parsedStartIndex;
                i++;
                break;

            case "--indexes" when i + 1 < args.Length:
                explicitIndexes = ParseIndexSet(args[++i]);
                break;

            case "--output" when i + 1 < args.Length:
                outputPath = args[++i];
                break;

            case "--summary-output" when i + 1 < args.Length:
                summaryOutputPath = args[++i];
                break;
        }
    }

    var rootFullPath = Path.GetFullPath(root);
    var indexedRoms = Directory.EnumerateFiles(rootFullPath, "*.gba", SearchOption.AllDirectories)
        .Order(StringComparer.OrdinalIgnoreCase)
        .Select((path, index) => (Path: path, Index: index + 1));

    if (explicitIndexes is not null)
    {
        indexedRoms = indexedRoms.Where(rom => explicitIndexes.Contains(rom.Index));
    }
    else
    {
        indexedRoms = indexedRoms
            .Skip(startIndex - 1)
            .Take(limit ?? int.MaxValue);
    }

    var roms = indexedRoms.ToArray();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    await using var stream = File.Create(outputPath);
    await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
    await writer.WriteLineAsync("index,status,saveType,saveSize,writtenBytes,verifiedBytes,title,gameCode,error,path");

    var results = new List<SaveProbeResult>();
    foreach (var rom in roms)
    {
        var result = await ProbeSaveRom(rootFullPath, rom.Path, rom.Index);
        results.Add(result);
        await writer.WriteLineAsync(result.ToCsv());
        await writer.FlushAsync();
        Console.WriteLine($"#{result.Index,-4} {result.Status,-9} {result.SaveType,-9} bytes={result.SaveSize,6} verified={result.VerifiedBytes,4} {Display(result.Title)} ({Display(result.GameCode)})");
    }

    if (!string.IsNullOrWhiteSpace(summaryOutputPath))
    {
        WriteSaveProbeSummary(summaryOutputPath, results);
        Console.WriteLine($"Save probe summary: {Path.GetFullPath(summaryOutputPath)}");
    }

    var failed = results.Count(result => result.Status is "fail" or "crash");
    Console.WriteLine($"Save probes={results.Count}, ok={results.Count(result => result.Status == "ok")}, noSave={results.Count(result => result.Status == "no-save")}, failed={failed}");
    return failed == 0 ? 0 : 4;
}

static async Task<SaveProbeResult> ProbeSaveRom(string rootFullPath, string romPath, int index)
{
    Cartridge? cartridge = null;
    try
    {
        cartridge = await Cartridge.LoadFileAsync(romPath);
        var relativePath = Path.GetRelativePath(rootFullPath, romPath);
        if (cartridge.SaveType == SaveType.None)
        {
            return SaveProbeResult.FromCartridge(index, "no-save", cartridge, 0, 0, "", relativePath);
        }

        var bus = new MemoryBus();
        bus.LoadCartridge(cartridge);
        var written = ExerciseSaveBackend(bus, cartridge);
        var exported = bus.ExportSaveData().ToArray();

        var target = new MemoryBus();
        target.LoadCartridge(cartridge);
        target.LoadSaveData(exported);
        var verified = VerifySaveBackend(target, cartridge);
        var status = verified == written ? "ok" : "fail";
        var error = status == "ok" ? "" : $"verified {verified} of {written} bytes";
        return SaveProbeResult.FromCartridge(index, status, cartridge, written, verified, error, relativePath);
    }
    catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException or UnauthorizedAccessException or IndexOutOfRangeException)
    {
        return SaveProbeResult.FromCartridge(index, "crash", cartridge, 0, 0, ex.Message, Path.GetRelativePath(rootFullPath, romPath));
    }
}

static int ExerciseSaveBackend(MemoryBus bus, Cartridge cartridge)
{
    return cartridge.SaveType switch
    {
        SaveType.Sram => WriteSramProbe(bus),
        SaveType.Flash64K => WriteFlashProbe(bus, bankCount: 1),
        SaveType.Flash128K => WriteFlashProbe(bus, bankCount: 2),
        SaveType.Eeprom => WriteEepromProbe(bus, cartridge),
        _ => 0
    };
}

static int VerifySaveBackend(MemoryBus bus, Cartridge cartridge)
{
    return cartridge.SaveType switch
    {
        SaveType.Sram => VerifySramProbe(bus),
        SaveType.Flash64K => VerifyFlashProbe(bus, bankCount: 1),
        SaveType.Flash128K => VerifyFlashProbe(bus, bankCount: 2),
        SaveType.Eeprom => VerifyEepromProbe(bus, cartridge),
        _ => 0
    };
}

static int WriteSramProbe(MemoryBus bus)
{
    var count = 0;
    foreach (var (address, value) in SaveProbeBytes())
    {
        bus.Write8(GbaMemoryMap.GamePakSramStart + address, value);
        count++;
    }

    return count;
}

static int VerifySramProbe(MemoryBus bus)
{
    var count = 0;
    foreach (var (address, value) in SaveProbeBytes())
    {
        if (bus.Read8(GbaMemoryMap.GamePakSramStart + address) == value)
        {
            count++;
        }
    }

    return count;
}

static int WriteFlashProbe(MemoryBus bus, int bankCount)
{
    var count = 0;
    for (var bank = 0; bank < bankCount; bank++)
    {
        SelectFlashBank(bus, bank);
        foreach (var (address, value) in SaveProbeBytes())
        {
            ProgramFlashByte(bus, address, unchecked((byte)(value + bank * 0x31)));
            count++;
        }
    }

    return count;
}

static int VerifyFlashProbe(MemoryBus bus, int bankCount)
{
    var count = 0;
    for (var bank = 0; bank < bankCount; bank++)
    {
        SelectFlashBank(bus, bank);
        foreach (var (address, value) in SaveProbeBytes())
        {
            if (bus.Read8(GbaMemoryMap.GamePakSramStart + address) == unchecked((byte)(value + bank * 0x31)))
            {
                count++;
            }
        }
    }

    return count;
}

static int WriteEepromProbe(MemoryBus bus, Cartridge cartridge)
{
    var addressBits = cartridge.Rom.Length >= 16 * 1024 * 1024 ? 14 : 6;
    var count = 0;
    foreach (var (address, value) in EepromProbeBlocks(addressBits))
    {
        WriteEepromBlock(bus, address, addressBits, value);
        count += 8;
    }

    return count;
}

static int VerifyEepromProbe(MemoryBus bus, Cartridge cartridge)
{
    var addressBits = cartridge.Rom.Length >= 16 * 1024 * 1024 ? 14 : 6;
    var count = 0;
    foreach (var (address, value) in EepromProbeBlocks(addressBits))
    {
        if (ReadEepromBlock(bus, address, addressBits) == value)
        {
            count += 8;
        }
    }

    return count;
}

static IEnumerable<(uint Address, byte Value)> SaveProbeBytes()
{
    yield return (0x0000, 0x12);
    yield return (0x0017, 0xA5);
    yield return (0x1234, 0x5A);
    yield return (0x7FFE, 0xC3);
}

static IEnumerable<(int Address, ulong Value)> EepromProbeBlocks(int addressBits)
{
    yield return (3, 0x0123_4567_89AB_CDEFul);
    yield return (addressBits == 14 ? 0x1234 : 0x12, 0xA5A5_5A5A_F00D_CAFEul);
}

static void ProgramFlashByte(MemoryBus bus, uint address, byte value)
{
    bus.Write8(0x0E00_5555, 0xAA);
    bus.Write8(0x0E00_2AAA, 0x55);
    bus.Write8(0x0E00_5555, 0xA0);
    bus.Write8(GbaMemoryMap.GamePakSramStart + address, value);
}

static void SelectFlashBank(MemoryBus bus, int bank)
{
    bus.Write8(0x0E00_5555, 0xAA);
    bus.Write8(0x0E00_2AAA, 0x55);
    bus.Write8(0x0E00_5555, 0xB0);
    bus.Write8(0x0E00_0000, (byte)bank);
}

static void WriteEepromBlock(MemoryBus bus, int address, int addressBits, ulong value)
{
    WriteEepromBits(bus, 0b10, 2);
    WriteEepromBits(bus, (ulong)address, addressBits);
    WriteEepromBits(bus, value, 64);
    WriteEepromBits(bus, 0, 1);
}

static ulong ReadEepromBlock(MemoryBus bus, int address, int addressBits)
{
    WriteEepromBits(bus, 0b11, 2);
    WriteEepromBits(bus, (ulong)address, addressBits);
    WriteEepromBits(bus, 0, 1);

    for (var i = 0; i < 4; i++)
    {
        _ = bus.Read16(0x0D00_0000);
    }

    ulong value = 0;
    for (var i = 0; i < 64; i++)
    {
        value = (value << 1) | (uint)(bus.Read16(0x0D00_0000) & 1);
    }

    return value;
}

static void WriteEepromBits(MemoryBus bus, ulong value, int bits)
{
    for (var bit = bits - 1; bit >= 0; bit--)
    {
        bus.Write16(0x0D00_0000, (ushort)((value >> bit) & 1));
    }
}

static void WriteSaveProbeSummary(string outputPath, IReadOnlyList<SaveProbeResult> results)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    using var writer = new StreamWriter(outputPath);
    writer.WriteLine("group,key,count");
    foreach (var group in results.GroupBy(result => result.Status).OrderByDescending(group => group.Count()).ThenBy(group => group.Key))
    {
        writer.WriteLine($"{Csv("status")},{Csv(group.Key)},{group.Count()}");
    }

    foreach (var group in results.GroupBy(result => result.SaveType).OrderByDescending(group => group.Count()).ThenBy(group => group.Key))
    {
        writer.WriteLine($"{Csv("saveType")},{Csv(group.Key)},{group.Count()}");
    }

    foreach (var group in results.GroupBy(result => $"{result.SaveType}:{result.Status}").OrderByDescending(group => group.Count()).ThenBy(group => group.Key))
    {
        writer.WriteLine($"{Csv("saveType_status")},{Csv(group.Key)},{group.Count()}");
    }
}

static int Run(GbaSystem gba, string[] args)
{
    var options = ParseRunOptions(args);
    var inputState = new InputEventState();
    var frame = 0;
    gba.Video.VBlankStarted += () => frame++;
    LoadSaveFileIfRequested(gba, options);
    if (options.AlignRomEntry)
    {
        gba.Keypad.SetPressedKeys(GbaKey.None);
        if (!AlignToRomEntry(gba, options.MaxSteps, out var alignStatus, out var alignSteps))
        {
            Console.WriteLine($"TIMEOUT: {alignStatus} before ROM entry after {alignSteps:N0} steps at PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
            return 5;
        }

        frame = 0;
        inputState = new InputEventState();
    }

    gba.Keypad.SetPressedKeys(options.Keys);
    InstallWatchReads(gba, options, () => frame);
    InstallWatchWrites(gba, options, () => frame);
    InstallSwiTrace(gba, options, () => frame);
    InstallIrqTrace(gba, options, () => frame);
    InstallDmaTrace(gba, options, () => frame);
    InstallEepromTrace(gba, options, () => frame);
    using var snapshots = OpenSnapshotWriter(options);
    using var pcSnapshots = OpenPcSnapshotWriter(options);
    using var audioWav = OpenAudioWavWriter(options, gba);
    var traceTail = CreateTraceTail(options);
    var traceLimiter = CreateTraceLimiter(options);
    var wallClockLimit = StartWallClockLimit(options);
    var hitWallClockLimit = false;
    var stopPcHits = 0;
    var snapshotPcHits = 0;
    for (long step = 0; step < options.MaxSteps; step++)
    {
        if (ShouldStopAtWallClock(options, wallClockLimit))
        {
            hitWallClockLimit = true;
            break;
        }

        ApplyInputEvents(gba, options, inputState, step, frame);
        ApplyFramePokeEvents(gba, options, inputState, step, frame);
        if (StopIfInvalidPc(gba, options, traceTail, step))
        {
            return 6;
        }

        if (StopIfRequestedPc(gba, options, traceTail, step, frame, ref stopPcHits))
        {
            audioWav?.Finish(gba.Scheduler.Now);
            WriteSaveFileIfRequested(gba, options);
            return 0;
        }

        SnapshotIfRequestedPc(gba, options, pcSnapshots, step, frame, ref snapshotPcHits);
        RecordTraceTailIfNeeded(gba, step, frame, options, traceTail);
        TraceIfNeeded(gba, step, frame, options, traceLimiter);
        try
        {
            gba.Step();
        }
        catch (Exception ex)
        {
            return ReportExecutionException(gba, options, traceTail, ex, step, frame);
        }

        ApplyFrameHashEvents(gba, options, inputState, step, frame);
        ApplyMemoryTriggerEvents(gba, options, inputState, step, frame);
        WriteSnapshotIfNeeded(gba, options, snapshots, frame);
        if (ShouldStopAtFrame(options, frame))
        {
            break;
        }
    }

    if (hitWallClockLimit)
    {
        Console.WriteLine($"TIMEOUT: wall-clock>{options.MaxSeconds!.Value.ToString(CultureInfo.InvariantCulture)}s at frame={frame:N0}, PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
    }

    Console.WriteLine($"Stopped after {options.MaxSteps:N0} steps at PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
    audioWav?.Finish(gba.Scheduler.Now);
    if (audioWav is not null)
    {
        Console.WriteLine($"Wrote {audioWav.FrameCount:N0} stereo audio frames to {Path.GetFullPath(options.AudioWav!)}.");
    }

    DumpMemoryIfRequested(gba, options);
    PrintStateIfRequested(gba, options);
    WriteSaveFileIfRequested(gba, options);
    return hitWallClockLimit ? 5 : 0;
}

static int CompareBios(Cartridge cartridge, byte[] bios, string[] args)
{
    var outputPath = "bios-compare.csv";
    var startFrame = 1;
    var frameInterval = 1;
    var firstDiffOnly = false;
    var gameStateOnly = false;
    var alignRomEntry = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--compare-output" when i + 1 < args.Length:
                outputPath = args[++i];
                break;

            case "--compare-start-frame" when i + 1 < args.Length:
                startFrame = ParseNonNegativeInt(args[++i], "compare start frame");
                break;

            case "--compare-frame-interval" when i + 1 < args.Length:
                frameInterval = ParsePositiveInt(args[++i], "compare frame interval");
                break;

            case "--compare-first-diff-only":
                firstDiffOnly = true;
                break;

            case "--compare-game-state-only":
                gameStateOnly = true;
                break;

            case "--compare-align-rom-entry":
                alignRomEntry = true;
                break;
        }
    }

    var options = ParseRunOptions(args);
    var noBios = new GbaSystem();
    noBios.LoadCartridge(cartridge);
    var realBios = new GbaSystem(bios);
    realBios.LoadCartridge(cartridge);

    var noBiosResult = CollectFrameStates(noBios, options, "nobios", startFrame, frameInterval, alignRomEntry);
    var realBiosResult = CollectFrameStates(realBios, options, "realbios", startFrame, frameInterval, alignRomEntry);
    var comparisons = CompareFrameStates(noBiosResult.States, realBiosResult.States, firstDiffOnly, gameStateOnly);
    WriteFrameStateComparison(outputPath, comparisons);

    var firstDiff = comparisons.FirstOrDefault();
    Console.WriteLine($"No-BIOS: status={noBiosResult.Status} frames={noBiosResult.Frame:N0} steps={noBiosResult.Steps:N0} cycles={noBiosResult.Cycles:N0} pc=0x{noBiosResult.Pc:X8}");
    Console.WriteLine($"Real BIOS: status={realBiosResult.Status} frames={realBiosResult.Frame:N0} steps={realBiosResult.Steps:N0} cycles={realBiosResult.Cycles:N0} pc=0x{realBiosResult.Pc:X8}");
    Console.WriteLine($"Wrote {comparisons.Count:N0} comparison rows to {Path.GetFullPath(outputPath)}.");
    if (firstDiff is { } diff)
    {
        Console.WriteLine($"First diff: frame={diff.Frame:N0} field={diff.Field} nobios={diff.NoBios} realbios={diff.RealBios}");
        return 4;
    }

    Console.WriteLine("No sampled differences found.");
    return noBiosResult.Status == "ok" && realBiosResult.Status == "ok" ? 0 : 5;
}

static FrameStateRunResult CollectFrameStates(GbaSystem gba, RunOptions options, string label, int startFrame, int frameInterval, bool alignRomEntry)
{
    var inputState = new InputEventState();
    var states = new List<FrameState>();
    var frame = 0;
    var lastCapturedFrame = -1;
    var status = "ok";
    var wallClockLimit = StartWallClockLimit(options);
    gba.Video.VBlankStarted += () => frame++;
    LoadSaveFileIfRequested(gba, options);
    gba.Keypad.SetPressedKeys(options.Keys);
    if (alignRomEntry && !AlignToRomEntry(gba, options.MaxSteps, out var alignStatus, out var alignSteps))
    {
        return new FrameStateRunResult(label, alignStatus, states, frame, alignSteps, gba.Scheduler.Now, gba.Cpu.Pc);
    }

    if (alignRomEntry)
    {
        frame = 0;
        inputState = new InputEventState();
        gba.Keypad.SetPressedKeys(options.Keys);
    }

    long step;
    for (step = 0; step < options.MaxSteps; step++)
    {
        if (ShouldStopAtWallClock(options, wallClockLimit))
        {
            status = "timeout";
            break;
        }

        ApplyInputEvents(gba, options, inputState, step, frame);
        ApplyFramePokeEvents(gba, options, inputState, step, frame);
        try
        {
            gba.Step();
        }
        catch (Exception ex)
        {
            status = $"{ex.GetType().Name}:{ex.Message}";
            break;
        }

        ApplyFrameHashEvents(gba, options, inputState, step, frame);
        ApplyMemoryTriggerEvents(gba, options, inputState, step, frame);

        if (frame != lastCapturedFrame && frame >= startFrame && frame % frameInterval == 0)
        {
            states.Add(CaptureFrameState(gba, label, frame, step + 1));
            lastCapturedFrame = frame;
        }

        if (ShouldStopAtFrame(options, frame))
        {
            break;
        }
    }

    if (step >= options.MaxSteps && (options.StopFrame is null || frame < options.StopFrame))
    {
        status = "max-steps";
    }

    return new FrameStateRunResult(label, status, states, frame, step, gba.Scheduler.Now, gba.Cpu.Pc);
}

static bool AlignToRomEntry(GbaSystem gba, long maxSteps, out string status, out long steps)
{
    status = "ok";
    for (steps = 0; steps < maxSteps; steps++)
    {
        if (IsRomPc(gba.Cpu.Pc))
        {
            return true;
        }

        try
        {
            gba.Step();
        }
        catch (Exception ex)
        {
            status = $"align:{ex.GetType().Name}:{ex.Message}";
            return false;
        }
    }

    status = "align:max-steps";
    return false;
}

static bool IsRomPc(uint pc) => (pc >> 24) is >= 0x08 and <= 0x0D;

static FrameState CaptureFrameState(GbaSystem gba, string label, int frame, long step)
{
    var worklistHead = gba.Bus.Read32(0x0300_0028);
    var worklistCount = gba.Bus.Read32(0x0300_0024);
    return new FrameState(
        label,
        frame,
        step,
        gba.Scheduler.Now,
        gba.Cpu.Pc,
        gba.Cpu.Cpsr,
        gba.Cpu.Mode.ToString(),
        gba.Cpu.ThumbState,
        gba.Cpu[13],
        gba.Cpu[14],
        gba.Bus.DisplayControl,
        gba.Bus.DisplayStatus,
        gba.Bus.VerticalCount,
        gba.Bus.InterruptEnable,
        gba.Bus.InterruptFlags,
        gba.Bus.InterruptMasterEnable,
        gba.Bus.BiosInterruptFlags,
        gba.Bus.PeekIo16(IoRegisters.KEYINPUT),
        gba.Keypad.PressedKeys.ToString(),
        gba.Bus.Read32(0x0300_0020),
        worklistCount,
        worklistHead,
        gba.Bus.Read32(0x0300_002C),
        gba.Bus.Read32(0x0300_7FFC),
        gba.Bus.Read32(0x0300_0100),
        gba.Bus.Read32(0x0300_02DC),
        HashMemory(gba, 0x0300_0100, 0x200),
        worklistHead >= GbaMemoryMap.IwramStart && worklistHead < GbaMemoryMap.IwramStart + GbaMemoryMap.IwramSize
            ? HashMemory(gba, worklistHead, Math.Min(0x400, Math.Max(0, (int)(GbaMemoryMap.IwramStart + GbaMemoryMap.IwramSize - worklistHead))))
            : 0,
        HashFramebuffer(gba.Video.Framebuffer));
}

static ulong HashMemory(GbaSystem gba, uint address, int length)
{
    const ulong offsetBasis = 14_695_981_039_346_656_037;
    const ulong prime = 1_099_511_628_211;
    var hash = offsetBasis;
    for (var i = 0; i < length; i++)
    {
        hash ^= gba.Bus.Read8(address + (uint)i);
        hash *= prime;
    }

    return hash;
}

static List<FrameStateDifference> CompareFrameStates(IReadOnlyList<FrameState> noBiosStates, IReadOnlyList<FrameState> realBiosStates, bool firstDiffOnly, bool gameStateOnly)
{
    var realByFrame = realBiosStates.ToDictionary(state => state.Frame);
    var differences = new List<FrameStateDifference>();
    foreach (var noBios in noBiosStates)
    {
        if (!realByFrame.TryGetValue(noBios.Frame, out var realBios))
        {
            differences.Add(new FrameStateDifference(noBios.Frame, "frame", "present", "missing"));
            if (firstDiffOnly)
            {
                break;
            }

            continue;
        }

        if (!gameStateOnly)
        {
            AddFrameDiff(differences, noBios.Frame, "pc", Hex32(noBios.Pc), Hex32(realBios.Pc));
            AddFrameDiff(differences, noBios.Frame, "cpsr", Hex32(noBios.Cpsr), Hex32(realBios.Cpsr));
            AddFrameDiff(differences, noBios.Frame, "mode", noBios.Mode, realBios.Mode);
            AddFrameDiff(differences, noBios.Frame, "thumb", noBios.Thumb ? "1" : "0", realBios.Thumb ? "1" : "0");
            AddFrameDiff(differences, noBios.Frame, "sp", Hex32(noBios.Sp), Hex32(realBios.Sp));
            AddFrameDiff(differences, noBios.Frame, "lr", Hex32(noBios.Lr), Hex32(realBios.Lr));
            AddFrameDiff(differences, noBios.Frame, "cycles", noBios.Cycles.ToString(CultureInfo.InvariantCulture), realBios.Cycles.ToString(CultureInfo.InvariantCulture));
        }
        AddFrameDiff(differences, noBios.Frame, "dispcnt", Hex16(noBios.DisplayControl), Hex16(realBios.DisplayControl));
        AddFrameDiff(differences, noBios.Frame, "dispstat", Hex16(noBios.DisplayStatus), Hex16(realBios.DisplayStatus));
        AddFrameDiff(differences, noBios.Frame, "vcount", noBios.VerticalCount.ToString(CultureInfo.InvariantCulture), realBios.VerticalCount.ToString(CultureInfo.InvariantCulture));
        AddFrameDiff(differences, noBios.Frame, "ie", Hex16(noBios.InterruptEnable), Hex16(realBios.InterruptEnable));
        AddFrameDiff(differences, noBios.Frame, "if", Hex16(noBios.InterruptFlags), Hex16(realBios.InterruptFlags));
        AddFrameDiff(differences, noBios.Frame, "ime", noBios.InterruptMasterEnable ? "1" : "0", realBios.InterruptMasterEnable ? "1" : "0");
        AddFrameDiff(differences, noBios.Frame, "biosIf", Hex16(noBios.BiosInterruptFlags), Hex16(realBios.BiosInterruptFlags));
        AddFrameDiff(differences, noBios.Frame, "keyInput", Hex16(noBios.KeyInput), Hex16(realBios.KeyInput));
        AddFrameDiff(differences, noBios.Frame, "pressedKeys", noBios.PressedKeys, realBios.PressedKeys);
        AddFrameDiff(differences, noBios.Frame, "g03000020", Hex32(noBios.Global20), Hex32(realBios.Global20));
        AddFrameDiff(differences, noBios.Frame, "g03000024", Hex32(noBios.Global24), Hex32(realBios.Global24));
        AddFrameDiff(differences, noBios.Frame, "g03000028", Hex32(noBios.Global28), Hex32(realBios.Global28));
        AddFrameDiff(differences, noBios.Frame, "g0300002C", Hex32(noBios.Global2C), Hex32(realBios.Global2C));
        AddFrameDiff(differences, noBios.Frame, "irqHandler", Hex32(noBios.IrqHandler), Hex32(realBios.IrqHandler));
        AddFrameDiff(differences, noBios.Frame, "helper100", Hex32(noBios.Helper100), Hex32(realBios.Helper100));
        AddFrameDiff(differences, noBios.Frame, "helper2DC", Hex32(noBios.Helper2Dc), Hex32(realBios.Helper2Dc));
        AddFrameDiff(differences, noBios.Frame, "helperHash", Hex64(noBios.HelperHash), Hex64(realBios.HelperHash));
        AddFrameDiff(differences, noBios.Frame, "worklistHash", Hex64(noBios.WorklistHash), Hex64(realBios.WorklistHash));
        AddFrameDiff(differences, noBios.Frame, "frameHash", Hex64(noBios.FrameHash), Hex64(realBios.FrameHash));

        if (firstDiffOnly && differences.Count > 0)
        {
            break;
        }
    }

    return differences;
}

static void AddFrameDiff(List<FrameStateDifference> differences, int frame, string field, string noBios, string realBios)
{
    if (!string.Equals(noBios, realBios, StringComparison.Ordinal))
    {
        differences.Add(new FrameStateDifference(frame, field, noBios, realBios));
    }
}

static void WriteFrameStateComparison(string outputPath, IReadOnlyList<FrameStateDifference> differences)
{
    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var writer = new StreamWriter(fullPath, append: false, System.Text.Encoding.ASCII);
    writer.WriteLine("frame,field,nobios,realbios");
    foreach (var difference in differences)
    {
        writer.WriteLine($"{difference.Frame},{Csv(difference.Field)},{Csv(difference.NoBios)},{Csv(difference.RealBios)}");
    }
}

static string Hex16(ushort value) => $"0x{value:X4}";

static string Hex32(uint value) => $"0x{value:X8}";

static string Hex64(ulong value) => $"0x{value:X16}";

static async Task<int> RunCompatibility(string[] args, byte[]? bios)
{
    var root = args.ElementAtOrDefault(0);
    if (string.IsNullOrWhiteSpace(root))
    {
        throw new ArgumentException("compat requires a ROM directory.");
    }

    var maxSteps = 5_000_000;
    var frameStepBudget = 150_000;
    int? stopFrame = 120;
    int? limit = null;
    var startIndex = 1;
    SortedSet<int>? explicitIndexes = null;
    int? maxSeconds = null;
    var outputPath = "compat-report.csv";
    var summaryOutputPath = "";
    var profileOutputPath = "";
    var captureDir = "";
    var captureStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var phaseName = "boot";
    var suiteName = "";
    var keys = GbaKey.None;
    var keyEvents = new List<KeyEvent>();
    var frameKeyEvents = new List<FrameKeyEvent>();
    var frameHashEvents = new List<FrameHashEvent>();
    var memoryTriggerEvents = new List<MemoryTriggerEvent>();
    var framePokeEvents = new List<FramePokeEvent>();
    int? menuSelection = null;
    var traceInput = false;
    var errorDetails = false;
    var alignRomEntry = false;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--max-steps" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedMaxSteps):
                maxSteps = parsedMaxSteps;
                i++;
                break;

            case "--frame-step-budget" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedFrameStepBudget):
                if (parsedFrameStepBudget < 0)
                {
                    throw new ArgumentException("--frame-step-budget must be zero or greater.");
                }

                frameStepBudget = parsedFrameStepBudget;
                i++;
                break;

            case "--stop-frame" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedStopFrame):
                stopFrame = parsedStopFrame;
                i++;
                break;

            case "--no-stop-frame":
                stopFrame = null;
                break;

            case "--limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedLimit):
                limit = parsedLimit;
                i++;
                break;

            case "--start-index" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedStartIndex):
                if (parsedStartIndex <= 0)
                {
                    throw new ArgumentException("--start-index must be one or greater.");
                }

                startIndex = parsedStartIndex;
                i++;
                break;

            case "--indexes" when i + 1 < args.Length:
                explicitIndexes = ParseIndexSet(args[++i]);
                break;

            case "--max-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedMaxSeconds):
                if (parsedMaxSeconds <= 0)
                {
                    throw new ArgumentException("--max-seconds must be one or greater.");
                }

                maxSeconds = parsedMaxSeconds;
                i++;
                break;

            case "--output" when i + 1 < args.Length:
                outputPath = args[++i];
                break;

            case "--summary-output" when i + 1 < args.Length:
                summaryOutputPath = args[++i];
                break;

            case "--profile-output" when i + 1 < args.Length:
                profileOutputPath = args[++i];
                break;

            case "--capture-dir" when i + 1 < args.Length:
                captureDir = args[++i];
                break;

            case "--capture-statuses" when i + 1 < args.Length:
                captureStatuses = ParseStatusSet(args[++i]);
                break;

            case "--phase" when i + 1 < args.Length:
                phaseName = args[++i];
                break;

            case "--suite" when i + 1 < args.Length:
                suiteName = args[++i];
                break;

            case "--keys" when i + 1 < args.Length:
                keys = ParseKeys(args[++i]);
                break;

            case "--key-event" when i + 1 < args.Length:
                keyEvents.Add(ParseKeyEvent(args[++i]));
                break;

            case "--frame-event" when i + 1 < args.Length:
                frameKeyEvents.Add(ParseFrameKeyEvent(args[++i]));
                break;

            case "--tap-frames" when i + 1 < args.Length:
                AddFrameTapEvents(frameKeyEvents, args[++i]);
                break;

            case "--input-script" when i + 1 < args.Length:
                AddInputScriptEvents(frameKeyEvents, args[++i]);
                break;

            case "--tap-on-hash" when i + 1 < args.Length:
                frameHashEvents.Add(ParseFrameHashEvent(args[++i]));
                break;

            case "--tap-on-memory" when i + 1 < args.Length:
                memoryTriggerEvents.Add(ParseMemoryTriggerEvent(args[++i]));
                break;

            case "--poke-frame" when i + 1 < args.Length:
                framePokeEvents.Add(ParseFramePokeEvent(args[++i]));
                break;

            case "--poke-frame" when i + 1 < args.Length:
                framePokeEvents.Add(ParseFramePokeEvent(args[++i]));
                break;

            case "--menu-select" when i + 1 < args.Length && int.TryParse(args[i + 1], out var selection):
                if (selection < 0)
                {
                    throw new ArgumentException("--menu-select must be zero or greater.");
                }

                menuSelection = selection;
                i++;
                break;

            case "--trace-input":
                traceInput = true;
                break;

            case "--error-details":
                errorDetails = true;
                break;

            case "--align-rom-entry":
                alignRomEntry = true;
                break;
        }
    }

    if (menuSelection is { } selectedIndex)
    {
        AddMenuSelectionEvents(keyEvents, selectedIndex);
    }

    keyEvents.Sort((left, right) => left.Step.CompareTo(right.Step));
    frameKeyEvents.Sort((left, right) => left.Frame.CompareTo(right.Frame));
    framePokeEvents.Sort((left, right) => left.Frame.CompareTo(right.Frame));
    var options = new RunOptions(maxSteps, null, false, keys, keyEvents, frameKeyEvents, frameHashEvents, memoryTriggerEvents, framePokeEvents, [], [], [], [], 0, false, false, false, false, 0, false, false, 0, traceInput, [], [], [], null, 0, 0, null, false, null, 1, [], 0, 6, stopFrame, null, null, null, 1, alignRomEntry, null, null, null, 44_100, 0.5);
    var phases = BuildCompatibilityPhases(suiteName, phaseName, options, frameStepBudget);
    var rootFullPath = Path.GetFullPath(root);
    var indexedRoms = Directory.EnumerateFiles(rootFullPath, "*.gba", SearchOption.AllDirectories)
        .Order(StringComparer.OrdinalIgnoreCase)
        .Select((path, index) => (Path: path, Index: index + 1));

    if (explicitIndexes is not null)
    {
        indexedRoms = indexedRoms.Where(rom => explicitIndexes.Contains(rom.Index));
    }
    else
    {
        indexedRoms = indexedRoms
            .Skip(startIndex - 1)
            .Take(limit ?? int.MaxValue);
    }

    var roms = indexedRoms
        .ToArray();

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    await using var stream = File.Create(outputPath);
    await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
    await writer.WriteLineAsync("index,phase,status,classification,frames,steps,cycles,wallMs,stepsPerSecond,framesPerSecond,framesPerMillionSteps,cyclesPerFrame,profileCpuPct,profileBusPct,profileSchedulerPct,distinctFrames,changedFrames,lastChangedFrame,staticTailFrames,firstHash,lastHash,pc,cpsr,mode,thumb,dispcnt,dispstat,vcount,ie,if,ime,activeObjects,hiddenObjects,title,gameCode,saveType,romSize,error,capture,path");
    StreamWriter? profileWriter = null;
    if (!string.IsNullOrWhiteSpace(profileOutputPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(profileOutputPath)) ?? ".");
        profileWriter = new StreamWriter(File.Create(profileOutputPath), System.Text.Encoding.UTF8);
        await profileWriter.WriteLineAsync("index,phase,status,classification,frames,steps,cycles,wallMs,stepsPerSecond,framesPerSecond,cpuMs,busMs,schedulerMs,cpuPct,busPct,schedulerPct,title,gameCode,path");
    }

    var passed = 0;
    var crashed = 0;
    var staticFrames = 0;
    var timedOut = 0;
    var results = new List<CompatibilityResult>();
    var totalRuns = roms.Length * phases.Count;
    var runIndex = 0;
    for (var index = 0; index < roms.Length; index++)
    {
        foreach (var phase in phases)
        {
            runIndex++;
            var result = await RunCompatibilityRom(rootFullPath, roms[index].Path, roms[index].Index, phase, maxSeconds, bios, captureDir, captureStatuses, errorDetails, profileWriter is not null);
            results.Add(result);
            await writer.WriteLineAsync(result.ToCsv());
            await writer.FlushAsync();
            if (profileWriter is not null)
            {
                await profileWriter.WriteLineAsync(result.ToProfileCsv());
                await profileWriter.FlushAsync();
            }

            if (result.Status == "crash")
            {
                crashed++;
            }
            else if (result.Status == "timeout")
            {
                timedOut++;
            }
            else if (result.Status == "static")
            {
                staticFrames++;
            }
            else
            {
                passed++;
            }

            Console.WriteLine($"{runIndex,4}/{totalRuns} #{result.Index,-4} {result.Phase,-12} {result.Status,-8} {result.Classification,-14} f={result.Frames,4} chg={result.ChangedFrames,4} pc=0x{result.Pc:X8} {Display(result.Title)} ({result.GameCode})");
        }
    }

    if (!string.IsNullOrWhiteSpace(summaryOutputPath))
    {
        WriteCompatibilitySummary(summaryOutputPath, results);
    }

    Console.WriteLine($"Compatibility report: {Path.GetFullPath(outputPath)}");
    if (!string.IsNullOrWhiteSpace(summaryOutputPath))
    {
        Console.WriteLine($"Compatibility summary: {Path.GetFullPath(summaryOutputPath)}");
    }
    if (profileWriter is not null)
    {
        await profileWriter.DisposeAsync();
        Console.WriteLine($"Compatibility profile: {Path.GetFullPath(profileOutputPath)}");
    }

    Console.WriteLine($"Runs={results.Count}, ROMs={roms.Length}, phases={phases.Count}, booted={passed}, static={staticFrames}, crashed={crashed}, timedOut={timedOut}");
    return crashed == 0 && timedOut == 0 ? 0 : 4;
}

static async Task<CompatibilityResult> RunCompatibilityRom(string rootFullPath, string romPath, int index, CompatibilityPhase phase, int? maxSeconds, byte[]? bios, string captureDir, IReadOnlySet<string> captureStatuses, bool errorDetails, bool profileSteps)
{
    const long compatibilityRomEntryAlignmentMaxSteps = 90_000_000;

    var relativePath = Path.GetRelativePath(rootFullPath, romPath);
    Cartridge? cartridge = null;
    GbaSystem? gba = null;
    var frames = 0;
    var hashes = new HashSet<ulong>();
    ulong firstHash = 0;
    ulong lastHash = 0;
    ulong? previousHash = null;
    var changedFrames = 0;
    var lastChangedFrame = 0;
    var steps = 0;
    var inputState = new InputEventState();
    var stopwatch = Stopwatch.StartNew();
    var stepProfile = new GbaStepProfile();
    try
    {
        cartridge = await Cartridge.LoadFileAsync(romPath);
        gba = new GbaSystem(bios);
        gba.LoadCartridge(cartridge);

        gba.Video.VBlankStarted += () =>
        {
            frames++;
            lastHash = HashFramebuffer(gba.Video.Framebuffer);
            if (frames == 1)
            {
                firstHash = lastHash;
                lastChangedFrame = 1;
            }
            else if (previousHash != lastHash)
            {
                changedFrames++;
                lastChangedFrame = frames;
            }

            previousHash = lastHash;
            hashes.Add(lastHash);
        };

        CompatibilityResult Finish(string status, string error)
        {
            var staticTailFrames = frames == 0 ? 0 : frames - lastChangedFrame;
            var result = CompatibilityResult.FromCartridge(
                index,
                phase.Name,
                status,
                ClassifyCompatibility(status, phase.Name, phase.Options.StopFrame, frames, steps, hashes.Count, changedFrames, staticTailFrames, error),
                frames,
                steps,
                gba?.Scheduler.Now ?? 0,
                stopwatch.Elapsed.TotalMilliseconds,
                stepProfile,
                hashes.Count,
                changedFrames,
                lastChangedFrame,
                staticTailFrames,
                firstHash,
                lastHash,
                gba,
                cartridge,
                error,
                "",
                relativePath);

            if (gba is not null && ShouldCaptureCompatibilityStatus(status, captureStatuses))
            {
                result = result with { CapturePath = CaptureCompatibilityFrame(captureDir, result, gba) };
            }

            return result;
        }

        if (phase.Options.AlignRomEntry)
        {
            gba.Keypad.SetPressedKeys(GbaKey.None);
            var alignMaxSteps = Math.Max(phase.Options.MaxSteps, compatibilityRomEntryAlignmentMaxSteps);
            if (!AlignToRomEntry(gba, alignMaxSteps, out var alignStatus, out _))
            {
                return Finish("timeout", alignStatus);
            }

            frames = 0;
            hashes.Clear();
            firstHash = 0;
            lastHash = 0;
            previousHash = null;
            changedFrames = 0;
            lastChangedFrame = 0;
            steps = 0;
            inputState = new InputEventState();
            stepProfile = new GbaStepProfile();
            stopwatch.Restart();
        }

        gba.Keypad.SetPressedKeys(phase.Options.Keys);

        for (; steps < phase.Options.MaxSteps; steps++)
        {
            ApplyInputEvents(gba, phase.Options, inputState, steps, frames);
            ApplyFramePokeEvents(gba, phase.Options, inputState, steps, frames);
            if (!IsExecutablePc(gba.Cpu.Pc, gba.Bus.HasBios))
            {
                return Finish("crash", "invalid-pc");
            }

            if (maxSeconds is { } seconds && stopwatch.Elapsed.TotalSeconds >= seconds)
            {
                return Finish("timeout", $"wall-clock>{seconds}s");
            }

            if (profileSteps)
            {
                gba.Step(ref stepProfile);
            }
            else
            {
                gba.Step();
            }
            ApplyFrameHashEvents(gba, phase.Options, inputState, steps, frames);
            ApplyMemoryTriggerEvents(gba, phase.Options, inputState, steps, frames);
            if (phase.Options.StopFrame is { } frameLimit && frames >= frameLimit)
            {
                steps++;
                break;
            }
        }

        if (phase.Options.StopFrame is { } requestedFrames && frames < requestedFrames && steps >= phase.Options.MaxSteps)
        {
            return Finish("timeout", $"max-steps<{requestedFrames}f");
        }

        var status = frames == 0 ? "no-video" : hashes.Count <= 1 && frames >= Math.Min(phase.Options.StopFrame ?? frames, 2) ? "static" : "boot";
        return Finish(status, "");
    }
    catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException or UnauthorizedAccessException or IndexOutOfRangeException)
    {
        var staticTailFrames = frames == 0 ? 0 : frames - lastChangedFrame;
        var result = CompatibilityResult.FromCartridge(
            index,
            phase.Name,
            "crash",
            ClassifyCompatibility("crash", phase.Name, phase.Options.StopFrame, frames, steps, hashes.Count, changedFrames, staticTailFrames, ex.Message),
            frames,
            steps,
            gba?.Scheduler.Now ?? 0,
            stopwatch.Elapsed.TotalMilliseconds,
            stepProfile,
            hashes.Count,
            changedFrames,
            lastChangedFrame,
            staticTailFrames,
            firstHash,
            lastHash,
            gba,
            cartridge,
            errorDetails ? ex.ToString() : ex.Message,
            "",
            relativePath);

        if (gba is not null && ShouldCaptureCompatibilityStatus("crash", captureStatuses))
        {
            result = result with { CapturePath = CaptureCompatibilityFrame(captureDir, result, gba) };
        }

        return result;
    }
}

static List<CompatibilityPhase> BuildCompatibilityPhases(string suiteName, string phaseName, RunOptions baseOptions, int frameStepBudget)
{
    if (string.IsNullOrWhiteSpace(suiteName) || suiteName.Equals("single", StringComparison.OrdinalIgnoreCase))
    {
        return [new CompatibilityPhase(phaseName, ApplyFrameStepBudget(baseOptions, frameStepBudget))];
    }

    if (suiteName.Equals("boot", StringComparison.OrdinalIgnoreCase))
    {
        return [new CompatibilityPhase("boot", ApplyFrameStepBudget(baseOptions with { StopFrame = baseOptions.StopFrame ?? 120 }, frameStepBudget))];
    }

    if (suiteName.Equals("standard", StringComparison.OrdinalIgnoreCase))
    {
        var boot = baseOptions with { StopFrame = baseOptions.StopFrame ?? 120 };
        var startEvents = baseOptions.FrameKeyEvents.ToList();
        AddRepeatedFrameTap(startEvents, GbaKey.Start, firstFrame: 120, intervalFrames: 60, count: 6, durationFrames: 5);
        var start = baseOptions with { StopFrame = Math.Max(baseOptions.StopFrame ?? 0, 480), FrameKeyEvents = startEvents };
        return
        [
            new CompatibilityPhase("boot", ApplyFrameStepBudget(boot, frameStepBudget)),
            new CompatibilityPhase("start-probe", ApplyFrameStepBudget(start, frameStepBudget))
        ];
    }

    if (suiteName.Equals("input", StringComparison.OrdinalIgnoreCase))
    {
        var startEvents = baseOptions.FrameKeyEvents.ToList();
        AddRepeatedFrameTap(startEvents, GbaKey.Start, firstFrame: 120, intervalFrames: 60, count: 8, durationFrames: 5);
        var broadEvents = startEvents.ToList();
        AddRepeatedFrameTap(broadEvents, GbaKey.A, firstFrame: 180, intervalFrames: 45, count: 10, durationFrames: 5);
        AddRepeatedFrameTap(broadEvents, GbaKey.Right, firstFrame: 260, intervalFrames: 90, count: 8, durationFrames: 10);
        return
        [
            new CompatibilityPhase("boot", ApplyFrameStepBudget(baseOptions with { StopFrame = baseOptions.StopFrame ?? 120 }, frameStepBudget)),
            new CompatibilityPhase("start-probe", ApplyFrameStepBudget(baseOptions with { StopFrame = Math.Max(baseOptions.StopFrame ?? 0, 600), FrameKeyEvents = startEvents }, frameStepBudget)),
            new CompatibilityPhase("broad-input", ApplyFrameStepBudget(baseOptions with { StopFrame = Math.Max(baseOptions.StopFrame ?? 0, 900), FrameKeyEvents = broadEvents }, frameStepBudget))
        ];
    }

    if (suiteName.Equals("gameplay", StringComparison.OrdinalIgnoreCase))
    {
        var startEvents = baseOptions.FrameKeyEvents.ToList();
        AddRepeatedFrameTap(startEvents, GbaKey.Start, firstFrame: 120, intervalFrames: 60, count: 10, durationFrames: 5);
        var broadEvents = startEvents.ToList();
        AddRepeatedFrameTap(broadEvents, GbaKey.A, firstFrame: 180, intervalFrames: 45, count: 12, durationFrames: 5);
        AddRepeatedFrameTap(broadEvents, GbaKey.Right, firstFrame: 260, intervalFrames: 90, count: 10, durationFrames: 10);
        var gameplayEvents = broadEvents.ToList();
        AddRepeatedFrameTap(gameplayEvents, GbaKey.A, firstFrame: 720, intervalFrames: 75, count: 14, durationFrames: 6);
        AddRepeatedFrameTap(gameplayEvents, GbaKey.B, firstFrame: 760, intervalFrames: 120, count: 9, durationFrames: 6);
        AddRepeatedFrameTap(gameplayEvents, GbaKey.Right, firstFrame: 820, intervalFrames: 160, count: 7, durationFrames: 18);
        AddRepeatedFrameTap(gameplayEvents, GbaKey.Down, firstFrame: 900, intervalFrames: 180, count: 6, durationFrames: 12);
        AddRepeatedFrameTap(gameplayEvents, GbaKey.Left, firstFrame: 1040, intervalFrames: 220, count: 4, durationFrames: 12);
        return
        [
            new CompatibilityPhase("boot", ApplyFrameStepBudget(baseOptions with { StopFrame = baseOptions.StopFrame ?? 120 }, frameStepBudget)),
            new CompatibilityPhase("start-probe", ApplyFrameStepBudget(baseOptions with { StopFrame = Math.Max(baseOptions.StopFrame ?? 0, 600), FrameKeyEvents = startEvents }, frameStepBudget)),
            new CompatibilityPhase("broad-input", ApplyFrameStepBudget(baseOptions with { StopFrame = Math.Max(baseOptions.StopFrame ?? 0, 900), FrameKeyEvents = broadEvents }, frameStepBudget)),
            new CompatibilityPhase("long-input", ApplyFrameStepBudget(baseOptions with { StopFrame = Math.Max(baseOptions.StopFrame ?? 0, 1800), FrameKeyEvents = gameplayEvents }, frameStepBudget))
        ];
    }

    throw new ArgumentException($"Unknown compatibility suite '{suiteName}'. Expected single, boot, standard, input, or gameplay.");
}

static RunOptions ApplyFrameStepBudget(RunOptions options, int frameStepBudget)
{
    if (frameStepBudget <= 0 || options.StopFrame is not { } stopFrame)
    {
        return options;
    }

    var budget = Math.Min(int.MaxValue, (long)stopFrame * frameStepBudget);
    return options with { MaxSteps = Math.Max(options.MaxSteps, (int)budget) };
}

static void AddRepeatedFrameTap(List<FrameKeyEvent> keyEvents, GbaKey keys, int firstFrame, int intervalFrames, int count, int durationFrames)
{
    for (var i = 0; i < count; i++)
    {
        var frame = firstFrame + i * intervalFrames;
        keyEvents.Add(new FrameKeyEvent(frame, keys));
        keyEvents.Add(new FrameKeyEvent(frame + durationFrames, GbaKey.None));
    }

    keyEvents.Sort((left, right) => left.Frame.CompareTo(right.Frame));
}

static HashSet<string> ParseStatusSet(string value)
{
    var statuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var status in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        statuses.Add(status);
    }

    return statuses;
}

static bool ShouldCaptureCompatibilityStatus(string status, IReadOnlySet<string> captureStatuses)
    => captureStatuses.Count > 0 && (captureStatuses.Contains("*") || captureStatuses.Contains(status));

static string CaptureCompatibilityFrame(string captureDir, CompatibilityResult result, GbaSystem gba)
{
    if (string.IsNullOrWhiteSpace(captureDir))
    {
        return "";
    }

    Directory.CreateDirectory(captureDir);
    var fileName = $"{result.Index:D5}-{SafeFileName(result.Phase)}-{SafeFileName(result.GameCode)}-{SafeFileName(result.Status)}.ppm";
    var path = Path.Combine(captureDir, fileName);
    WritePpm(path, gba.Video.Framebuffer);
    return Path.GetFullPath(path);
}

static string SafeFileName(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "blank";
    }

    var invalid = Path.GetInvalidFileNameChars();
    var chars = value.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray();
    return new string(chars);
}

static string ClassifyCompatibility(string status, string phase, int? stopFrame, int frames, int steps, int distinctFrames, int changedFrames, int staticTailFrames, string error)
{
    if (status == "crash")
    {
        return error.Contains("invalid-pc", StringComparison.OrdinalIgnoreCase) ? "invalid-pc" : "crash";
    }

    if (status == "timeout")
    {
        if (frames == 0)
        {
            return "no-video-timeout";
        }

        if (changedFrames == 0 || distinctFrames <= 1)
        {
            return "stalled-timeout";
        }

        var framesPerMillionSteps = steps > 0 ? frames * 1_000_000.0 / steps : 0;
        return framesPerMillionSteps >= 5.0 ? "slow-progress-timeout" : "very-slow-timeout";
    }

    if (status == "no-video")
    {
        return "no-video";
    }

    if (status == "static")
    {
        if (phase == "boot" && stopFrame is <= 180)
        {
            return "early-window-static";
        }

        return "static";
    }

    if (frames < 30)
    {
        return "early-video";
    }

    if (changedFrames == 0 || distinctFrames <= 1)
    {
        return "static";
    }

    if (staticTailFrames >= Math.Min(120, Math.Max(30, frames / 2)))
    {
        return "stalled-late";
    }

    return changedFrames >= 30 ? "animated" : "low-motion";
}

static void WriteCompatibilitySummary(string outputPath, IReadOnlyList<CompatibilityResult> results)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    using var writer = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
    writer.WriteLine("group,key,count");
    WriteSummaryGroup(writer, "status", results.GroupBy(result => result.Status));
    WriteSummaryGroup(writer, "classification", results.GroupBy(result => result.Classification));
    WriteSummaryGroup(writer, "phase_status", results.GroupBy(result => $"{result.Phase}:{result.Status}"));
    WriteSummaryGroup(writer, "saveType_status", results.GroupBy(result => $"{result.SaveType}:{result.Status}"));
}

static void WriteSummaryGroup(StreamWriter writer, string group, IEnumerable<IGrouping<string, CompatibilityResult>> rows)
{
    foreach (var row in rows.OrderByDescending(row => row.Count()).ThenBy(row => row.Key, StringComparer.OrdinalIgnoreCase))
    {
        writer.WriteLine($"{Csv(group)},{Csv(row.Key)},{row.Count()}");
    }
}

static int RunCompatibilitySummary(string[] args)
{
    var reportPath = args.ElementAtOrDefault(0);
    if (string.IsNullOrWhiteSpace(reportPath))
    {
        throw new ArgumentException("compat-summary requires a compatibility CSV path.");
    }

    var outputPath = args.ElementAtOrDefault(1);
    var rows = ReadCompatibilityCsv(reportPath);
    Console.WriteLine($"Compatibility rows: {rows.Count:N0}");
    PrintCompatibilityGroup("status", rows, "status");
    PrintCompatibilityGroup("classification", rows, "classification");
    PrintCompatibilityGroup("phase/status", rows, "phase", "status");

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        using var writer = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        writer.WriteLine("group,key,count");
        WriteDictionarySummary(writer, "status", rows, "status");
        WriteDictionarySummary(writer, "classification", rows, "classification");
        WriteDictionarySummary(writer, "phase_status", rows, "phase", "status");
        Console.WriteLine($"Wrote {Path.GetFullPath(outputPath)}");
    }

    return 0;
}

static List<Dictionary<string, string>> ReadCompatibilityCsv(string path)
{
    using var reader = new StreamReader(path);
    var headerLine = reader.ReadLine() ?? throw new ArgumentException($"CSV is empty: {path}");
    var headers = SplitCsvLine(headerLine);
    var rows = new List<Dictionary<string, string>>();
    while (reader.ReadLine() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var values = SplitCsvLine(line);
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count && i < values.Count; i++)
        {
            row[headers[i]] = values[i];
        }

        rows.Add(row);
    }

    return rows;
}

static List<string> SplitCsvLine(string line)
{
    var values = new List<string>();
    var value = new System.Text.StringBuilder();
    var quoted = false;
    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (quoted)
        {
            if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                value.Append('"');
                i++;
            }
            else if (ch == '"')
            {
                quoted = false;
            }
            else
            {
                value.Append(ch);
            }

            continue;
        }

        if (ch == ',')
        {
            values.Add(value.ToString());
            value.Clear();
            continue;
        }

        if (ch == '"')
        {
            quoted = true;
            continue;
        }

        value.Append(ch);
    }

    values.Add(value.ToString());
    return values;
}

static void PrintCompatibilityGroup(string label, IReadOnlyList<Dictionary<string, string>> rows, params string[] keys)
{
    Console.WriteLine(label);
    foreach (var group in GroupCompatibilityRows(rows, keys).Take(20))
    {
        Console.WriteLine($"  {group.Key}: {group.Count:N0}");
    }
}

static void WriteDictionarySummary(StreamWriter writer, string group, IReadOnlyList<Dictionary<string, string>> rows, params string[] keys)
{
    foreach (var row in GroupCompatibilityRows(rows, keys))
    {
        writer.WriteLine($"{Csv(group)},{Csv(row.Key)},{row.Count}");
    }
}

static IEnumerable<(string Key, int Count)> GroupCompatibilityRows(IReadOnlyList<Dictionary<string, string>> rows, params string[] keys)
    => rows
        .GroupBy(row => string.Join(':', keys.Select(key => row.GetValueOrDefault(key, ""))))
        .Select(group => (group.Key, group.Count()))
        .OrderByDescending(group => group.Item2)
        .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

static SortedSet<int> ParseIndexSet(string value)
{
    var indexes = new SortedSet<int>();
    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var dash = token.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            if (!int.TryParse(token[..dash], out var start)
                || !int.TryParse(token[(dash + 1)..], out var end)
                || start <= 0
                || end < start)
            {
                throw new ArgumentException($"Invalid --indexes range '{token}'. Use one-based indexes like 12,15-20.");
            }

            for (var index = start; index <= end; index++)
            {
                indexes.Add(index);
            }

            continue;
        }

        if (!int.TryParse(token, out var single) || single <= 0)
        {
            throw new ArgumentException($"Invalid --indexes value '{token}'. Use one-based indexes like 12,15-20.");
        }

        indexes.Add(single);
    }

    if (indexes.Count == 0)
    {
        throw new ArgumentException("--indexes requires at least one one-based ROM index.");
    }

    return indexes;
}

static int TestRom(GbaSystem gba, string[] args)
{
    var options = ParseRunOptions(args);
    var inputState = new InputEventState();
    var frame = 0;
    gba.Video.VBlankStarted += () => frame++;
    LoadSaveFileIfRequested(gba, options);
    gba.Keypad.SetPressedKeys(options.Keys);
    InstallWatchReads(gba, options, () => frame);
    InstallWatchWrites(gba, options, () => frame);
    InstallSwiTrace(gba, options, () => frame);
    InstallIrqTrace(gba, options, () => frame);
    InstallDmaTrace(gba, options, () => frame);
    InstallEepromTrace(gba, options, () => frame);
    using var snapshots = OpenSnapshotWriter(options);
    var traceTail = CreateTraceTail(options);
    var traceLimiter = CreateTraceLimiter(options);
    uint? successPc = null;
    uint? failurePc = null;
    var wallClockLimit = StartWallClockLimit(options);
    var stopPcHits = 0;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--success-pc" when i + 1 < args.Length:
                successPc = ParseAddress(args[++i]);
                break;

            case "--failure-pc" when i + 1 < args.Length:
                failurePc = ParseAddress(args[++i]);
                break;
        }
    }

    for (long step = 0; step < options.MaxSteps; step++)
    {
        if (ShouldStopAtWallClock(options, wallClockLimit))
        {
            Console.WriteLine($"TIMEOUT: wall-clock>{options.MaxSeconds!.Value.ToString(CultureInfo.InvariantCulture)}s at frame={frame:N0}, PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
            DumpMemoryIfRequested(gba, options);
            PrintRegisters(gba);
            PrintStateIfRequested(gba, options);
            WriteSaveFileIfRequested(gba, options);
            return 5;
        }

        ApplyInputEvents(gba, options, inputState, step, frame);
        ApplyFramePokeEvents(gba, options, inputState, step, frame);
        if (StopIfInvalidPc(gba, options, traceTail, step))
        {
            return 6;
        }

        if (StopIfRequestedPc(gba, options, traceTail, step, frame, ref stopPcHits))
        {
            WriteSaveFileIfRequested(gba, options);
            return 0;
        }

        var pc = gba.Cpu.Pc;
        if (successPc == pc)
        {
            Console.WriteLine($"PASS: reached success PC 0x{pc:X8} after {step:N0} steps, cycles={gba.Scheduler.Now:N0}.");
            WriteSaveFileIfRequested(gba, options);
            return 0;
        }

        if (failurePc == pc)
        {
            Console.WriteLine($"FAIL: reached failure PC 0x{pc:X8} after {step:N0} steps, cycles={gba.Scheduler.Now:N0}.");
            return 4;
        }

        RecordTraceTailIfNeeded(gba, step, frame, options, traceTail);
        TraceIfNeeded(gba, step, frame, options, traceLimiter);
        try
        {
            gba.Step();
        }
        catch (Exception ex)
        {
            return ReportExecutionException(gba, options, traceTail, ex, step, frame);
        }

        ApplyFrameHashEvents(gba, options, inputState, step, frame);
        ApplyMemoryTriggerEvents(gba, options, inputState, step, frame);
        WriteSnapshotIfNeeded(gba, options, snapshots, frame);
        if (ShouldStopAtFrame(options, frame))
        {
            break;
        }
    }

    Console.WriteLine($"TIMEOUT: stopped after {options.MaxSteps:N0} steps at PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
    DumpMemoryIfRequested(gba, options);
    PrintRegisters(gba);
    PrintStateIfRequested(gba, options);
    WriteSaveFileIfRequested(gba, options);
    return 5;
}

static int DumpFrame(GbaSystem gba, string[] args)
{
    var options = ParseRunOptions(args);
    var inputState = new InputEventState();
    var frame = 0;
    gba.Video.VBlankStarted += () => frame++;
    LoadSaveFileIfRequested(gba, options);
    var outputPath = "frame.ppm";
    string? debugLayerDir = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--output" when i + 1 < args.Length:
                outputPath = args[++i];
                break;

            case "--debug-layer-dir" when i + 1 < args.Length:
                debugLayerDir = args[++i];
                break;
        }
    }

    if (options.AlignRomEntry)
    {
        gba.Keypad.SetPressedKeys(GbaKey.None);
        if (!AlignToRomEntry(gba, options.MaxSteps, out var alignStatus, out var alignSteps))
        {
            Console.WriteLine($"TIMEOUT: {alignStatus} before ROM entry after {alignSteps:N0} steps at PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
            return 5;
        }

        frame = 0;
        inputState = new InputEventState();
    }

    gba.Keypad.SetPressedKeys(options.Keys);
    InstallWatchReads(gba, options, () => frame);
    InstallWatchWrites(gba, options, () => frame);
    InstallSwiTrace(gba, options, () => frame);
    InstallIrqTrace(gba, options, () => frame);
    InstallDmaTrace(gba, options, () => frame);
    InstallEepromTrace(gba, options, () => frame);
    using var snapshots = OpenSnapshotWriter(options);
    using var audioSamples = OpenAudioSampleWriter(options, gba);
    using var psgSamples = OpenPsgSampleWriter(options, gba);
    using var audioWav = OpenAudioWavWriter(options, gba);
    var traceTail = CreateTraceTail(options);
    var traceLimiter = CreateTraceLimiter(options);
    var wallClockLimit = StartWallClockLimit(options);
    var hitWallClockLimit = false;
    for (long step = 0; step < options.MaxSteps; step++)
    {
        if (ShouldStopAtWallClock(options, wallClockLimit))
        {
            hitWallClockLimit = true;
            break;
        }

        try
        {
            ApplyInputEvents(gba, options, inputState, step, frame);
            ApplyFramePokeEvents(gba, options, inputState, step, frame);
            if (StopIfInvalidPc(gba, options, traceTail, step))
            {
                return 6;
            }

            RecordTraceTailIfNeeded(gba, step, frame, options, traceTail);
            TraceIfNeeded(gba, step, frame, options, traceLimiter);
            gba.Step();
            WriteAudioSamplesIfNeeded(gba, audioSamples, step, frame);
            WritePsgSamplesIfNeeded(gba, psgSamples, step, frame);
            ApplyFrameHashEvents(gba, options, inputState, step, frame);
            ApplyMemoryTriggerEvents(gba, options, inputState, step, frame);
            WriteSnapshotIfNeeded(gba, options, snapshots, frame);
        }
        catch (Exception ex)
        {
            return ReportExecutionException(gba, options, traceTail, ex, step, frame);
        }

        if (ShouldStopAtFrame(options, frame))
        {
            break;
        }
    }

    WritePpm(outputPath, GetOutputFramebuffer(gba, options));
    if (!string.IsNullOrWhiteSpace(debugLayerDir))
    {
        WriteDebugLayerPpms(gba, debugLayerDir);
    }

    var timeoutPrefix = hitWallClockLimit
        ? $"TIMEOUT: wall-clock>{options.MaxSeconds!.Value.ToString(CultureInfo.InvariantCulture)}s; "
        : "";
    Console.WriteLine($"{timeoutPrefix}Wrote {VideoController.Width}x{VideoController.Height} frame to {Path.GetFullPath(outputPath)} at frame={frame:N0}, PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
    if (!string.IsNullOrWhiteSpace(debugLayerDir))
    {
        Console.WriteLine($"Wrote debug layer frames to {Path.GetFullPath(debugLayerDir)}.");
    }

    if (audioSamples is not null)
    {
        Console.WriteLine($"Wrote {audioSamples.Count:N0} direct sound samples to {Path.GetFullPath(options.AudioCsv!)}.");
    }

    if (psgSamples is not null)
    {
        Console.WriteLine($"Wrote {psgSamples.Count:N0} PSG samples to {Path.GetFullPath(options.PsgCsv!)}.");
    }

    audioWav?.Finish(gba.Scheduler.Now);
    if (audioWav is not null)
    {
        Console.WriteLine($"Wrote {audioWav.FrameCount:N0} stereo audio frames to {Path.GetFullPath(options.AudioWav!)}.");
    }

    DumpMemoryIfRequested(gba, options);
    PrintStateIfRequested(gba, options);
    WriteSaveFileIfRequested(gba, options);
    return hitWallClockLimit ? 5 : 0;
}

static int CaptureFrames(GbaSystem gba, string[] args)
{
    var options = ParseRunOptions(args);
    var inputState = new InputEventState();
    var frame = 0;
    gba.Video.VBlankStarted += () => frame++;
    LoadSaveFileIfRequested(gba, options);
    if (options.AlignRomEntry)
    {
        gba.Keypad.SetPressedKeys(GbaKey.None);
        if (!AlignToRomEntry(gba, options.MaxSteps, out var alignStatus, out var alignSteps))
        {
            Console.WriteLine($"TIMEOUT: {alignStatus} before ROM entry after {alignSteps:N0} steps at PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
            return 5;
        }

        frame = 0;
        inputState = new InputEventState();
    }

    gba.Keypad.SetPressedKeys(options.Keys);
    InstallWatchReads(gba, options, () => frame);
    InstallWatchWrites(gba, options, () => frame);
    InstallSwiTrace(gba, options, () => frame);
    InstallIrqTrace(gba, options, () => frame);
    InstallDmaTrace(gba, options, () => frame);
    InstallEepromTrace(gba, options, () => frame);
    using var snapshots = OpenSnapshotWriter(options);
    var outputDir = "captures";
    var sampleSteps = 50_000;
    int? sampleFrames = null;
    FrameRange? frameRange = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--output-dir" when i + 1 < args.Length:
                outputDir = args[++i];
                break;

            case "--sample-steps" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed) && parsed > 0:
                sampleSteps = parsed;
                i++;
                break;

            case "--sample-frames" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed) && parsed > 0:
                sampleFrames = parsed;
                i++;
                break;

            case "--frame-range" when i + 1 < args.Length:
                frameRange = ParseFrameRange(args[++i]);
                break;
        }
    }

    Directory.CreateDirectory(outputDir);
    ulong? lastHash = null;
    var captured = 0;
    var lastSampledFrame = -1;
    var traceTail = CreateTraceTail(options);
    var traceLimiter = CreateTraceLimiter(options);
    var wallClockLimit = StartWallClockLimit(options);
    var hitWallClockLimit = false;

    for (long step = 0; step < options.MaxSteps; step++)
    {
        if (ShouldStopAtWallClock(options, wallClockLimit))
        {
            hitWallClockLimit = true;
            break;
        }

        ApplyInputEvents(gba, options, inputState, step, frame);
        ApplyFramePokeEvents(gba, options, inputState, step, frame);
        if (StopIfInvalidPc(gba, options, traceTail, step))
        {
            return 6;
        }

        RecordTraceTailIfNeeded(gba, step, frame, options, traceTail);
        TraceIfNeeded(gba, step, frame, options, traceLimiter);
        gba.Step();
        ApplyFrameHashEvents(gba, options, inputState, step, frame);
        ApplyMemoryTriggerEvents(gba, options, inputState, step, frame);
        WriteSnapshotIfNeeded(gba, options, snapshots, frame);
        if (ShouldStopAtFrame(options, frame))
        {
            break;
        }

        var shouldSample = sampleFrames is { } frameInterval
            ? ShouldSampleFrame(frame, frameInterval, frameRange, ref lastSampledFrame)
            : step % sampleSteps == 0;
        if (!shouldSample)
        {
            continue;
        }

        var framebuffer = GetOutputFramebuffer(gba, options);
        var hash = HashFramebuffer(framebuffer);
        if (lastHash == hash)
        {
            continue;
        }

        lastHash = hash;
        var outputPath = Path.Combine(outputDir, $"frame-{captured:D4}-f-{frame:D5}-step-{step:D8}-pc-{gba.Cpu.Pc:X8}.ppm");
        WritePpm(outputPath, framebuffer);
        Console.WriteLine($"Captured {Path.GetFullPath(outputPath)} frame={frame:N0} hash=0x{hash:X16} cycles={gba.Scheduler.Now:N0}");
        captured++;
    }

    if (hitWallClockLimit)
    {
        Console.WriteLine($"TIMEOUT: wall-clock>{options.MaxSeconds!.Value.ToString(CultureInfo.InvariantCulture)}s at frame={frame:N0}, PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
    }

    Console.WriteLine($"Captured {captured:N0} distinct frames in {Path.GetFullPath(outputDir)} after {options.MaxSteps:N0} steps.");
    DumpMemoryIfRequested(gba, options);
    PrintStateIfRequested(gba, options);
    WriteSaveFileIfRequested(gba, options);
    return hitWallClockLimit ? 5 : 0;
}

static int VerifyFrame(GbaSystem gba, string[] args)
{
    var options = ParseRunOptions(args);
    var inputState = new InputEventState();
    var frame = 0;
    gba.Video.VBlankStarted += () => frame++;
    LoadSaveFileIfRequested(gba, options);
    gba.Keypad.SetPressedKeys(options.Keys);
    InstallWatchReads(gba, options, () => frame);
    InstallWatchWrites(gba, options, () => frame);
    InstallSwiTrace(gba, options, () => frame);
    InstallIrqTrace(gba, options, () => frame);
    InstallDmaTrace(gba, options, () => frame);
    InstallEepromTrace(gba, options, () => frame);
    using var snapshots = OpenSnapshotWriter(options);
    var traceTail = CreateTraceTail(options);
    var traceLimiter = CreateTraceLimiter(options);
    string? baselinePath = null;
    var actualPath = "actual.ppm";
    string? diffPath = null;
    var maxDifferentPixels = 0;
    var maxChannelDelta = 0;
    var writeBaseline = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--baseline" when i + 1 < args.Length:
                baselinePath = args[++i];
                break;

            case "--actual" when i + 1 < args.Length:
                actualPath = args[++i];
                break;

            case "--diff" when i + 1 < args.Length:
                diffPath = args[++i];
                break;

            case "--max-different-pixels" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedDifferentPixels) && parsedDifferentPixels >= 0:
                maxDifferentPixels = parsedDifferentPixels;
                i++;
                break;

            case "--max-channel-delta" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedChannelDelta) && parsedChannelDelta >= 0:
                maxChannelDelta = parsedChannelDelta;
                i++;
                break;

            case "--write-baseline":
                writeBaseline = true;
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(baselinePath))
    {
        throw new ArgumentException("verify-frame requires --baseline baseline.ppm.");
    }

    var wallClockLimit = StartWallClockLimit(options);
    var hitWallClockLimit = false;
    for (long step = 0; step < options.MaxSteps; step++)
    {
        if (ShouldStopAtWallClock(options, wallClockLimit))
        {
            hitWallClockLimit = true;
            break;
        }

        ApplyInputEvents(gba, options, inputState, step, frame);
        ApplyFramePokeEvents(gba, options, inputState, step, frame);
        if (StopIfInvalidPc(gba, options, traceTail, step))
        {
            return 6;
        }

        RecordTraceTailIfNeeded(gba, step, frame, options, traceTail);
        TraceIfNeeded(gba, step, frame, options, traceLimiter);
        gba.Step();
        ApplyFrameHashEvents(gba, options, inputState, step, frame);
        ApplyMemoryTriggerEvents(gba, options, inputState, step, frame);
        WriteSnapshotIfNeeded(gba, options, snapshots, frame);
        if (ShouldStopAtFrame(options, frame))
        {
            break;
        }
    }

    var actual = GetOutputFramebuffer(gba, options);
    WritePpm(actualPath, actual);

    if (hitWallClockLimit)
    {
        Console.WriteLine($"TIMEOUT: wall-clock>{options.MaxSeconds!.Value.ToString(CultureInfo.InvariantCulture)}s at frame={frame:N0}, PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
        DumpMemoryIfRequested(gba, options);
        PrintStateIfRequested(gba, options);
        WriteSaveFileIfRequested(gba, options);
        return 5;
    }

    if (writeBaseline)
    {
        WritePpm(baselinePath, actual);
        Console.WriteLine($"Wrote baseline {Path.GetFullPath(baselinePath)} from frame={frame:N0}, hash=0x{HashFramebuffer(actual):X16}.");
        return 0;
    }

    if (!File.Exists(baselinePath))
    {
        throw new ArgumentException($"Baseline does not exist: {baselinePath}. Use --write-baseline to create it.");
    }

    var baseline = ReadPpm(baselinePath);
    var comparison = CompareFramebuffers(baseline, actual, maxChannelDelta);
    if (!string.IsNullOrWhiteSpace(diffPath))
    {
        WritePpm(diffPath, BuildDiffFramebuffer(baseline, actual, maxChannelDelta));
    }

    var passed = comparison.DifferentPixels <= maxDifferentPixels;
    Console.WriteLine(
        "verify-frame {0}: frame={1} actual=0x{2:X16} baseline=0x{3:X16} differentPixels={4} maxDelta={5} totalDelta={6} allowedPixels={7} allowedChannelDelta={8} actualPath={9} baselinePath={10}{11}",
        passed ? "PASS" : "FAIL",
        frame,
        HashFramebuffer(actual),
        HashFramebuffer(baseline),
        comparison.DifferentPixels,
        comparison.MaxChannelDelta,
        comparison.TotalChannelDelta,
        maxDifferentPixels,
        maxChannelDelta,
        Path.GetFullPath(actualPath),
        Path.GetFullPath(baselinePath),
        string.IsNullOrWhiteSpace(diffPath) ? "" : $" diffPath={Path.GetFullPath(diffPath)}");

    DumpMemoryIfRequested(gba, options);
    PrintStateIfRequested(gba, options);
    WriteSaveFileIfRequested(gba, options);
    return passed ? 0 : 4;
}

static RunOptions ParseRunOptions(string[] args)
{
    long maxSteps = 1_000;
    double? maxSeconds = null;
    var trace = false;
    var keys = GbaKey.None;
    var keyEvents = new List<KeyEvent>();
    var frameKeyEvents = new List<FrameKeyEvent>();
    var frameHashEvents = new List<FrameHashEvent>();
    var memoryTriggerEvents = new List<MemoryTriggerEvent>();
    var framePokeEvents = new List<FramePokeEvent>();
    var watchReads = new List<uint>();
    var watchReadRanges = new List<AddressRange>();
    var watchWrites = new List<uint>();
    var watchWriteRanges = new List<AddressRange>();
    var watchLimit = 0;
    int? menuSelection = null;
    var stopOnInvalidPc = false;
    var printState = false;
    var traceSwi = false;
    var traceIrq = false;
    var traceDma = false;
    var traceIrqLimit = 0;
    var traceEeprom = false;
    var traceEepromLimit = 0;
    var traceInput = false;
    var dumps = new List<MemoryDump>();
    var instructionDumps = new List<InstructionDump>();
    var traceRanges = new List<AddressRange>();
    FrameRange? traceFrameRange = null;
    string? saveFile = null;
    var saveReadOnly = false;
    uint? stopPc = null;
    var stopPcHit = 1;
    var snapshotPcs = new List<uint>();
    var snapshotPcLimit = 0;
    var pcSnapshotStackWords = 6;
    int? stopFrame = null;
    int? debugLayer = null;
    string? snapshotCsv = null;
    string? pcSnapshotCsv = null;
    string? audioCsv = null;
    string? psgCsv = null;
    string? audioWav = null;
    var audioSampleRate = 44_100;
    var audioGain = 0.5;
    var snapshotFrames = 1;
    var traceTail = 0;
    var traceHitLimit = 0;
    var alignRomEntry = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--trace":
                trace = true;
                break;

            case "--align-rom-entry":
                alignRomEntry = true;
                break;

            case "--max-steps" when i + 1 < args.Length && long.TryParse(args[i + 1], out var parsed):
                maxSteps = parsed;
                i++;
                break;

            case "--max-seconds" when i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMaxSeconds) && parsedMaxSeconds > 0:
                maxSeconds = parsedMaxSeconds;
                i++;
                break;

            case "--keys" when i + 1 < args.Length:
                keys = ParseKeys(args[++i]);
                break;

            case "--key-event" when i + 1 < args.Length:
                keyEvents.Add(ParseKeyEvent(args[++i]));
                break;

            case "--frame-event" when i + 1 < args.Length:
                frameKeyEvents.Add(ParseFrameKeyEvent(args[++i]));
                break;

            case "--tap-frames" when i + 1 < args.Length:
                AddFrameTapEvents(frameKeyEvents, args[++i]);
                break;

            case "--input-script" when i + 1 < args.Length:
                AddInputScriptEvents(frameKeyEvents, args[++i]);
                break;

            case "--tap-on-hash" when i + 1 < args.Length:
                frameHashEvents.Add(ParseFrameHashEvent(args[++i]));
                break;

            case "--tap-on-memory" when i + 1 < args.Length:
                memoryTriggerEvents.Add(ParseMemoryTriggerEvent(args[++i]));
                break;

            case "--poke-frame" when i + 1 < args.Length:
                framePokeEvents.Add(ParseFramePokeEvent(args[++i]));
                break;

            case "--watch-read" when i + 1 < args.Length:
                watchReads.Add(ParseAddress(args[++i]));
                break;

            case "--watch-read-range" when i + 1 < args.Length:
                watchReadRanges.Add(ParseAddressRange(args[++i]));
                break;

            case "--watch-write" when i + 1 < args.Length:
                watchWrites.Add(ParseAddress(args[++i]));
                break;

            case "--watch-write-range" when i + 1 < args.Length:
                watchWriteRanges.Add(ParseAddressRange(args[++i]));
                break;

            case "--watch-limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedWatchLimit) && parsedWatchLimit >= 0:
                watchLimit = parsedWatchLimit;
                i++;
                break;

            case "--stop-on-invalid-pc":
                stopOnInvalidPc = true;
                break;

            case "--print-state":
                printState = true;
                break;

            case "--trace-swi":
                traceSwi = true;
                break;

            case "--trace-irq":
                traceIrq = true;
                break;

            case "--trace-irq-limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedTraceIrqLimit) && parsedTraceIrqLimit >= 0:
                traceIrqLimit = parsedTraceIrqLimit;
                i++;
                break;

            case "--trace-dma":
                traceDma = true;
                break;

            case "--trace-eeprom":
                traceEeprom = true;
                break;

            case "--trace-eeprom-limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedTraceEepromLimit) && parsedTraceEepromLimit >= 0:
                traceEepromLimit = parsedTraceEepromLimit;
                i++;
                break;

            case "--trace-input":
                traceInput = true;
                break;

            case "--dump-memory" when i + 1 < args.Length:
                dumps.Add(ParseMemoryDump(args[++i]));
                break;

            case "--disassemble-memory" when i + 1 < args.Length:
                instructionDumps.Add(ParseInstructionDump(args[++i]));
                break;

            case "--trace-range" when i + 1 < args.Length:
                traceRanges.Add(ParseAddressRange(args[++i]));
                break;

            case "--trace-frames" when i + 1 < args.Length:
                traceFrameRange = ParseFrameRange(args[++i]);
                break;

            case "--trace-tail" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedTraceTail) && parsedTraceTail >= 0:
                traceTail = parsedTraceTail;
                i++;
                break;

            case "--trace-hit-limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedTraceHitLimit) && parsedTraceHitLimit >= 0:
                traceHitLimit = parsedTraceHitLimit;
                i++;
                break;

            case "--stop-frame" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedStopFrame) && parsedStopFrame >= 0:
                stopFrame = parsedStopFrame;
                i++;
                break;

            case "--stop-pc" when i + 1 < args.Length:
                stopPc = ParseAddress(args[++i]);
                break;

            case "--stop-pc-hit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedStopPcHit) && parsedStopPcHit > 0:
                stopPcHit = parsedStopPcHit;
                i++;
                break;

            case "--snapshot-pc" when i + 1 < args.Length:
                snapshotPcs.Add(ParseAddress(args[++i]));
                break;

            case "--snapshot-pc-limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedSnapshotPcLimit) && parsedSnapshotPcLimit >= 0:
                snapshotPcLimit = parsedSnapshotPcLimit;
                i++;
                break;

            case "--pc-snapshot-stack-words" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPcSnapshotStackWords) && parsedPcSnapshotStackWords >= 0:
                pcSnapshotStackWords = parsedPcSnapshotStackWords;
                i++;
                break;

            case "--save-file" when i + 1 < args.Length:
                saveFile = args[++i];
                break;

            case "--save-read-only":
                saveReadOnly = true;
                break;

            case "--debug-layer" when i + 1 < args.Length:
                debugLayer = ParseDebugLayer(args[++i]);
                break;

            case "--snapshot-csv" when i + 1 < args.Length:
                snapshotCsv = args[++i];
                break;

            case "--pc-snapshot-csv" when i + 1 < args.Length:
                pcSnapshotCsv = args[++i];
                break;

            case "--audio-csv" when i + 1 < args.Length:
                audioCsv = args[++i];
                break;

            case "--psg-csv" when i + 1 < args.Length:
                psgCsv = args[++i];
                break;

            case "--audio-wav" when i + 1 < args.Length:
                audioWav = args[++i];
                break;

            case "--audio-sample-rate" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedAudioSampleRate) && parsedAudioSampleRate > 0:
                audioSampleRate = parsedAudioSampleRate;
                i++;
                break;

            case "--audio-gain" when i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedAudioGain) && parsedAudioGain > 0:
                audioGain = parsedAudioGain;
                i++;
                break;

            case "--snapshot-frames" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedSnapshotFrames) && parsedSnapshotFrames > 0:
                snapshotFrames = parsedSnapshotFrames;
                i++;
                break;

            case "--menu-select" when i + 1 < args.Length && int.TryParse(args[i + 1], out var selection):
                if (selection < 0)
                {
                    throw new ArgumentException("--menu-select must be zero or greater.");
                }

                menuSelection = selection;
                i++;
                break;
        }
    }

    if (menuSelection is { } selectedIndex)
    {
        AddMenuSelectionEvents(keyEvents, selectedIndex);
    }

    keyEvents.Sort((left, right) => left.Step.CompareTo(right.Step));
    frameKeyEvents.Sort((left, right) => left.Frame.CompareTo(right.Frame));
    framePokeEvents.Sort((left, right) => left.Frame.CompareTo(right.Frame));
    return new RunOptions(maxSteps, maxSeconds, trace, keys, keyEvents, frameKeyEvents, frameHashEvents, memoryTriggerEvents, framePokeEvents, watchReads, watchReadRanges, watchWrites, watchWriteRanges, watchLimit, stopOnInvalidPc, printState, traceSwi, traceIrq, traceIrqLimit, traceDma, traceEeprom, traceEepromLimit, traceInput, dumps, instructionDumps, traceRanges, traceFrameRange, traceTail, traceHitLimit, saveFile, saveReadOnly, stopPc, stopPcHit, snapshotPcs, snapshotPcLimit, pcSnapshotStackWords, stopFrame, debugLayer, snapshotCsv, pcSnapshotCsv, snapshotFrames, alignRomEntry, audioCsv, psgCsv, audioWav, audioSampleRate, audioGain);
}

static void AddMenuSelectionEvents(List<KeyEvent> keyEvents, int selectedIndex)
{
    const int firstPressStep = 300_000;
    const int pressDuration = 350_000;
    const int pressInterval = 650_000;

    for (var i = 0; i < selectedIndex; i++)
    {
        var pressStep = firstPressStep + i * pressInterval;
        keyEvents.Add(new KeyEvent(pressStep, GbaKey.Down));
        keyEvents.Add(new KeyEvent(pressStep + pressDuration, GbaKey.None));
    }

    var startStep = firstPressStep + selectedIndex * pressInterval;
    keyEvents.Add(new KeyEvent(startStep, GbaKey.Start));
    keyEvents.Add(new KeyEvent(startStep + pressDuration, GbaKey.None));
}

static void ApplyInputEvents(GbaSystem gba, RunOptions options, InputEventState state, long step, int frame)
{
    if (state.HashReleaseFrame is { } releaseFrame && releaseFrame <= frame)
    {
        state.HashReleaseFrame = null;
        gba.Keypad.SetPressedKeys(GbaKey.None);
        if (options.Trace || options.TraceInput)
        {
            Console.WriteLine($"{step:D8} FRAME {frame:D5} HASH-RELEASE");
        }
    }

    while (state.NextStepEvent < options.KeyEvents.Count && options.KeyEvents[state.NextStepEvent].Step <= step)
    {
        var keyEvent = options.KeyEvents[state.NextStepEvent++];
        if (keyEvent.Step == step)
        {
            gba.Keypad.SetPressedKeys(keyEvent.Keys);
            if (options.Trace || options.TraceInput)
            {
                Console.WriteLine($"{step:D8} KEYS {keyEvent.Keys}");
            }
        }
    }

    while (state.NextFrameEvent < options.FrameKeyEvents.Count && options.FrameKeyEvents[state.NextFrameEvent].Frame <= frame)
    {
        var keyEvent = options.FrameKeyEvents[state.NextFrameEvent++];
        gba.Keypad.SetPressedKeys(keyEvent.Keys);
        if (options.Trace || options.TraceInput)
        {
            Console.WriteLine($"{step:D8} FRAME {frame:D5} KEYS {keyEvent.Keys}");
        }
    }
}

static void ApplyFramePokeEvents(GbaSystem gba, RunOptions options, InputEventState state, long step, int frame)
{
    while (state.NextFramePokeEvent < options.FramePokeEvents.Count && options.FramePokeEvents[state.NextFramePokeEvent].Frame <= frame)
    {
        var poke = options.FramePokeEvents[state.NextFramePokeEvent++];
        switch (poke.Bytes)
        {
            case 1:
                gba.Bus.Write8(poke.Address, (byte)poke.Value);
                break;
            case 2:
                gba.Bus.Write16(poke.Address, (ushort)poke.Value);
                break;
            case 4:
                gba.Bus.Write32(poke.Address, poke.Value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported poke size: {poke.Bytes}.");
        }

        if (options.Trace || options.TraceInput)
        {
            Console.WriteLine($"{step:D8} FRAME {frame:D5} POKE {poke.Address:X8}/{poke.Bytes}=0x{poke.Value:X8}");
        }
    }
}

static void InstallWatchReads(GbaSystem gba, RunOptions options, Func<int> getFrame)
{
    if (options.WatchReads.Count == 0 && options.WatchReadRanges.Count == 0)
    {
        return;
    }

    var emittedWatchLines = 0;
    bool ShouldEmitWatchLine()
    {
        if (options.WatchLimit == 0)
        {
            return true;
        }

        if (emittedWatchLines >= options.WatchLimit)
        {
            return false;
        }

        emittedWatchLines++;
        return true;
    }

    gba.Bus.AddIoReadObserver((address, bytes) =>
    {
        var frame = getFrame();
        if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
        {
            return;
        }

        if (IsWatchedRead(address, bytes, options) && ShouldEmitWatchLine())
        {
            var value = bytes <= 2 ? gba.Bus.PeekIo16(address & ~1u) : gba.Bus.PeekIo32(address & ~3u);
            Console.WriteLine($"READ {address:X8}/{bytes} value=0x{value:X8} PC=0x{gba.Cpu.Pc:X8} frame={frame:D5} cycles={gba.Scheduler.Now:N0} {TraceVideoLocation(gba)} keys={gba.Keypad.PressedKeys}");
        }
    });

    gba.Bus.AddMemoryReadObserver((address, bytes, value) =>
    {
        var frame = getFrame();
        if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
        {
            return;
        }

        if (IsWatchedRead(address, bytes, options) && ShouldEmitWatchLine())
        {
            Console.WriteLine($"MEMREAD {address:X8}/{bytes} value=0x{value:X8} PC=0x{gba.Cpu.Pc:X8} frame={frame:D5} cycles={gba.Scheduler.Now:N0} {TraceVideoLocation(gba)} keys={gba.Keypad.PressedKeys}");
        }
    });
}

static bool IsWatchedRead(uint address, int bytes, RunOptions options)
{
    foreach (var watched in options.WatchReads)
    {
        if (address <= watched && watched < address + bytes)
        {
            return true;
        }
    }

    foreach (var range in options.WatchReadRanges)
    {
        if (address <= range.End && range.Start < address + bytes)
        {
            return true;
        }
    }

    return false;
}

static void InstallWatchWrites(GbaSystem gba, RunOptions options, Func<int> getFrame)
{
    if (options.WatchWrites.Count == 0 && options.WatchWriteRanges.Count == 0)
    {
        return;
    }

    var emittedWatchLines = 0;
    bool ShouldEmitWatchLine()
    {
        if (options.WatchLimit == 0)
        {
            return true;
        }

        if (emittedWatchLines >= options.WatchLimit)
        {
            return false;
        }

        emittedWatchLines++;
        return true;
    }

    gba.Bus.AddMemoryWriteObserver((address, bytes) =>
    {
        var frame = getFrame();
        if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
        {
            return;
        }

        if (IsWatchedWrite(address, bytes, options) && ShouldEmitWatchLine())
        {
            var value = bytes switch
            {
                1 => gba.Bus.Read8(address),
                2 => gba.Bus.Read16(address & ~1u),
                _ => gba.Bus.Read32(address & ~3u)
            };
            Console.WriteLine($"WRITE {address:X8}/{bytes} value=0x{value:X8} PC=0x{gba.Cpu.Pc:X8} frame={frame:D5} cycles={gba.Scheduler.Now:N0} {TraceVideoLocation(gba)} keys={gba.Keypad.PressedKeys}");
        }
    });

    gba.Bus.AddIoWriteObserver((address, bytes) =>
    {
        var frame = getFrame();
        if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
        {
            return;
        }

        if (IsWatchedWrite(address, bytes, options) && ShouldEmitWatchLine())
        {
            var value = bytes <= 2 ? gba.Bus.PeekIo16(address & ~1u) : gba.Bus.PeekIo32(address & ~3u);
            Console.WriteLine($"IOWRITE {address:X8}/{bytes} value=0x{value:X8} PC=0x{gba.Cpu.Pc:X8} frame={frame:D5} cycles={gba.Scheduler.Now:N0} {TraceVideoLocation(gba)} keys={gba.Keypad.PressedKeys}");
        }
    });
}

static bool IsWatchedWrite(uint address, int bytes, RunOptions options)
{
    foreach (var watched in options.WatchWrites)
    {
        if (address <= watched && watched < address + bytes)
        {
            return true;
        }
    }

    foreach (var range in options.WatchWriteRanges)
    {
        if (address <= range.End && range.Start < address + bytes)
        {
            return true;
        }
    }

    return false;
}

static void InstallSwiTrace(GbaSystem gba, RunOptions options, Func<int> frameProvider)
{
    if (!options.TraceSwi)
    {
        return;
    }

    gba.Cpu.SoftwareInterruptCalled += (number, pc) =>
    {
        var frame = frameProvider();
        if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
        {
            return;
        }

        Console.WriteLine($"SWI {number:X2} frame={frame:D5} PC=0x{pc:X8} cycles={gba.Scheduler.Now:N0} line={gba.Bus.VerticalCount} videoLine={gba.Video.CurrentLine} waitV={gba.Video.CyclesUntilNextVBlankStart} dispstat=0x{gba.Bus.DisplayStatus:X4} if=0x{gba.Bus.InterruptFlags:X4} biosIf=0x{gba.Bus.BiosInterruptFlags:X4} r0=0x{gba.Cpu[0]:X8} r1=0x{gba.Cpu[1]:X8} r2=0x{gba.Cpu[2]:X8}");
    };
}

static void InstallIrqTrace(GbaSystem gba, RunOptions options, Func<int> frameProvider)
{
    if (!options.TraceIrq)
    {
        return;
    }

    var emitted = 0;
    bool FrameAllowed(int frame)
        => options.TraceFrameRange is not { } frameRange || frameRange.Contains(frame);

    bool CanEmit()
    {
        if (options.TraceIrqLimit == 0)
        {
            return true;
        }

        if (emitted >= options.TraceIrqLimit)
        {
            return false;
        }

        emitted++;
        return true;
    }

    gba.Cpu.InterruptEntered += (returnPc, handler, ie, flags, ime) =>
    {
        var frame = frameProvider();
        if (FrameAllowed(frame) && CanEmit())
        {
            Console.WriteLine($"IRQ ENTER frame={frame:D5} return=0x{returnPc:X8} handler=0x{handler:X8} IE=0x{ie:X4} IF=0x{flags:X4} IME={(ime ? 1 : 0)}");
        }
    };

    gba.Cpu.InterruptReturned += (returnPc, branchTarget, ie, flags, ime) =>
    {
        var frame = frameProvider();
        if (FrameAllowed(frame) && CanEmit())
        {
            Console.WriteLine($"IRQ RETURN frame={frame:D5} pc=0x{returnPc:X8} target=0x{branchTarget:X8} IE=0x{ie:X4} IF=0x{flags:X4} IME={(ime ? 1 : 0)}");
        }
    };

    gba.Bus.InterruptRequested += (requested, flags) =>
    {
        var frame = frameProvider();
        if (FrameAllowed(frame) && CanEmit())
        {
            Console.WriteLine($"IRQ REQUEST frame={frame:D5} requested=0x{requested:X4} IF=0x{flags:X4} IE=0x{gba.Bus.InterruptEnable:X4} IME={(gba.Bus.InterruptMasterEnable ? 1 : 0)} line={gba.Bus.VerticalCount} dispstat=0x{gba.Bus.DisplayStatus:X4} PC=0x{gba.Cpu.Pc:X8} cycles={gba.Scheduler.Now:N0}");
        }
    };
}

static void InstallDmaTrace(GbaSystem gba, RunOptions options, Func<int> frameProvider)
{
    if (!options.TraceDma)
    {
        return;
    }

    gba.Dma.TransferStarted += trace =>
    {
        var frame = frameProvider();
        if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
        {
            return;
        }

        Console.WriteLine($"DMA{trace.Channel} {trace.Timing} frame={frame:D5} {TraceVideoLocation(gba)} src=0x{trace.Source:X8} dst=0x{trace.Destination:X8} count={trace.Count} width={(trace.WordTransfer ? 32 : 16)} ctrl=0x{trace.Control:X4} fifoA={trace.FifoALevel} fifoB={trace.FifoBLevel}");
    };
}

static string TraceVideoLocation(GbaSystem gba)
    => $"line={gba.Bus.VerticalCount} videoLine={gba.Video.CurrentLine} dispstat=0x{gba.Bus.DisplayStatus:X4}";

static void InstallEepromTrace(GbaSystem gba, RunOptions options, Func<int> frameProvider)
{
    if (!options.TraceEeprom)
    {
        return;
    }

    var emitted = 0;
    gba.Bus.EepromAccessed += trace =>
    {
        var frame = frameProvider();
        if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
        {
            return;
        }

        if (options.TraceEepromLimit != 0 && emitted >= options.TraceEepromLimit)
        {
            return;
        }

        emitted++;
        Console.WriteLine($"EEPROM {trace.Operation} frame={frame:D5} address=0x{trace.Address:X4} bits={trace.AddressBits} data=0x{trace.Data:X16} pending={trace.PendingBits}");
    };
}

static MemoryDump ParseMemoryDump(string value)
{
    var separator = value.IndexOf(':');
    if (separator <= 0 || separator == value.Length - 1)
    {
        throw new ArgumentException($"Invalid memory dump '{value}'. Expected ADDRESS:LENGTH.");
    }

    return new MemoryDump(ParseAddress(value[..separator]), ParseAddress(value[(separator + 1)..]));
}

static InstructionDump ParseInstructionDump(string value)
{
    var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length is not (2 or 3))
    {
        throw new ArgumentException($"Invalid instruction dump '{value}'. Expected ADDRESS:LENGTH[:arm|thumb].");
    }

    var mode = parts.Length == 3
        ? parts[2].ToLowerInvariant() switch
        {
            "arm" => InstructionSet.Arm,
            "thumb" => InstructionSet.Thumb,
            _ => throw new ArgumentException($"Invalid instruction dump mode '{parts[2]}'. Expected arm or thumb.")
        }
        : InstructionSet.Arm;
    return new InstructionDump(ParseAddress(parts[0]), ParseAddress(parts[1]), mode);
}

static AddressRange ParseAddressRange(string value)
{
    var separator = value.IndexOf(':');
    if (separator <= 0 || separator == value.Length - 1)
    {
        throw new ArgumentException($"Invalid address range '{value}'. Expected START:END.");
    }

    var start = ParseAddress(value[..separator]);
    var end = ParseAddress(value[(separator + 1)..]);
    if (end < start)
    {
        throw new ArgumentException($"Invalid address range '{value}'. END must be greater than or equal to START.");
    }

    return new AddressRange(start, end);
}

static void DumpMemoryIfRequested(GbaSystem gba, RunOptions options)
{
    foreach (var dump in options.Dumps)
    {
        Console.WriteLine($"MEMORY {dump.Address:X8}:{dump.Length:X}");
        for (var offset = 0u; offset < dump.Length; offset += 16)
        {
            var count = Math.Min(16u, dump.Length - offset);
            Console.Write($"{dump.Address + offset:X8}:");
            for (var i = 0u; i < count; i++)
            {
                Console.Write($" {gba.Bus.Read8(dump.Address + offset + i):X2}");
            }

            Console.WriteLine();
        }
    }

    foreach (var dump in options.InstructionDumps)
    {
        if (dump.Set == InstructionSet.Thumb)
        {
            DumpThumbDisassembly(gba, dump);
        }
        else
        {
            DumpArmDisassembly(gba, dump);
        }
    }
}

static void DumpArmDisassembly(GbaSystem gba, InstructionDump dump)
{
    Console.WriteLine($"DISASSEMBLY ARM {dump.Address:X8}:{dump.Length:X}");
    var alignedAddress = dump.Address & ~3u;
    var end = dump.Address + dump.Length;
    for (var address = alignedAddress; address < end; address += 4)
    {
        var instruction = gba.Bus.Read32(address);
        Console.WriteLine($"{address:X8}: {instruction:X8}  {DisassembleArm(instruction, address)}");
    }
}

static void DumpThumbDisassembly(GbaSystem gba, InstructionDump dump)
{
    Console.WriteLine($"DISASSEMBLY THUMB {dump.Address:X8}:{dump.Length:X}");
    var alignedAddress = dump.Address & ~1u;
    var end = dump.Address + dump.Length;
    uint? pendingBlHighAddress = null;
    int pendingBlHighOffset = 0;
    for (var address = alignedAddress; address < end; address += 2)
    {
        var instruction = gba.Bus.Read16(address);
        var disassembly = DisassembleThumb(instruction, address, pendingBlHighAddress, pendingBlHighOffset, out var nextBlHighAddress, out var nextBlHighOffset);
        Console.WriteLine($"{address:X8}: {instruction:X4}      {disassembly}");
        pendingBlHighAddress = nextBlHighAddress;
        pendingBlHighOffset = nextBlHighOffset;
    }
}

static string DisassembleArm(uint instruction, uint address)
{
    var condition = ArmConditionName((int)(instruction >> 28));
    var suffix = condition == "AL" ? "" : condition.ToLowerInvariant();

    if ((instruction & 0x0FFF_FFF0) == 0x012F_FF10)
    {
        return $"bx{suffix} r{instruction & 0xF}";
    }

    if ((instruction & 0x0F00_0000) == 0x0F00_0000)
    {
        return $"swi{suffix} 0x{(instruction & 0x00FF_FFFF):X6}";
    }

    if ((instruction & 0x0E00_0000) == 0x0A00_0000)
    {
        var link = (instruction & (1u << 24)) != 0 ? "l" : "";
        var signedOffset = SignExtend24ForDisassembly(instruction & 0x00FF_FFFF) << 2;
        var target = unchecked((uint)((int)address + 8 + signedOffset));
        return $"b{link}{suffix} 0x{target:X8}";
    }

    if ((instruction & 0x0FC0_00F0) == 0x0000_0090)
    {
        var accumulate = (instruction & (1u << 21)) != 0;
        var setFlags = (instruction & (1u << 20)) != 0 ? "s" : "";
        var rd = (instruction >> 16) & 0xF;
        var rn = (instruction >> 12) & 0xF;
        var rs = (instruction >> 8) & 0xF;
        var rm = instruction & 0xF;
        return accumulate
            ? $"mla{suffix}{setFlags} r{rd}, r{rm}, r{rs}, r{rn}"
            : $"mul{suffix}{setFlags} r{rd}, r{rm}, r{rs}";
    }

    if ((instruction & 0x0E00_0090) == 0x0000_0090)
    {
        return DisassembleArmHalfwordTransfer(instruction, suffix);
    }

    if ((instruction & 0x0C00_0000) == 0x0400_0000)
    {
        return DisassembleArmSingleDataTransfer(instruction, suffix);
    }

    if ((instruction & 0x0E00_0000) == 0x0800_0000)
    {
        return DisassembleArmBlockTransfer(instruction, suffix);
    }

    if ((instruction & 0x0C00_0000) == 0)
    {
        return DisassembleArmDataProcessing(instruction, suffix);
    }

    return "unknown";
}

static string DisassembleThumb(ushort instruction, uint address, uint? pendingBlHighAddress, int pendingBlHighOffset, out uint? nextBlHighAddress, out int nextBlHighOffset)
{
    nextBlHighAddress = null;
    nextBlHighOffset = 0;

    if ((instruction & 0xF800) == 0x2000)
    {
        return $"mov r{(instruction >> 8) & 0x7}, #0x{instruction & 0xFF:X}";
    }

    if ((instruction & 0xF800) == 0x2800)
    {
        return $"cmp r{(instruction >> 8) & 0x7}, #0x{instruction & 0xFF:X}";
    }

    if ((instruction & 0xF800) == 0x3000)
    {
        return $"add r{(instruction >> 8) & 0x7}, #0x{instruction & 0xFF:X}";
    }

    if ((instruction & 0xF800) == 0x3800)
    {
        return $"sub r{(instruction >> 8) & 0x7}, #0x{instruction & 0xFF:X}";
    }

    if ((instruction & 0xF800) is 0x1800 or 0x1A00)
    {
        return DisassembleThumbAddSubtract(instruction);
    }

    if ((instruction & 0xE000) == 0x0000)
    {
        return DisassembleThumbShift(instruction);
    }

    if ((instruction & 0xFC00) == 0x4000)
    {
        return DisassembleThumbAlu(instruction);
    }

    if ((instruction & 0xFC00) == 0x4400)
    {
        return DisassembleThumbHighRegisterOrBranchExchange(instruction);
    }

    if ((instruction & 0xF200) == 0x5200)
    {
        return DisassembleThumbLoadStoreSignExtended(instruction);
    }

    if ((instruction & 0xF000) == 0x5000)
    {
        return DisassembleThumbLoadStoreRegisterOffset(instruction);
    }

    if ((instruction & 0xE000) == 0x6000 || (instruction & 0xF000) == 0x8000)
    {
        return DisassembleThumbLoadStoreImmediateOffset(instruction);
    }

    if ((instruction & 0xF800) == 0x4800)
    {
        var rd = (instruction >> 8) & 0x7;
        var target = ((address + 4) & ~2u) + (uint)((instruction & 0xFF) << 2);
        return $"ldr r{rd}, [pc, #0x{(instruction & 0xFF) << 2:X}] ; 0x{target:X8}";
    }

    if ((instruction & 0xF000) == 0x9000)
    {
        var load = (instruction & (1 << 11)) != 0;
        var rd = (instruction >> 8) & 0x7;
        return $"{(load ? "ldr" : "str")} r{rd}, [sp, #0x{(instruction & 0xFF) << 2:X}]";
    }

    if ((instruction & 0xF000) == 0xA000)
    {
        var rd = (instruction >> 8) & 0x7;
        var source = (instruction & (1 << 11)) == 0 ? "pc" : "sp";
        return $"add r{rd}, {source}, #0x{(instruction & 0xFF) << 2:X}";
    }

    if ((instruction & 0xFF00) == 0xB000)
    {
        var sign = (instruction & (1 << 7)) == 0 ? "" : "-";
        return $"add sp, {sign}#0x{(instruction & 0x7F) << 2:X}";
    }

    if ((instruction & 0xF600) == 0xB400)
    {
        return DisassembleThumbPushPop(instruction);
    }

    if ((instruction & 0xF000) == 0xC000)
    {
        var load = (instruction & (1 << 11)) != 0;
        var rb = (instruction >> 8) & 0x7;
        return $"{(load ? "ldmia" : "stmia")} r{rb}!, {{{FormatRegisterList((uint)(instruction & 0xFF))}}}";
    }

    if ((instruction & 0xF000) == 0xD000 && (instruction & 0x0F00) != 0x0F00)
    {
        var condition = ThumbConditionName((instruction >> 8) & 0xF);
        var target = unchecked((uint)((int)address + 4 + ((sbyte)(instruction & 0xFF) * 2)));
        return $"b{condition} 0x{target:X8}";
    }

    if ((instruction & 0xFF00) == 0xDF00)
    {
        return $"swi 0x{instruction & 0xFF:X2}";
    }

    if ((instruction & 0xFF87) == 0x4700)
    {
        return $"bx r{(instruction >> 3) & 0xF}";
    }

    if ((instruction & 0xF800) == 0xE000)
    {
        var target = unchecked((uint)((int)address + 4 + (SignExtend11ForDisassembly((uint)(instruction & 0x7FF)) * 2)));
        return $"b 0x{target:X8}";
    }

    if ((instruction & 0xF800) == 0xF000)
    {
        var high = SignExtend11ForDisassembly((uint)(instruction & 0x7FF)) << 12;
        nextBlHighAddress = address;
        nextBlHighOffset = high;
        return $"bl-hi #0x{high:X}";
    }

    if ((instruction & 0xF800) == 0xF800)
    {
        var low = (int)((instruction & 0x7FF) << 1);
        if (pendingBlHighAddress == address - 2)
        {
            var target = unchecked((uint)((int)address + 2 + pendingBlHighOffset + low));
            return $"bl 0x{target:X8}";
        }

        return $"bl-lo #0x{low:X}";
    }

    return "unknown";
}

static string DisassembleThumbShift(ushort instruction)
{
    var op = (instruction >> 11) & 0x3;
    var offset = (instruction >> 6) & 0x1F;
    var rs = (instruction >> 3) & 0x7;
    var rd = instruction & 0x7;
    var mnemonic = op switch
    {
        0 => "lsl",
        1 => "lsr",
        2 => "asr",
        _ => "shift"
    };
    return $"{mnemonic} r{rd}, r{rs}, #0x{offset:X}";
}

static string DisassembleThumbAddSubtract(ushort instruction)
{
    var immediate = (instruction & (1 << 10)) != 0;
    var subtract = (instruction & (1 << 9)) != 0;
    var operand = immediate ? $"#0x{(instruction >> 6) & 0x7:X}" : $"r{(instruction >> 6) & 0x7}";
    var rs = (instruction >> 3) & 0x7;
    var rd = instruction & 0x7;
    return $"{(subtract ? "sub" : "add")} r{rd}, r{rs}, {operand}";
}

static string DisassembleThumbAlu(ushort instruction)
{
    var op = (instruction >> 6) & 0xF;
    var rs = (instruction >> 3) & 0x7;
    var rd = instruction & 0x7;
    var mnemonic = op switch
    {
        0x0 => "and",
        0x1 => "eor",
        0x2 => "lsl",
        0x3 => "lsr",
        0x4 => "asr",
        0x5 => "adc",
        0x6 => "sbc",
        0x7 => "ror",
        0x8 => "tst",
        0x9 => "neg",
        0xA => "cmp",
        0xB => "cmn",
        0xC => "orr",
        0xD => "mul",
        0xE => "bic",
        0xF => "mvn",
        _ => "alu"
    };

    return op switch
    {
        0x8 or 0xA or 0xB => $"{mnemonic} r{rd}, r{rs}",
        0x9 or 0xF => $"{mnemonic} r{rd}, r{rs}",
        _ => $"{mnemonic} r{rd}, r{rs}"
    };
}

static string DisassembleThumbHighRegisterOrBranchExchange(ushort instruction)
{
    var op = (instruction >> 8) & 0x3;
    var highDestination = (instruction & (1 << 7)) != 0;
    var highSource = (instruction & (1 << 6)) != 0;
    var rs = ((instruction >> 3) & 0x7) | (highSource ? 8 : 0);
    var rd = (instruction & 0x7) | (highDestination ? 8 : 0);
    return op switch
    {
        0 => $"add r{rd}, r{rs}",
        1 => $"cmp r{rd}, r{rs}",
        2 => $"mov r{rd}, r{rs}",
        3 => $"bx r{rs}",
        _ => "hi"
    };
}

static string DisassembleThumbLoadStoreRegisterOffset(ushort instruction)
{
    var load = (instruction & (1 << 11)) != 0;
    var byteTransfer = (instruction & (1 << 10)) != 0;
    var ro = (instruction >> 6) & 0x7;
    var rb = (instruction >> 3) & 0x7;
    var rd = instruction & 0x7;
    return $"{(load ? "ldr" : "str")}{(byteTransfer ? "b" : "")} r{rd}, [r{rb}, r{ro}]";
}

static string DisassembleThumbLoadStoreSignExtended(ushort instruction)
{
    var op = (instruction >> 10) & 0x3;
    var ro = (instruction >> 6) & 0x7;
    var rb = (instruction >> 3) & 0x7;
    var rd = instruction & 0x7;
    var mnemonic = op switch
    {
        0 => "strh",
        1 => "ldrsb",
        2 => "ldrh",
        3 => "ldrsh",
        _ => "lsse"
    };
    return $"{mnemonic} r{rd}, [r{rb}, r{ro}]";
}

static string DisassembleThumbLoadStoreImmediateOffset(ushort instruction)
{
    var halfwordTransfer = (instruction & 0xF000) == 0x8000;
    var load = (instruction & (1 << 11)) != 0;
    var byteTransfer = !halfwordTransfer && (instruction & (1 << 12)) != 0;
    var offset5 = (instruction >> 6) & 0x1F;
    var rb = (instruction >> 3) & 0x7;
    var rd = instruction & 0x7;
    var scale = halfwordTransfer ? 1 : byteTransfer ? 0 : 2;
    var mnemonic = halfwordTransfer ? (load ? "ldrh" : "strh") : $"{(load ? "ldr" : "str")}{(byteTransfer ? "b" : "")}";
    return $"{mnemonic} r{rd}, [r{rb}, #0x{offset5 << scale:X}]";
}

static string DisassembleThumbPushPop(ushort instruction)
{
    var pop = (instruction & (1 << 11)) != 0;
    var includeSpecial = (instruction & (1 << 8)) != 0;
    var registerMask = (uint)(instruction & 0xFF);
    if (includeSpecial)
    {
        registerMask |= pop ? 1u << 15 : 1u << 14;
    }

    return $"{(pop ? "pop" : "push")} {{{FormatRegisterList(registerMask)}}}";
}

static string DisassembleArmDataProcessing(uint instruction, string suffix)
{
    var op = (instruction >> 21) & 0xF;
    var setFlags = (instruction & (1u << 20)) != 0;
    var rn = (instruction >> 16) & 0xF;
    var rd = (instruction >> 12) & 0xF;
    var operand = DisassembleArmOperand2(instruction);
    var mnemonic = op switch
    {
        0x0 => "and",
        0x1 => "eor",
        0x2 => "sub",
        0x3 => "rsb",
        0x4 => "add",
        0x5 => "adc",
        0x6 => "sbc",
        0x7 => "rsc",
        0x8 => "tst",
        0x9 => "teq",
        0xA => "cmp",
        0xB => "cmn",
        0xC => "orr",
        0xD => "mov",
        0xE => "bic",
        0xF => "mvn",
        _ => "dp"
    };

    var s = setFlags && op is not (0x8 or 0x9 or 0xA or 0xB) ? "s" : "";
    return op switch
    {
        0x8 or 0x9 or 0xA or 0xB => $"{mnemonic}{suffix} r{rn}, {operand}",
        0xD or 0xF => $"{mnemonic}{suffix}{s} r{rd}, {operand}",
        _ => $"{mnemonic}{suffix}{s} r{rd}, r{rn}, {operand}"
    };
}

static string DisassembleArmSingleDataTransfer(uint instruction, string suffix)
{
    var immediate = (instruction & (1u << 25)) != 0;
    var pre = (instruction & (1u << 24)) != 0;
    var up = (instruction & (1u << 23)) != 0;
    var byteTransfer = (instruction & (1u << 22)) != 0;
    var writeback = (instruction & (1u << 21)) != 0;
    var load = (instruction & (1u << 20)) != 0;
    var rn = (instruction >> 16) & 0xF;
    var rd = (instruction >> 12) & 0xF;
    var operand = immediate ? DisassembleArmOperand2(instruction) : $"#0x{(instruction & 0xFFF):X}";
    var sign = up ? "" : "-";
    var address = pre
        ? $"[r{rn}, {sign}{operand}]{(writeback ? "!" : "")}"
        : $"[r{rn}], {sign}{operand}";
    return $"{(load ? "ldr" : "str")}{suffix}{(byteTransfer ? "b" : "")} r{rd}, {address}";
}

static string DisassembleArmHalfwordTransfer(uint instruction, string suffix)
{
    var pre = (instruction & (1u << 24)) != 0;
    var up = (instruction & (1u << 23)) != 0;
    var immediate = (instruction & (1u << 22)) != 0;
    var writeback = (instruction & (1u << 21)) != 0;
    var load = (instruction & (1u << 20)) != 0;
    var rn = (instruction >> 16) & 0xF;
    var rd = (instruction >> 12) & 0xF;
    var transferKind = (instruction >> 5) & 0x3;
    var mnemonic = transferKind switch
    {
        0x1 => load ? "ldrh" : "strh",
        0x2 => "ldrsb",
        0x3 => "ldrsh",
        _ => "half"
    };

    var offset = immediate
        ? $"#0x{(((instruction >> 4) & 0xF0) | (instruction & 0xF)):X}"
        : $"r{instruction & 0xF}";
    var sign = up ? "" : "-";
    var address = pre
        ? $"[r{rn}, {sign}{offset}]{(writeback ? "!" : "")}"
        : $"[r{rn}], {sign}{offset}";
    return $"{mnemonic}{suffix} r{rd}, {address}";
}

static string DisassembleArmBlockTransfer(uint instruction, string suffix)
{
    var pre = (instruction & (1u << 24)) != 0;
    var up = (instruction & (1u << 23)) != 0;
    var psr = (instruction & (1u << 22)) != 0 ? "^" : "";
    var writeback = (instruction & (1u << 21)) != 0 ? "!" : "";
    var load = (instruction & (1u << 20)) != 0;
    var rn = (instruction >> 16) & 0xF;
    var mode = (pre, up) switch
    {
        (false, false) => "da",
        (true, false) => "db",
        (false, true) => "ia",
        (true, true) => "ib"
    };

    return $"{(load ? "ldm" : "stm")}{suffix}{mode} r{rn}{writeback}, {{{FormatRegisterList(instruction & 0xFFFF)}}}{psr}";
}

static string DisassembleArmOperand2(uint instruction)
{
    if ((instruction & (1u << 25)) != 0)
    {
        var immediate = instruction & 0xFF;
        var rotate = (int)((instruction >> 8) & 0xF) * 2;
        var value = RotateRight(immediate, rotate);
        return $"#0x{value:X}";
    }

    var rm = instruction & 0xF;
    if ((instruction & 0xFF0) == 0)
    {
        return $"r{rm}";
    }

    var shiftByRegister = (instruction & (1u << 4)) != 0;
    var shiftType = ((instruction >> 5) & 0x3) switch
    {
        0 => "lsl",
        1 => "lsr",
        2 => "asr",
        _ => "ror"
    };

    if (shiftByRegister)
    {
        var rs = (instruction >> 8) & 0xF;
        return $"r{rm}, {shiftType} r{rs}";
    }

    var amount = (instruction >> 7) & 0x1F;
    return $"r{rm}, {shiftType} #{amount}";
}

static string FormatRegisterList(uint mask)
{
    var registers = new List<string>();
    for (var i = 0; i < 16; i++)
    {
        if ((mask & (1u << i)) != 0)
        {
            registers.Add($"r{i}");
        }
    }

    return string.Join(", ", registers);
}

static uint RotateRight(uint value, int amount)
{
    amount &= 31;
    return amount == 0 ? value : (value >> amount) | (value << (32 - amount));
}

static int SignExtend24ForDisassembly(uint value)
{
    value &= 0x00FF_FFFF;
    return (value & 0x0080_0000) != 0 ? unchecked((int)(value | 0xFF00_0000)) : (int)value;
}

static int SignExtend11ForDisassembly(uint value)
{
    value &= 0x7FF;
    return (value & 0x400) != 0 ? unchecked((int)(value | 0xFFFF_F800)) : (int)value;
}

static string ArmConditionName(int condition) => condition switch
{
    0x0 => "EQ",
    0x1 => "NE",
    0x2 => "CS",
    0x3 => "CC",
    0x4 => "MI",
    0x5 => "PL",
    0x6 => "VS",
    0x7 => "VC",
    0x8 => "HI",
    0x9 => "LS",
    0xA => "GE",
    0xB => "LT",
    0xC => "GT",
    0xD => "LE",
    0xE => "AL",
    _ => "NV"
};

static string ThumbConditionName(int condition) => condition switch
{
    0x0 => "eq",
    0x1 => "ne",
    0x2 => "cs",
    0x3 => "cc",
    0x4 => "mi",
    0x5 => "pl",
    0x6 => "vs",
    0x7 => "vc",
    0x8 => "hi",
    0x9 => "ls",
    0xA => "ge",
    0xB => "lt",
    0xC => "gt",
    0xD => "le",
    _ => ""
};

static void ApplyFrameHashEvents(GbaSystem gba, RunOptions options, InputEventState state, long step, int frame)
{
    if (options.FrameHashEvents.Count == 0 || frame == 0 || frame == state.LastHashFrame)
    {
        return;
    }

    state.LastHashFrame = frame;
    var hash = HashFramebuffer(gba.Video.Framebuffer);
    for (var i = 0; i < options.FrameHashEvents.Count; i++)
    {
        if (state.FiredHashEvents.Contains(i) || options.FrameHashEvents[i].Hash != hash || frame < options.FrameHashEvents[i].MinFrame)
        {
            continue;
        }

        var keyEvent = options.FrameHashEvents[i];
        state.FiredHashEvents.Add(i);
        state.HashReleaseFrame = frame + keyEvent.DurationFrames;
        gba.Keypad.SetPressedKeys(keyEvent.Keys);
        if (options.Trace || options.TraceInput)
        {
            Console.WriteLine($"{step:D8} FRAME {frame:D5} HASH 0x{hash:X16} KEYS {keyEvent.Keys} duration={keyEvent.DurationFrames}");
        }

        return;
    }
}

static void ApplyMemoryTriggerEvents(GbaSystem gba, RunOptions options, InputEventState state, long step, int frame)
{
    if (options.MemoryTriggerEvents.Count == 0 || frame == 0 || frame == state.LastMemoryTriggerFrame)
    {
        return;
    }

    state.LastMemoryTriggerFrame = frame;
    for (var i = 0; i < options.MemoryTriggerEvents.Count; i++)
    {
        if (state.FiredMemoryTriggerEvents.Contains(i))
        {
            continue;
        }

        var trigger = options.MemoryTriggerEvents[i];
        if (frame < trigger.MinFrame || ReadMemoryTriggerValue(gba, trigger.Address, trigger.Bytes) != trigger.Value)
        {
            continue;
        }

        state.FiredMemoryTriggerEvents.Add(i);
        state.HashReleaseFrame = frame + trigger.DurationFrames;
        gba.Keypad.SetPressedKeys(trigger.Keys);
        if (options.Trace || options.TraceInput)
        {
            Console.WriteLine($"{step:D8} FRAME {frame:D5} MEMORY {trigger.Address:X8}/{trigger.Bytes}=0x{trigger.Value:X8} KEYS {trigger.Keys} duration={trigger.DurationFrames}");
        }

        return;
    }
}

static uint ReadMemoryTriggerValue(GbaSystem gba, uint address, int bytes)
    => bytes switch
    {
        1 => gba.Bus.Read8(address),
        2 => gba.Bus.Read16(address),
        4 => gba.Bus.Read32(address),
        _ => throw new ArgumentOutOfRangeException(nameof(bytes))
    };

static FrameRange ParseFrameRange(string value)
{
    var separator = value.IndexOf(':');
    if (separator <= 0 || separator == value.Length - 1)
    {
        throw new ArgumentException($"Invalid frame range '{value}'. Expected START:END.");
    }

    if (!int.TryParse(value[..separator], out var start) || !int.TryParse(value[(separator + 1)..], out var end) || start < 0 || end < start)
    {
        throw new ArgumentException($"Invalid frame range '{value}'. START and END must be non-negative and ordered.");
    }

    return new FrameRange(start, end);
}

static bool ShouldSampleFrame(int frame, int frameInterval, FrameRange? frameRange, ref int lastSampledFrame)
{
    if (frame == lastSampledFrame || frame == 0 || frame % frameInterval != 0)
    {
        return false;
    }

    if (frameRange is { } range && !range.Contains(frame))
    {
        return false;
    }

    lastSampledFrame = frame;
    return true;
}

static SnapshotWriter? OpenSnapshotWriter(RunOptions options)
{
    if (options.SnapshotCsv is not { Length: > 0 } path)
    {
        return null;
    }

    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var writer = new StreamWriter(fullPath, append: false, System.Text.Encoding.ASCII);
    writer.WriteLine("frame,cycles,pc,cpsr,thumb,keyInput,pressedKeys,mainCallback,sonicState1100,sonicState1140,sonicState1144,sonicState1148,sonicState1150,sonicKey98,sonicKey9A,sonicKey9C,sonicKey9E,sonicKeyA0,rubyTaskCallback,rubyTask28,rubyTask2A,rubyTask2E,rubyTask30,rubyBgmStatus,rubySaveX,rubySaveY,rubySaveMapGroup,rubySaveMapNum,rubyVarLittlerootState,rubyVarRoute101State,rubyVarLittlerootHousesState,rubyVarLittlerootHousesState2,rubyVarLittlerootRivalState,rubyVarLittlerootIntroState,taskSlots,firstTask,rubyMovingNpcId,rubyMovingNpcMapGroup,rubyMovingNpcMapNum,rubyMovementTaskId,rubyMovementTaskFlags,rubyMovementObjects,rubyPlayerObject,rubyObjectEvents,dispcnt,dispstat,vcount,ie,if,ime,bg0cnt,bg1cnt,bg2cnt,bg3cnt,bg0hofs,bg0vofs,bg1hofs,bg1vofs,bg2hofs,bg2vofs,bg3hofs,bg3vofs,bg2pa,bg2pb,bg2pc,bg2pd,bg2x,bg2y,bg3pa,bg3pb,bg3pc,bg3pd,bg3x,bg3y,win0h,win1h,win0v,win1v,winin,winout,mosaic,bldcnt,bldalpha,bldy,soundcntL,soundcntH,soundcntX,dma0cnt,dma1cnt,dma2cnt,dma3cnt,activeObjects,hiddenObjects,firstActiveObject");
    return new SnapshotWriter(writer);
}

static PcSnapshotWriter? OpenPcSnapshotWriter(RunOptions options)
{
    if (options.PcSnapshotCsv is not { Length: > 0 } path)
    {
        return null;
    }

    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var writer = new StreamWriter(fullPath, append: false, System.Text.Encoding.ASCII);
    writer.Write("hit,step,frame,cycles,line,pc,cpsr,thumb,mode,instruction,r0,r1,r2,r3,r4,r5,r6,r7,r8,r9,r10,r11,r12,sp,lr,keyInput,pressedKeys,dispcnt,dispstat,vcount,ie,if,ime,biosIf");
    for (var word = 0; word < options.PcSnapshotStackWords; word++)
    {
        writer.Write($",sp{word * 4:X2}");
    }

    writer.WriteLine(",iwram7e10,iwram7e14,iwram7e18,iwram7e1c,iwram7e20,iwram7e24,iwram7e28,iwram7e2c,iwram7e30,iwram7e34");
    return new PcSnapshotWriter(writer);
}

static AudioSampleWriter? OpenAudioSampleWriter(RunOptions options, GbaSystem gba)
{
    if (options.AudioCsv is not { Length: > 0 } path)
    {
        return null;
    }

    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    gba.Audio.CaptureSamples = true;
    var writer = new StreamWriter(fullPath, append: false, System.Text.Encoding.ASCII);
    writer.WriteLine("step,frame,cycle,index,fifo,timer,raw,left,right");
    return new AudioSampleWriter(writer);
}

static void WriteAudioSamplesIfNeeded(GbaSystem gba, AudioSampleWriter? samples, long step, int frame)
{
    if (samples is null)
    {
        return;
    }

    var index = 0;
    foreach (var sample in gba.Audio.DrainSamples())
    {
        samples.Writer.Write(step);
        samples.Writer.Write(',');
        samples.Writer.Write(frame);
        samples.Writer.Write(',');
        samples.Writer.Write(sample.Cycle);
        samples.Writer.Write(',');
        samples.Writer.Write(index++);
        samples.Writer.Write(',');
        samples.Writer.Write(sample.Fifo);
        samples.Writer.Write(',');
        samples.Writer.Write(sample.Timer);
        samples.Writer.Write(',');
        samples.Writer.Write(sample.RawSample);
        samples.Writer.Write(',');
        samples.Writer.Write(sample.Left);
        samples.Writer.Write(',');
        samples.Writer.WriteLine(sample.Right);
        samples.Count++;
    }
}

static PsgSampleWriter? OpenPsgSampleWriter(RunOptions options, GbaSystem gba)
{
    if (options.PsgCsv is not { Length: > 0 } path)
    {
        return null;
    }

    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    gba.Audio.CapturePsgSamples = true;
    var writer = new StreamWriter(fullPath, append: false, System.Text.Encoding.ASCII);
    writer.WriteLine("step,frame,cycle,index,left,right");
    return new PsgSampleWriter(writer);
}

static AudioWavWriter? OpenAudioWavWriter(RunOptions options, GbaSystem gba)
{
    if (options.AudioWav is not { Length: > 0 } path)
    {
        return null;
    }

    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    return new AudioWavWriter(fullPath, options.AudioSampleRate, options.AudioGain, gba);
}

static void WritePsgSamplesIfNeeded(GbaSystem gba, PsgSampleWriter? samples, long step, int frame)
{
    if (samples is null)
    {
        return;
    }

    var index = 0;
    foreach (var sample in gba.Audio.DrainPsgSamples())
    {
        samples.Writer.Write(step);
        samples.Writer.Write(',');
        samples.Writer.Write(frame);
        samples.Writer.Write(',');
        samples.Writer.Write(sample.Cycle);
        samples.Writer.Write(',');
        samples.Writer.Write(index++);
        samples.Writer.Write(',');
        samples.Writer.Write(sample.Left);
        samples.Writer.Write(',');
        samples.Writer.WriteLine(sample.Right);
        samples.Count++;
    }
}

static void WriteSnapshotIfNeeded(GbaSystem gba, RunOptions options, SnapshotWriter? snapshots, int frame)
{
    if (snapshots is null || frame == 0 || frame == snapshots.LastFrame || frame % options.SnapshotFrames != 0)
    {
        return;
    }

    if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
    {
        return;
    }

    snapshots.LastFrame = frame;
    snapshots.Writer.Write(frame);
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(gba.Scheduler.Now);
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Cpu.Pc);
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Cpu.Cpsr);
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(gba.Cpu.ThumbState ? 1 : 0);
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.KEYINPUT));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(FormatCsvToken(gba.Keypad.PressedKeys.ToString()));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.Read32(0x03002034));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.Read32(0x03001100));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x03001140));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.Read32(0x03001144));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.Read32(0x03001148));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x03001150));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x03001798));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x0300179A));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x0300179C));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x0300179E));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x030017A0));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.Read32(0x03004B20));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x03004B28));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x03004B2A));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x03004B2E));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x03004B30));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.Read32(0x03007DEC));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(ReadS16(gba.Bus, 0x02025734));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(ReadS16(gba.Bus, 0x02025736));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(unchecked((sbyte)gba.Bus.Read8(0x02025738)).ToString(CultureInfo.InvariantCulture));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(unchecked((sbyte)gba.Bus.Read8(0x02025739)).ToString(CultureInfo.InvariantCulture));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, ReadRubyVar(gba.Bus, 0x4050));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, ReadRubyVar(gba.Bus, 0x4060));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, ReadRubyVar(gba.Bus, 0x4082));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, ReadRubyVar(gba.Bus, 0x408C));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, ReadRubyVar(gba.Bus, 0x408D));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, ReadRubyVar(gba.Bus, 0x4092));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(CountTaskSlots(gba.Bus, 0x03004B20, 16));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(SummarizeFirstTask(gba.Bus, 0x03004B20, 16));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x0202E8B6));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x0202E8B8));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.Read16(0x0202E8BA));
    snapshots.Writer.Write(',');
    var rubyMovementTaskId = FindTaskIdByFunc(gba.Bus, 0x03004B20, 16, 0x080A244D);
    snapshots.Writer.Write(rubyMovementTaskId >= 0 ? rubyMovementTaskId.ToString(CultureInfo.InvariantCulture) : "none");
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, rubyMovementTaskId >= 0 ? gba.Bus.Read16((uint)(0x03004B20 + (rubyMovementTaskId * 0x28) + 0x08)) : (ushort)0);
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(FormatCsvToken(rubyMovementTaskId >= 0 ? SummarizeRubyMovementTask(gba.Bus, 0x03004B20, rubyMovementTaskId) : "none"));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(FormatCsvToken(SummarizeRubyPlayerObject(gba.Bus, 0x030048AC, 16)));
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(FormatCsvToken(SummarizeRubyObjectEvents(gba.Bus, 0x030048AC, 16)));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.DisplayControl);
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.DisplayStatus);
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(gba.Bus.VerticalCount);
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.InterruptEnable);
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.InterruptFlags);
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(gba.Bus.InterruptMasterEnable ? 1 : 0);
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG0CNT));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG1CNT));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG2CNT));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG3CNT));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG0HOFS));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG0VOFS));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG1HOFS));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG1VOFS));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG2HOFS));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG2VOFS));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG3HOFS));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG3VOFS));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG2PA));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG2PB));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG2PC));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG2PD));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.PeekIo32(IoRegisters.BG2X));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.PeekIo32(IoRegisters.BG2Y));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG3PA));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG3PB));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG3PC));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BG3PD));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.PeekIo32(IoRegisters.BG3X));
    snapshots.Writer.Write(',');
    WriteHex32(snapshots.Writer, gba.Bus.PeekIo32(IoRegisters.BG3Y));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.WIN0H));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.WIN1H));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.WIN0V));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.WIN1V));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.WININ));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.WINOUT));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.MOSAIC));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BLDCNT));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BLDALPHA));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.BLDY));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.SOUNDCNT_L));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.SOUNDCNT_H));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.SOUNDCNT_X));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.DMA0CNT_H));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.DMA1CNT_H));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.DMA2CNT_H));
    snapshots.Writer.Write(',');
    WriteHex16(snapshots.Writer, gba.Bus.PeekIo16(IoRegisters.DMA3CNT_H));
    snapshots.Writer.Write(',');
    var oamSummary = SummarizeObjects(gba.Bus.ObjectAttributeMemory);
    snapshots.Writer.Write(oamSummary.Active);
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(oamSummary.Hidden);
    snapshots.Writer.Write(',');
    snapshots.Writer.Write(oamSummary.FirstActive);
    snapshots.Writer.WriteLine();
}

static void WriteHex16(TextWriter writer, ushort value) => writer.Write($"0x{value:X4}");

static void WriteHex32(TextWriter writer, uint value) => writer.Write($"0x{value:X8}");

static void WriteHex32OrBlank(TextWriter writer, uint? value)
{
    if (value is { } present)
    {
        WriteHex32(writer, present);
    }
}

static uint? TryRead32(MemoryBus bus, uint address)
{
    try
    {
        return bus.Read32(address);
    }
    catch (ArgumentOutOfRangeException)
    {
        return null;
    }
    catch (InvalidOperationException)
    {
        return null;
    }
}

static string FormatCsvToken(string value)
    => value.Replace(", ", "+", StringComparison.Ordinal).Replace(",", "+", StringComparison.Ordinal);

static int CountTaskSlots(MemoryBus bus, uint tableAddress, int slots)
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

static string SummarizeFirstTask(MemoryBus bus, uint tableAddress, int slots)
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

static int FindTaskIdByFunc(MemoryBus bus, uint taskBaseAddress, int slots, uint callback)
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

static string SummarizeRubyMovementTask(MemoryBus bus, uint taskBaseAddress, int taskId)
{
    var taskAddress = taskBaseAddress + (uint)(taskId * 0x28);
    Span<char> objects = stackalloc char[16 * 3];
    var written = 0;
    for (var i = 0; i < 16; i++)
    {
        var value = bus.Read8(taskAddress + 0x0A + (uint)i);
        if (i != 0)
        {
            objects[written++] = ' ';
        }

        var hex = value.ToString("X2", CultureInfo.InvariantCulture);
        objects[written++] = hex[0];
        objects[written++] = hex[1];
    }

    return $"#{taskId}:flags=0x{bus.Read16(taskAddress + 8):X4};objects={objects[..written].ToString()}";
}

static string SummarizeRubyPlayerObject(MemoryBus bus, uint objectBaseAddress, int slots)
{
    for (var i = 0; i < slots; i++)
    {
        var objectAddress = objectBaseAddress + (uint)(i * 0x24);
        if (IsPlausibleRubyObjectEvent(bus, objectAddress)
            && (bus.Read8(objectAddress + 8) == 0xFF || (bus.Read8(objectAddress + 2) & 1) != 0))
        {
            return SummarizeRubyObjectEvent(bus, objectAddress, i);
        }
    }

    return ScanRubyPlayerObject(bus);
}

static string SummarizeRubyObjectEvents(MemoryBus bus, uint objectBaseAddress, int slots)
{
    var parts = new List<string>();
    for (var i = 0; i < slots; i++)
    {
        var objectAddress = objectBaseAddress + (uint)(i * 0x24);
        if (IsPlausibleRubyObjectEvent(bus, objectAddress))
        {
            parts.Add(SummarizeRubyObjectEvent(bus, objectAddress, i));
        }
    }

    return parts.Count == 0 ? ScanRubyObjectEvents(bus, slots) : string.Join("|", parts);
}

static string SummarizeRubyObjectEvent(MemoryBus bus, uint objectAddress, int index)
{
    var flags0 = bus.Read8(objectAddress);
    var flags1 = bus.Read8(objectAddress + 1);
    var flags2 = bus.Read8(objectAddress + 2);
    var facingMovement = bus.Read8(objectAddress + 0x18);
    return string.Create(
        CultureInfo.InvariantCulture,
        $"#{index}:l={bus.Read8(objectAddress + 8)};map={bus.Read8(objectAddress + 0x0A)}.{bus.Read8(objectAddress + 9)};xy={ReadS16(bus, objectAddress + 0x10)}/{ReadS16(bus, objectAddress + 0x12)};prev={ReadS16(bus, objectAddress + 0x14)}/{ReadS16(bus, objectAddress + 0x16)};flags={flags0:X2}{flags1:X2}{flags2:X2};face={facingMovement & 0xF};move={facingMovement >> 4};act=0x{bus.Read8(objectAddress + 0x1C):X2};meta=0x{bus.Read8(objectAddress + 0x1E):X2}");
}

static string ScanRubyPlayerObject(MemoryBus bus)
{
    var ewramPlayer = ScanRubyPlayerObjectRange(bus, GbaMemoryMap.EwramStart, GbaMemoryMap.EwramSize);
    if (ewramPlayer != "none")
    {
        return ewramPlayer;
    }

    return ScanRubyPlayerObjectRange(bus, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramSize);
}

static string ScanRubyPlayerObjectRange(MemoryBus bus, uint start, int size)
{
    for (var address = start; address <= start + size - 0x24; address += 4)
    {
        if (IsPlausibleRubyObjectEvent(bus, address) && bus.Read8(address + 8) == 0xFF)
        {
            return $"scan@0x{address:X8}:{SummarizeRubyObjectEvent(bus, address, 0)}";
        }
    }

    return "none";
}

static string ScanRubyObjectEvents(MemoryBus bus, int slots)
{
    var ewramEvents = ScanRubyObjectEventsRange(bus, GbaMemoryMap.EwramStart, GbaMemoryMap.EwramSize, slots);
    if (ewramEvents != "none")
    {
        return ewramEvents;
    }

    return ScanRubyObjectEventsRange(bus, GbaMemoryMap.IwramStart, GbaMemoryMap.IwramSize, slots);
}

static string ScanRubyObjectEventsRange(MemoryBus bus, uint start, int size, int slots)
{
    for (var address = start; address <= start + size - (slots * 0x24); address += 4)
    {
        var parts = new List<string>();
        var hasPlayer = false;
        for (var i = 0; i < slots; i++)
        {
            var objectAddress = address + (uint)(i * 0x24);
            if (!IsPlausibleRubyObjectEvent(bus, objectAddress))
            {
                continue;
            }

            hasPlayer |= bus.Read8(objectAddress + 8) == 0xFF;
            parts.Add(SummarizeRubyObjectEvent(bus, objectAddress, i));
        }

        if (hasPlayer && parts.Count >= 2)
        {
            return $"scan@0x{address:X8}:{string.Join("|", parts)}";
        }
    }

    return "none";
}

static bool IsPlausibleRubyObjectEvent(MemoryBus bus, uint objectAddress)
{
    var flags0 = bus.Read8(objectAddress);
    if ((flags0 & 1) == 0 || flags0 == 0xFF)
    {
        return false;
    }

    var spriteId = bus.Read8(objectAddress + 4);
    var graphicsId = bus.Read8(objectAddress + 5);
    var currentX = ReadS16(bus, objectAddress + 0x10);
    var currentY = ReadS16(bus, objectAddress + 0x12);
    var previousX = ReadS16(bus, objectAddress + 0x14);
    var previousY = ReadS16(bus, objectAddress + 0x16);
    return spriteId < 128
        && graphicsId < 240
        && currentX is >= -16 and <= 256
        && currentY is >= -16 and <= 256
        && previousX is >= -16 and <= 256
        && previousY is >= -16 and <= 256;
}

static short ReadS16(MemoryBus bus, uint address) => unchecked((short)bus.Read16(address));

static ushort ReadRubyVar(MemoryBus bus, ushort id)
{
    const uint saveBlock1 = 0x0202_5734;
    const uint varsOffset = 0x1340;
    const ushort varsStart = 0x4000;
    return bus.Read16(saveBlock1 + varsOffset + (uint)((id - varsStart) * 2));
}

static void LoadSaveFileIfRequested(GbaSystem gba, RunOptions options)
{
    if (options.SaveFile is not { Length: > 0 } path || !File.Exists(path))
    {
        return;
    }

    var data = File.ReadAllBytes(path);
    gba.Bus.LoadSaveData(data);
    Console.WriteLine($"Loaded {data.Length:N0} bytes of save data from {Path.GetFullPath(path)}.");
}

static void WriteSaveFileIfRequested(GbaSystem gba, RunOptions options)
{
    if (options.SaveReadOnly || options.SaveFile is not { Length: > 0 } path || gba.Bus.SaveDataSize == 0)
    {
        return;
    }

    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllBytes(fullPath, gba.Bus.ExportSaveData().ToArray());
    Console.WriteLine($"Wrote {gba.Bus.SaveDataSize:N0} bytes of save data to {fullPath}.");
}

static GbaKey ParseKeys(string value)
{
    var keys = GbaKey.None;
    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Enum.TryParse<GbaKey>(part, ignoreCase: true, out var key))
        {
            throw new ArgumentException($"Unknown key name: {part}");
        }

        keys |= key;
    }

    return keys;
}

static int ParseDebugLayer(string value)
{
    return value.ToLowerInvariant() switch
    {
        "bg0" or "0" => 0,
        "bg1" or "1" => 1,
        "bg2" or "2" => 2,
        "bg3" or "3" => 3,
        "obj" or "object" or "objects" or "4" => 4,
        _ => throw new ArgumentException($"Unknown debug layer '{value}'. Expected bg0, bg1, bg2, bg3, or obj.")
    };
}

static KeyEvent ParseKeyEvent(string value)
{
    var separator = value.IndexOf(':');
    if (separator <= 0 || separator == value.Length - 1)
    {
        throw new ArgumentException($"Invalid key event '{value}'. Expected STEP:KEYS.");
    }

    if (!int.TryParse(value[..separator], out var step) || step < 0)
    {
        throw new ArgumentException($"Invalid key event step in '{value}'.");
    }

    return new KeyEvent(step, ParseKeys(value[(separator + 1)..]));
}

static FrameKeyEvent ParseFrameKeyEvent(string value)
{
    var separator = value.IndexOf(':');
    if (separator <= 0 || separator == value.Length - 1)
    {
        throw new ArgumentException($"Invalid frame event '{value}'. Expected FRAME:KEYS.");
    }

    if (!int.TryParse(value[..separator], out var frame) || frame < 0)
    {
        throw new ArgumentException($"Invalid frame event frame in '{value}'.");
    }

    return new FrameKeyEvent(frame, ParseKeys(value[(separator + 1)..]));
}

static void AddFrameTapEvents(List<FrameKeyEvent> keyEvents, string value)
{
    var parts = value.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length != 5
        || !int.TryParse(parts[1], out var firstFrame)
        || !int.TryParse(parts[2], out var intervalFrames)
        || !int.TryParse(parts[3], out var count)
        || !int.TryParse(parts[4], out var durationFrames)
        || firstFrame < 0
        || intervalFrames <= 0
        || count < 0
        || durationFrames <= 0)
    {
        throw new ArgumentException($"Invalid frame tap script '{value}'. Expected KEYS:FIRST_FRAME:INTERVAL_FRAMES:COUNT:DURATION_FRAMES.");
    }

    var keys = ParseKeys(parts[0]);
    for (var i = 0; i < count; i++)
    {
        var frame = firstFrame + i * intervalFrames;
        keyEvents.Add(new FrameKeyEvent(frame, keys));
        keyEvents.Add(new FrameKeyEvent(frame + durationFrames, GbaKey.None));
    }
}

static void AddInputScriptEvents(List<FrameKeyEvent> keyEvents, string path)
{
    var cursor = 0;
    var fullPath = Path.GetFullPath(path);
    var lineNumber = 0;
    foreach (var rawLine in File.ReadLines(fullPath))
    {
        lineNumber++;
        var commentStart = rawLine.IndexOf('#');
        var line = (commentStart >= 0 ? rawLine[..commentStart] : rawLine).Trim();
        if (line.Length == 0)
        {
            continue;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "at":
                    RequirePartCount(parts, 3, 4, "at FRAME KEYS [DURATION]");
                    cursor = ParseNonNegativeInt(parts[1], "frame");
                    AddTap(keyEvents, cursor, ParseKeys(parts[2]), parts.Length == 4 ? ParsePositiveInt(parts[3], "duration") : 1);
                    break;

                case "tap" when parts.Length >= 3 && int.TryParse(parts[1], out var absoluteFrame):
                    RequirePartCount(parts, 3, 4, "tap FRAME KEYS [DURATION]");
                    cursor = absoluteFrame;
                    AddTap(keyEvents, cursor, ParseKeys(parts[2]), parts.Length == 4 ? ParsePositiveInt(parts[3], "duration") : 4);
                    break;

                case "tap":
                    RequirePartCount(parts, 2, 3, "tap KEYS [DURATION]");
                    AddTap(keyEvents, cursor, ParseKeys(parts[1]), parts.Length == 3 ? ParsePositiveInt(parts[2], "duration") : 4);
                    cursor += parts.Length == 3 ? ParsePositiveInt(parts[2], "duration") : 4;
                    break;

                case "press":
                    RequirePartCount(parts, 3, 3, "press FRAME KEYS");
                    cursor = ParseNonNegativeInt(parts[1], "frame");
                    keyEvents.Add(new FrameKeyEvent(cursor, ParseKeys(parts[2])));
                    break;

                case "release":
                    RequirePartCount(parts, 2, 2, "release FRAME");
                    cursor = ParseNonNegativeInt(parts[1], "frame");
                    keyEvents.Add(new FrameKeyEvent(cursor, GbaKey.None));
                    break;

                case "wait":
                    RequirePartCount(parts, 2, 2, "wait FRAMES");
                    cursor += ParsePositiveInt(parts[1], "frames");
                    break;

                default:
                    throw new ArgumentException($"Unknown input script command '{parts[0]}'.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            throw new ArgumentException($"{fullPath}:{lineNumber}: {ex.Message}", ex);
        }
    }
}

static FrameHashEvent ParseFrameHashEvent(string value)
{
    var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length is < 2 or > 4)
    {
        throw new ArgumentException($"Invalid hash tap '{value}'. Expected HASH:KEYS[:DURATION_FRAMES[:MIN_FRAME]].");
    }

    var hashText = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0];
    if (!ulong.TryParse(hashText, System.Globalization.NumberStyles.HexNumber, provider: null, out var hash))
    {
        throw new ArgumentException($"Invalid frame hash '{parts[0]}'.");
    }

    var duration = parts.Length >= 3 ? ParsePositiveInt(parts[2], "duration") : 4;
    var minFrame = parts.Length == 4 ? ParseNonNegativeInt(parts[3], "minimum frame") : 0;
    return new FrameHashEvent(hash, ParseKeys(parts[1]), duration, minFrame);
}

static MemoryTriggerEvent ParseMemoryTriggerEvent(string value)
{
    var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length is < 4 or > 6)
    {
        throw new ArgumentException($"Invalid memory tap '{value}'. Expected ADDRESS:BYTES:VALUE:KEYS[:DURATION[:MIN_FRAME]].");
    }

    var bytes = ParsePositiveInt(parts[1], "bytes");
    if (bytes is not (1 or 2 or 4))
    {
        throw new ArgumentException("Memory tap byte width must be 1, 2, or 4.");
    }

    var duration = parts.Length >= 5 ? ParsePositiveInt(parts[4], "duration") : 4;
    var minFrame = parts.Length == 6 ? ParseNonNegativeInt(parts[5], "minimum frame") : 0;
    return new MemoryTriggerEvent(ParseAddress(parts[0]), bytes, ParseAddress(parts[2]), ParseKeys(parts[3]), duration, minFrame);
}

static FramePokeEvent ParseFramePokeEvent(string value)
{
    var parts = value.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length is not (3 or 4) || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame) || frame < 0)
    {
        throw new ArgumentException("--poke-frame must be FRAME:ADDRESS:VALUE[:8|16|32].");
    }

    var bytes = parts.Length == 4
        ? parts[3] switch
        {
            "8" => 1,
            "16" => 2,
            "32" => 4,
            _ => throw new ArgumentException("--poke-frame size must be 8, 16, or 32.")
        }
        : 4;

    var address = ParseAddress(parts[1]);
    var parsedValue = ParseAddress(parts[2]);
    return new FramePokeEvent(frame, address, parsedValue, bytes);
}

static void AddTap(List<FrameKeyEvent> keyEvents, int frame, GbaKey keys, int durationFrames)
{
    keyEvents.Add(new FrameKeyEvent(frame, keys));
    keyEvents.Add(new FrameKeyEvent(frame + durationFrames, GbaKey.None));
}

static void RequirePartCount(string[] parts, int min, int max, string usage)
{
    if (parts.Length < min || parts.Length > max)
    {
        throw new ArgumentException($"Expected {usage}.");
    }
}

static int ParseNonNegativeInt(string value, string name)
{
    if (!int.TryParse(value, out var parsed) || parsed < 0)
    {
        throw new ArgumentException($"Invalid {name}: {value}.");
    }

    return parsed;
}

static int ParsePositiveInt(string value, string name)
{
    if (!int.TryParse(value, out var parsed) || parsed <= 0)
    {
        throw new ArgumentException($"Invalid {name}: {value}.");
    }

    return parsed;
}

static void TraceIfNeeded(GbaSystem gba, long step, int frame, RunOptions options, InstructionTraceLimiter? traceLimiter)
{
    if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
    {
        return;
    }

    if (!options.Trace && !options.TraceRanges.Any(range => range.Contains(gba.Cpu.Pc)))
    {
        return;
    }

    if (traceLimiter is not null && !traceLimiter.ShouldTrace(gba.Cpu.Pc))
    {
        return;
    }

    Console.WriteLine(FormatTraceLine(gba, step, frame));
}

static void RecordTraceTailIfNeeded(GbaSystem gba, long step, int frame, RunOptions options, InstructionTraceTail? traceTail)
{
    if (traceTail is null)
    {
        return;
    }

    if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
    {
        return;
    }

    traceTail.Record(FormatTraceLine(gba, step, frame));
}

static InstructionTraceTail? CreateTraceTail(RunOptions options)
{
    return options.TraceTail > 0 ? new InstructionTraceTail(options.TraceTail) : null;
}

static InstructionTraceLimiter? CreateTraceLimiter(RunOptions options)
{
    return options.TraceHitLimit > 0 ? new InstructionTraceLimiter(options.TraceHitLimit) : null;
}

static string FormatTraceLine(GbaSystem gba, long step, int frame)
{
    var pc = gba.Cpu.Pc;
    var thumb = gba.Cpu.ThumbState;
    var instruction = ReadTraceInstruction(gba, pc, thumb);
    return $"{step:D8} FRAME={frame:D5} CYCLES={gba.Scheduler.Now:D12} LINE={gba.Bus.VerticalCount:D3} DISPSTAT={gba.Bus.DisplayStatus:X4} PC={pc:X8} {(thumb ? "THUMB" : "ARM  ")} INS={(thumb ? instruction & 0xFFFF : instruction):X8} r0={gba.Cpu[0]:X8} r1={gba.Cpu[1]:X8} r2={gba.Cpu[2]:X8} r3={gba.Cpu[3]:X8} r4={gba.Cpu[4]:X8} r5={gba.Cpu[5]:X8} r6={gba.Cpu[6]:X8} r7={gba.Cpu[7]:X8} r8={gba.Cpu[8]:X8} r9={gba.Cpu[9]:X8} r10={gba.Cpu[10]:X8} r11={gba.Cpu[11]:X8} r12={gba.Cpu[12]:X8} sp={gba.Cpu[13]:X8} lr={gba.Cpu[14]:X8} CPSR={gba.Cpu.Cpsr:X8}";
}

static uint ReadTraceInstruction(GbaSystem gba, uint pc, bool thumb)
{
    var restoreBiosAccessible = gba.Bus.BiosAccessible;
    if (pc < GbaMemoryMap.BiosSize)
    {
        gba.Bus.SetBiosAccessible(true);
    }

    try
    {
        return thumb ? gba.Bus.Read16(pc) : gba.Bus.Read32(pc);
    }
    finally
    {
        gba.Bus.SetBiosAccessible(restoreBiosAccessible);
    }
}

static bool StopIfInvalidPc(GbaSystem gba, RunOptions options, InstructionTraceTail? traceTail, long step)
{
    if (!options.StopOnInvalidPc || IsExecutablePc(gba.Cpu.Pc, gba.Bus.HasBios))
    {
        return false;
    }

    Console.WriteLine($"STOP: invalid executable PC=0x{gba.Cpu.Pc:X8} at step={step:N0}, cycles={gba.Scheduler.Now:N0}.");
    traceTail?.Dump();
    DumpMemoryIfRequested(gba, options);
    PrintRegisters(gba);
    return true;
}

static bool StopIfRequestedPc(GbaSystem gba, RunOptions options, InstructionTraceTail? traceTail, long step, int frame, ref int stopPcHits)
{
    if (options.StopPc is not { } stopPc || gba.Cpu.Pc != stopPc)
    {
        return false;
    }

    stopPcHits++;
    if (stopPcHits < options.StopPcHit)
    {
        return false;
    }

    Console.WriteLine($"STOP: reached PC=0x{gba.Cpu.Pc:X8} hit={stopPcHits:N0} at step={step:N0}, frame={frame:N0}, cycles={gba.Scheduler.Now:N0}.");
    traceTail?.Dump();
    DumpMemoryIfRequested(gba, options);
    PrintRegisters(gba);
    PrintStateIfRequested(gba, options);
    return true;
}

static void SnapshotIfRequestedPc(GbaSystem gba, RunOptions options, PcSnapshotWriter? pcSnapshots, long step, int frame, ref int snapshotPcHits)
{
    if (options.SnapshotPcs.Count == 0 || !options.SnapshotPcs.Contains(gba.Cpu.Pc))
    {
        return;
    }

    if (options.TraceFrameRange is { } frameRange && !frameRange.Contains(frame))
    {
        return;
    }

    if (options.SnapshotPcLimit > 0 && snapshotPcHits >= options.SnapshotPcLimit)
    {
        return;
    }

    snapshotPcHits++;
    Console.WriteLine($"SNAPSHOT: PC=0x{gba.Cpu.Pc:X8} hit={snapshotPcHits:N0} step={step:N0} frame={frame:N0} cycles={gba.Scheduler.Now:N0}");
    Console.WriteLine(FormatTraceLine(gba, step, frame));
    WritePcSnapshotIfNeeded(gba, options, pcSnapshots, snapshotPcHits, step, frame);
    DumpMemoryIfRequested(gba, options);
    PrintRegisters(gba);
}

static void WritePcSnapshotIfNeeded(GbaSystem gba, RunOptions options, PcSnapshotWriter? pcSnapshots, int hit, long step, int frame)
{
    if (pcSnapshots is null)
    {
        return;
    }

    var writer = pcSnapshots.Writer;
    writer.Write(hit);
    writer.Write(',');
    writer.Write(step);
    writer.Write(',');
    writer.Write(frame);
    writer.Write(',');
    writer.Write(gba.Scheduler.Now);
    writer.Write(',');
    writer.Write(gba.Bus.VerticalCount);
    writer.Write(',');
    WriteHex32(writer, gba.Cpu.Pc);
    writer.Write(',');
    WriteHex32(writer, gba.Cpu.Cpsr);
    writer.Write(',');
    writer.Write(gba.Cpu.ThumbState ? 1 : 0);
    writer.Write(',');
    writer.Write(FormatCsvToken(gba.Cpu.Mode.ToString()));
    writer.Write(',');
    WriteHex32(writer, ReadTraceInstruction(gba, gba.Cpu.Pc, gba.Cpu.ThumbState));
    for (var register = 0; register < 13; register++)
    {
        writer.Write(',');
        WriteHex32(writer, gba.Cpu[register]);
    }

    var sp = gba.Cpu[13];
    writer.Write(',');
    WriteHex32(writer, sp);
    writer.Write(',');
    WriteHex32(writer, gba.Cpu[14]);
    writer.Write(',');
    WriteHex16(writer, gba.Bus.PeekIo16(IoRegisters.KEYINPUT));
    writer.Write(',');
    writer.Write(FormatCsvToken(gba.Keypad.PressedKeys.ToString()));
    writer.Write(',');
    WriteHex16(writer, gba.Bus.PeekIo16(IoRegisters.DISPCNT));
    writer.Write(',');
    WriteHex16(writer, gba.Bus.PeekIo16(IoRegisters.DISPSTAT));
    writer.Write(',');
    WriteHex16(writer, gba.Bus.PeekIo16(IoRegisters.VCOUNT));
    writer.Write(',');
    WriteHex16(writer, gba.Bus.InterruptEnable);
    writer.Write(',');
    WriteHex16(writer, gba.Bus.InterruptFlags);
    writer.Write(',');
    writer.Write(gba.Bus.InterruptMasterEnable ? 1 : 0);
    writer.Write(',');
    WriteHex16(writer, gba.Bus.BiosInterruptFlags);

    for (var word = 0; word < options.PcSnapshotStackWords; word++)
    {
        writer.Write(',');
        WriteHex32OrBlank(writer, TryRead32(gba.Bus, sp + (uint)(word * 4)));
    }

    for (uint address = 0x0300_7E10; address <= 0x0300_7E34; address += 4)
    {
        writer.Write(',');
        WriteHex32OrBlank(writer, TryRead32(gba.Bus, address));
    }

    writer.WriteLine();
    writer.Flush();
}

static int ReportExecutionException(GbaSystem gba, RunOptions options, InstructionTraceTail? traceTail, Exception ex, long step, int frame)
{
    Console.Error.WriteLine($"CRASH: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine($"At step={step:N0}, frame={frame:N0}, PC=0x{gba.Cpu.Pc:X8}, cycles={gba.Scheduler.Now:N0}.");
    traceTail?.Dump();
    DumpMemoryIfRequested(gba, options);
    PrintRegisters(gba);
    PrintStateIfRequested(gba, options);
    WriteSaveFileIfRequested(gba, options);
    return 4;
}

static bool IsExecutablePc(uint pc, bool hasBios)
{
    var region = pc >> 24;
    return (hasBios && pc < GbaMemoryMap.BiosSize)
        || region is 0x02 or 0x03
        || region is >= 0x05 and <= 0x07
        || region is >= 0x08 and <= 0x0D;
}

static void PrintRegisters(GbaSystem gba)
{
    for (var i = 0; i < 16; i += 4)
    {
        Console.WriteLine($"r{i:D2}=0x{gba.Cpu[i]:X8} r{i + 1:D2}=0x{gba.Cpu[i + 1]:X8} r{i + 2:D2}=0x{gba.Cpu[i + 2]:X8} r{i + 3:D2}=0x{gba.Cpu[i + 3]:X8}");
    }

    Console.WriteLine($"CPSR=0x{gba.Cpu.Cpsr:X8}");
}

static void PrintStateIfRequested(GbaSystem gba, RunOptions options)
{
    if (!options.PrintState)
    {
        return;
    }

    PrintRegisters(gba);
    Console.WriteLine($"DISPCNT=0x{gba.Bus.DisplayControl:X4} DISPSTAT=0x{gba.Bus.DisplayStatus:X4} VCOUNT={gba.Bus.VerticalCount}");
    Console.WriteLine($"IE=0x{gba.Bus.InterruptEnable:X4} IF=0x{gba.Bus.InterruptFlags:X4} IME={(gba.Bus.InterruptMasterEnable ? 1 : 0)}");
    Console.WriteLine($"BG0CNT=0x{gba.Bus.PeekIo16(IoRegisters.BG0CNT):X4} BG1CNT=0x{gba.Bus.PeekIo16(IoRegisters.BG1CNT):X4} BG2CNT=0x{gba.Bus.PeekIo16(IoRegisters.BG2CNT):X4} BG3CNT=0x{gba.Bus.PeekIo16(IoRegisters.BG3CNT):X4}");
    Console.WriteLine($"BG2PA=0x{gba.Bus.PeekIo16(IoRegisters.BG2PA):X4} BG2PB=0x{gba.Bus.PeekIo16(IoRegisters.BG2PB):X4} BG2PC=0x{gba.Bus.PeekIo16(IoRegisters.BG2PC):X4} BG2PD=0x{gba.Bus.PeekIo16(IoRegisters.BG2PD):X4} BG2X=0x{gba.Bus.PeekIo32(IoRegisters.BG2X):X8} BG2Y=0x{gba.Bus.PeekIo32(IoRegisters.BG2Y):X8}");
    Console.WriteLine($"BG3PA=0x{gba.Bus.PeekIo16(IoRegisters.BG3PA):X4} BG3PB=0x{gba.Bus.PeekIo16(IoRegisters.BG3PB):X4} BG3PC=0x{gba.Bus.PeekIo16(IoRegisters.BG3PC):X4} BG3PD=0x{gba.Bus.PeekIo16(IoRegisters.BG3PD):X4} BG3X=0x{gba.Bus.PeekIo32(IoRegisters.BG3X):X8} BG3Y=0x{gba.Bus.PeekIo32(IoRegisters.BG3Y):X8}");
    Console.WriteLine($"WIN0H=0x{gba.Bus.PeekIo16(IoRegisters.WIN0H):X4} WIN1H=0x{gba.Bus.PeekIo16(IoRegisters.WIN1H):X4} WIN0V=0x{gba.Bus.PeekIo16(IoRegisters.WIN0V):X4} WIN1V=0x{gba.Bus.PeekIo16(IoRegisters.WIN1V):X4} WININ=0x{gba.Bus.PeekIo16(IoRegisters.WININ):X4} WINOUT=0x{gba.Bus.PeekIo16(IoRegisters.WINOUT):X4}");
    Console.WriteLine($"BLDCNT=0x{gba.Bus.PeekIo16(IoRegisters.BLDCNT):X4} BLDALPHA=0x{gba.Bus.PeekIo16(IoRegisters.BLDALPHA):X4} BLDY=0x{gba.Bus.PeekIo16(IoRegisters.BLDY):X4}");
    var oamSummary = SummarizeObjects(gba.Bus.ObjectAttributeMemory);
    Console.WriteLine($"KEYINPUT=0x{gba.Bus.PeekIo16(IoRegisters.KEYINPUT):X4} KEYCNT=0x{gba.Bus.PeekIo16(IoRegisters.KEYCNT):X4} activeObjects={oamSummary.Active} hiddenObjects={oamSummary.Hidden} firstActive={oamSummary.FirstActive}");
}

static bool ShouldStopAtFrame(RunOptions options, int frame)
    => options.StopFrame is { } stopFrame && frame >= stopFrame;

static Stopwatch? StartWallClockLimit(RunOptions options)
    => options.MaxSeconds is null ? null : Stopwatch.StartNew();

static bool ShouldStopAtWallClock(RunOptions options, Stopwatch? stopwatch)
    => stopwatch is not null && stopwatch.Elapsed.TotalSeconds >= options.MaxSeconds!.Value;

static ObjectSummary SummarizeObjects(ReadOnlySpan<byte> oam)
{
    var count = 0;
    var hidden = 0;
    var firstActive = "none";
    for (var sprite = 0; sprite < 128; sprite++)
    {
        var offset = sprite * 8;
        var attr0 = (ushort)(oam[offset] | (oam[offset + 1] << 8));
        var attr1 = (ushort)(oam[offset + 2] | (oam[offset + 3] << 8));
        var attr2 = (ushort)(oam[offset + 4] | (oam[offset + 5] << 8));
        var affineMode = (attr0 >> 8) & 0x3;
        if (affineMode == 2)
        {
            hidden++;
            continue;
        }

        if (count == 0)
        {
            firstActive = $"#{sprite}:a0=0x{attr0:X4};a1=0x{attr1:X4};a2=0x{attr2:X4}";
        }

        count++;
    }

    return new ObjectSummary(count, hidden, firstActive);
}

static int PrintInfo(Cartridge cartridge, GbaSystem gba, string romPath)
{
    Console.WriteLine("gbaSharp cartridge info");
    Console.WriteLine($"Path: {Path.GetFullPath(romPath)}");
    Console.WriteLine($"ROM size: {cartridge.Rom.Length:N0} bytes");
    Console.WriteLine($"Title: {Display(cartridge.Header.Title)}");
    Console.WriteLine($"Game code: {Display(cartridge.Header.GameCode)}");
    Console.WriteLine($"Maker code: {Display(cartridge.Header.MakerCode)}");
    Console.WriteLine($"Version: {cartridge.Header.SoftwareVersion}");
    Console.WriteLine($"Detected save: {cartridge.SaveType}");
    Console.WriteLine($"Fixed value valid: {cartridge.Header.HasValidFixedValue}");
    Console.WriteLine($"Header checksum valid: {cartridge.Header.HasValidComplementCheck}");
    Console.WriteLine($"Initial PC: 0x{gba.Cpu.Pc:X8}");
    return 0;
}

static uint ParseAddress(string value)
{
    var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
    return Convert.ToUInt32(normalized, 16);
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  gbaSharp <rom.gba>");
    Console.Error.WriteLine("  gbaSharp compat <rom-directory> [--bios gba_bios.bin] [--align-rom-entry] [--limit N] [--start-index N] [--indexes 12,15-20] [--suite single|boot|standard|input|gameplay] [--phase NAME] [--max-steps N] [--frame-step-budget N] [--max-seconds N] [--stop-frame N] [--output compat-report.csv] [--summary-output compat-summary.csv] [--capture-dir captures --capture-statuses crash,static] [--error-details]");
    Console.Error.WriteLine("  gbaSharp compat-summary <compat-report.csv> [summary.csv]");
    Console.Error.WriteLine("  gbaSharp save-probe <rom-directory> [--limit N] [--start-index N] [--indexes 12,15-20] [--output save-probe.csv] [--summary-output save-probe-summary.csv]");
    Console.Error.WriteLine("  gbaSharp run <rom.gba> [--max-steps N] [--max-seconds N] [--stop-frame N] [--align-rom-entry] [--trace] [--audio-wav audio.wav]");
    Console.Error.WriteLine("  gbaSharp test-rom <rom.gba> [--max-steps N] [--max-seconds N] [--stop-frame N] [--trace] [--success-pc HEX] [--failure-pc HEX]");
    Console.Error.WriteLine("  gbaSharp dump-frame <rom.gba> [--max-steps N] [--max-seconds N] [--stop-frame N] [--trace] [--output frame.ppm] [--audio-csv direct.csv] [--psg-csv psg.csv] [--audio-wav audio.wav]");
    Console.Error.WriteLine("  gbaSharp compare-bios <rom.gba> --bios gba_bios.bin [--stop-frame N] [--compare-output diff.csv] [--compare-start-frame N] [--compare-frame-interval N] [--compare-first-diff-only] [--compare-game-state-only] [--compare-align-rom-entry]");
    Console.Error.WriteLine("  gbaSharp capture-frames <rom.gba> [--max-steps N] [--max-seconds N] [--output-dir captures] [--sample-steps N]");
    Console.Error.WriteLine("  gbaSharp verify-frame <rom.gba> --baseline baseline.ppm [--actual actual.ppm] [--diff diff.ppm] [--write-baseline] [--max-different-pixels N] [--max-channel-delta N]");
    Console.Error.WriteLine("  Capture by frame: [--sample-frames N] [--frame-range START:END]");
    Console.Error.WriteLine("  Debug video: [--debug-layer bg0|bg1|bg2|bg3|obj] [--debug-layer-dir DIR]");
    Console.Error.WriteLine("  Debug snapshots: [--snapshot-csv state.csv] [--snapshot-frames N] [--pc-snapshot-csv pcs.csv] [--pc-snapshot-stack-words N] (uses --trace-frames as an optional frame filter)");
    Console.Error.WriteLine("  Optional for run/test-rom/dump-frame: [--keys A,B,Start]");
    Console.Error.WriteLine("  Tracing: [--trace-swi] [--trace-irq] [--trace-dma] [--trace-eeprom --trace-eeprom-limit N]");
    Console.Error.WriteLine("  Script input: [--key-event STEP:A] [--key-event STEP:none]");
    Console.Error.WriteLine("  Frame input/debug: [--frame-event FRAME:A] [--tap-frames A:120:30:20:4] [--input-script script.txt] [--poke-frame FRAME:ADDRESS:VALUE[:8|16|32]]");
    Console.Error.WriteLine("  Dynamic input: [--tap-on-hash HASH:KEYS[:DURATION[:MIN_FRAME]]] [--tap-on-memory ADDRESS:BYTES:VALUE:KEYS[:DURATION[:MIN_FRAME]]]");
    Console.Error.WriteLine("  Menu helper: [--menu-select INDEX] selects a zero-based menu row with Down taps then Start");
    Console.Error.WriteLine("  Save data: [--save-file game.sav] loads an existing save and writes it back on exit unless --save-read-only is set");
    Console.Error.WriteLine("  Audio capture: [--audio-wav audio.wav] [--audio-sample-rate 44100] [--audio-gain 0.5]");
    Console.Error.WriteLine("  Debug reads/writes: [--watch-read 04000130] [--watch-read-range 03000000:03007FFF] [--watch-write 03007E44] [--watch-write-range 03000000:03007FFF] [--watch-limit 100] [--dump-memory 03000000:100] [--disassemble-memory 03000000:100[:arm|thumb]]");
    Console.Error.WriteLine("  Debug execution: [--stop-pc 08000100] [--stop-pc-hit 2] [--snapshot-pc 08000100] [--snapshot-pc-limit 4] [--pc-snapshot-csv pcs.csv] [--stop-on-invalid-pc] [--print-state] [--trace-swi] [--trace-irq] [--trace-irq-limit 100] [--trace-dma] [--trace-input] [--trace-range 08000000:08000100] [--trace-frames 120:140] [--trace-tail 200] [--trace-hit-limit 4]");
}

static string Csv(string value)
{
    if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
    {
        return value;
    }

    return $"\"{value.Replace("\"", "\"\"")}\"";
}

static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "(blank)" : value;

static ulong HashFramebuffer(ReadOnlySpan<uint> framebuffer)
{
    const ulong offsetBasis = 14_695_981_039_346_656_037;
    const ulong prime = 1_099_511_628_211;
    var hash = offsetBasis;
    ref var pixel = ref MemoryMarshal.GetReference(framebuffer);
    var length = framebuffer.Length;
    var i = 0;
    for (; i <= length - 4; i += 4)
    {
        hash ^= Unsafe.Add(ref pixel, i);
        hash *= prime;
        hash ^= Unsafe.Add(ref pixel, i + 1);
        hash *= prime;
        hash ^= Unsafe.Add(ref pixel, i + 2);
        hash *= prime;
        hash ^= Unsafe.Add(ref pixel, i + 3);
        hash *= prime;
    }

    for (; i < length; i++)
    {
        hash ^= Unsafe.Add(ref pixel, i);
        hash *= prime;
    }

    return hash;
}

static uint[] GetOutputFramebuffer(GbaSystem gba, RunOptions options)
    => options.DebugLayer is { } layer ? gba.Video.RenderDebugLayer(layer) : gba.Video.Framebuffer.ToArray();

static void WriteDebugLayerPpms(GbaSystem gba, string directory)
{
    WritePpm(Path.Combine(directory, "full.ppm"), gba.Video.Framebuffer);
    WritePpm(Path.Combine(directory, "bg0.ppm"), gba.Video.RenderDebugLayer(0));
    WritePpm(Path.Combine(directory, "bg1.ppm"), gba.Video.RenderDebugLayer(1));
    WritePpm(Path.Combine(directory, "bg2.ppm"), gba.Video.RenderDebugLayer(2));
    WritePpm(Path.Combine(directory, "bg3.ppm"), gba.Video.RenderDebugLayer(3));
    WritePpm(Path.Combine(directory, "obj.ppm"), gba.Video.RenderDebugLayer(4));
    WritePpm(Path.Combine(directory, "pre-blend.ppm"), gba.Video.RenderDebugPreBlend());
    WritePpm(Path.Combine(directory, "second-target.ppm"), gba.Video.RenderDebugSecondTarget());
    WritePpm(Path.Combine(directory, "top-layer-map.ppm"), gba.Video.RenderDebugTopLayerMap());
    WritePpm(Path.Combine(directory, "second-layer-map.ppm"), gba.Video.RenderDebugSecondLayerMap());
    for (var bg = 0; bg <= 3; bg++)
    {
        WriteRegularBgDebugCsv(Path.Combine(directory, $"bg{bg}-regular-samples.csv"), gba.Video.RenderDebugRegularBgSamples(bg));
    }

    WriteAffineDebugCsv(Path.Combine(directory, "bg2-affine-samples.csv"), gba.Video.RenderDebugAffineSamples(2));
    WriteAffineDebugCsv(Path.Combine(directory, "bg3-affine-samples.csv"), gba.Video.RenderDebugAffineSamples(3));
}

static void WriteRegularBgDebugCsv(string path, IReadOnlyList<RegularBgDebugSample> samples)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
    writer.WriteLine("x,y,bg,control,sourceX,sourceY,tileX,tileY,screenOffset,screenEntry,paletteIndex,hofs,vofs");
    for (var pixel = 0; pixel < samples.Count; pixel++)
    {
        var sample = samples[pixel];
        if (!sample.Valid)
        {
            continue;
        }

        var x = pixel % VideoController.Width;
        var y = pixel / VideoController.Width;
        writer.Write(x.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(y.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.Bg.ToString(CultureInfo.InvariantCulture));
        writer.Write(",0x");
        writer.Write(sample.Control.ToString("X4", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.SourceX.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.SourceY.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.TileX.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.TileY.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.ScreenOffset.ToString(CultureInfo.InvariantCulture));
        writer.Write(",0x");
        writer.Write(sample.ScreenEntry.ToString("X4", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.PaletteIndex.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.HOffset.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.WriteLine(sample.VOffset.ToString(CultureInfo.InvariantCulture));
    }
}

static void WriteAffineDebugCsv(string path, IReadOnlyList<AffineDebugSample> samples)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
    writer.WriteLine("x,y,bg,control,fixedX,fixedY,sourceX,sourceY,tileX,tileY,mapOffset,tileNumber,tileOffset,paletteIndex,pa,pb,pc,pd,referenceX,referenceY");
    for (var pixel = 0; pixel < samples.Count; pixel++)
    {
        var sample = samples[pixel];
        if (!sample.Valid)
        {
            continue;
        }

        var x = pixel % VideoController.Width;
        var y = pixel / VideoController.Width;
        writer.Write(x.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(y.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.Bg.ToString(CultureInfo.InvariantCulture));
        writer.Write(",0x");
        writer.Write(sample.Control.ToString("X4", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.FixedX.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.FixedY.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.SourceX.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.SourceY.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.TileX.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.TileY.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.MapOffset.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.TileNumber.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.TileOffset.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.PaletteIndex.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.Pa.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.Pb.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.Pc.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.Pd.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(sample.ReferenceX.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.WriteLine(sample.ReferenceY.ToString(CultureInfo.InvariantCulture));
    }
}

static void WritePpm(string path, ReadOnlySpan<uint> framebuffer)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var stream = File.Create(path);
    using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write($"P6\n{VideoController.Width} {VideoController.Height}\n255\n");
    writer.Flush();

    Span<byte> row = stackalloc byte[VideoController.Width * 3];
    for (var y = 0; y < VideoController.Height; y++)
    {
        for (var x = 0; x < VideoController.Width; x++)
        {
            var color = framebuffer[y * VideoController.Width + x];
            row[x * 3] = (byte)(color >> 16);
            row[x * 3 + 1] = (byte)(color >> 8);
            row[x * 3 + 2] = (byte)color;
        }

        stream.Write(row);
    }
}

static uint[] ReadPpm(string path)
{
    using var stream = File.OpenRead(path);
    var magic = ReadPpmToken(stream);
    if (magic != "P6")
    {
        throw new ArgumentException($"{path} is not a binary PPM (P6) image.");
    }

    var width = int.Parse(ReadPpmToken(stream), System.Globalization.CultureInfo.InvariantCulture);
    var height = int.Parse(ReadPpmToken(stream), System.Globalization.CultureInfo.InvariantCulture);
    var maxValue = int.Parse(ReadPpmToken(stream), System.Globalization.CultureInfo.InvariantCulture);
    if (width != VideoController.Width || height != VideoController.Height || maxValue != 255)
    {
        throw new ArgumentException($"{path} has unsupported PPM dimensions or depth. Expected {VideoController.Width}x{VideoController.Height} max 255.");
    }

    var bytes = new byte[width * height * 3];
    stream.ReadExactly(bytes);
    var framebuffer = new uint[width * height];
    for (var i = 0; i < framebuffer.Length; i++)
    {
        var offset = i * 3;
        framebuffer[i] = 0xFF00_0000u | ((uint)bytes[offset] << 16) | ((uint)bytes[offset + 1] << 8) | bytes[offset + 2];
    }

    return framebuffer;
}

static string ReadPpmToken(Stream stream)
{
    var bytes = new List<byte>();
    var inComment = false;
    while (true)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            if (bytes.Count == 0)
            {
                throw new ArgumentException("Unexpected end of PPM header.");
            }

            break;
        }

        var b = (byte)value;
        if (inComment)
        {
            inComment = b != '\n';
            continue;
        }

        if (b == '#')
        {
            inComment = true;
            continue;
        }

        if (char.IsWhiteSpace((char)b))
        {
            if (bytes.Count == 0)
            {
                continue;
            }

            break;
        }

        bytes.Add(b);
    }

    return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
}

static FrameComparison CompareFramebuffers(ReadOnlySpan<uint> baseline, ReadOnlySpan<uint> actual, int channelTolerance)
{
    if (baseline.Length != actual.Length)
    {
        throw new ArgumentException("Framebuffer sizes do not match.");
    }

    var differentPixels = 0;
    var maxChannelDelta = 0;
    long totalChannelDelta = 0;
    for (var i = 0; i < baseline.Length; i++)
    {
        var baselineColor = baseline[i];
        var actualColor = actual[i];
        var redDelta = Math.Abs((int)((baselineColor >> 16) & 0xFF) - (int)((actualColor >> 16) & 0xFF));
        var greenDelta = Math.Abs((int)((baselineColor >> 8) & 0xFF) - (int)((actualColor >> 8) & 0xFF));
        var blueDelta = Math.Abs((int)(baselineColor & 0xFF) - (int)(actualColor & 0xFF));
        var pixelMax = Math.Max(redDelta, Math.Max(greenDelta, blueDelta));
        if (pixelMax > channelTolerance)
        {
            differentPixels++;
        }

        maxChannelDelta = Math.Max(maxChannelDelta, pixelMax);
        totalChannelDelta += redDelta + greenDelta + blueDelta;
    }

    return new FrameComparison(differentPixels, maxChannelDelta, totalChannelDelta);
}

static uint[] BuildDiffFramebuffer(ReadOnlySpan<uint> baseline, ReadOnlySpan<uint> actual, int channelTolerance)
{
    if (baseline.Length != actual.Length)
    {
        throw new ArgumentException("Framebuffer sizes do not match.");
    }

    var diff = new uint[baseline.Length];
    for (var i = 0; i < diff.Length; i++)
    {
        var baselineColor = baseline[i];
        var actualColor = actual[i];
        var redDelta = Math.Abs((int)((baselineColor >> 16) & 0xFF) - (int)((actualColor >> 16) & 0xFF));
        var greenDelta = Math.Abs((int)((baselineColor >> 8) & 0xFF) - (int)((actualColor >> 8) & 0xFF));
        var blueDelta = Math.Abs((int)(baselineColor & 0xFF) - (int)(actualColor & 0xFF));
        var pixelMax = Math.Max(redDelta, Math.Max(greenDelta, blueDelta));
        diff[i] = pixelMax > channelTolerance
            ? 0xFFFF_00FFu
            : 0xFF00_0000u | ((actualColor >> 2) & 0x003F_3F3Fu);
    }

    return diff;
}

internal sealed record RunOptions(
    long MaxSteps,
    double? MaxSeconds,
    bool Trace,
    GbaKey Keys,
    IReadOnlyList<KeyEvent> KeyEvents,
    IReadOnlyList<FrameKeyEvent> FrameKeyEvents,
    IReadOnlyList<FrameHashEvent> FrameHashEvents,
    IReadOnlyList<MemoryTriggerEvent> MemoryTriggerEvents,
    IReadOnlyList<FramePokeEvent> FramePokeEvents,
    IReadOnlyList<uint> WatchReads,
    IReadOnlyList<AddressRange> WatchReadRanges,
    IReadOnlyList<uint> WatchWrites,
    IReadOnlyList<AddressRange> WatchWriteRanges,
    int WatchLimit,
    bool StopOnInvalidPc,
    bool PrintState,
    bool TraceSwi,
    bool TraceIrq,
    int TraceIrqLimit,
    bool TraceDma,
    bool TraceEeprom,
    int TraceEepromLimit,
    bool TraceInput,
    IReadOnlyList<MemoryDump> Dumps,
    IReadOnlyList<InstructionDump> InstructionDumps,
    IReadOnlyList<AddressRange> TraceRanges,
    FrameRange? TraceFrameRange,
    int TraceTail,
    int TraceHitLimit,
    string? SaveFile,
    bool SaveReadOnly,
    uint? StopPc,
    int StopPcHit,
    IReadOnlyList<uint> SnapshotPcs,
    int SnapshotPcLimit,
    int PcSnapshotStackWords,
    int? StopFrame,
    int? DebugLayer,
    string? SnapshotCsv,
    string? PcSnapshotCsv,
    int SnapshotFrames,
    bool AlignRomEntry,
    string? AudioCsv,
    string? PsgCsv,
    string? AudioWav,
    int AudioSampleRate,
    double AudioGain);

internal sealed class InstructionTraceTail
{
    private readonly string[] _entries;
    private int _next;
    private int _count;

    public InstructionTraceTail(int capacity)
    {
        _entries = new string[capacity];
    }

    public void Record(string line)
    {
        _entries[_next] = line;
        _next = (_next + 1) % _entries.Length;
        if (_count < _entries.Length)
        {
            _count++;
        }
    }

    public void Dump()
    {
        if (_count == 0)
        {
            return;
        }

        Console.WriteLine($"Last {_count:N0} instructions before stop:");
        var start = (_next - _count + _entries.Length) % _entries.Length;
        for (var i = 0; i < _count; i++)
        {
            Console.WriteLine(_entries[(start + i) % _entries.Length]);
        }
    }
}

internal sealed class InstructionTraceLimiter
{
    private readonly int _limitPerPc;
    private readonly Dictionary<uint, int> _hits = [];

    public InstructionTraceLimiter(int limitPerPc)
    {
        _limitPerPc = limitPerPc;
    }

    public bool ShouldTrace(uint pc)
    {
        _hits.TryGetValue(pc, out var hits);
        if (hits >= _limitPerPc)
        {
            return false;
        }

        _hits[pc] = hits + 1;
        return true;
    }
}

internal readonly record struct KeyEvent(int Step, GbaKey Keys);

internal readonly record struct FrameKeyEvent(int Frame, GbaKey Keys);

internal readonly record struct FrameHashEvent(ulong Hash, GbaKey Keys, int DurationFrames, int MinFrame);

internal readonly record struct MemoryTriggerEvent(uint Address, int Bytes, uint Value, GbaKey Keys, int DurationFrames, int MinFrame);

internal readonly record struct FramePokeEvent(int Frame, uint Address, uint Value, int Bytes);

internal readonly record struct ObjectSummary(int Active, int Hidden, string FirstActive);

internal readonly record struct FrameStateRunResult(
    string Label,
    string Status,
    IReadOnlyList<FrameState> States,
    int Frame,
    long Steps,
    long Cycles,
    uint Pc);

internal readonly record struct FrameState(
    string Label,
    int Frame,
    long Step,
    long Cycles,
    uint Pc,
    uint Cpsr,
    string Mode,
    bool Thumb,
    uint Sp,
    uint Lr,
    ushort DisplayControl,
    ushort DisplayStatus,
    ushort VerticalCount,
    ushort InterruptEnable,
    ushort InterruptFlags,
    bool InterruptMasterEnable,
    ushort BiosInterruptFlags,
    ushort KeyInput,
    string PressedKeys,
    uint Global20,
    uint Global24,
    uint Global28,
    uint Global2C,
    uint IrqHandler,
    uint Helper100,
    uint Helper2Dc,
    ulong HelperHash,
    ulong WorklistHash,
    ulong FrameHash);

internal readonly record struct FrameStateDifference(int Frame, string Field, string NoBios, string RealBios);

internal readonly record struct CompatibilityPhase(string Name, RunOptions Options);

internal readonly record struct CompatibilityResult(
    int Index,
    string Phase,
    string Status,
    string Classification,
    int Frames,
    int Steps,
    long Cycles,
    double WallMilliseconds,
    double StepsPerSecond,
    double FramesPerSecond,
    double FramesPerMillionSteps,
    double CyclesPerFrame,
    double ProfileCpuPercent,
    double ProfileBusPercent,
    double ProfileSchedulerPercent,
    double ProfileCpuMilliseconds,
    double ProfileBusMilliseconds,
    double ProfileSchedulerMilliseconds,
    int DistinctFrames,
    int ChangedFrames,
    int LastChangedFrame,
    int StaticTailFrames,
    ulong FirstHash,
    ulong LastHash,
    uint Pc,
    uint Cpsr,
    string Mode,
    bool Thumb,
    ushort DisplayControl,
    ushort DisplayStatus,
    ushort VerticalCount,
    ushort InterruptEnable,
    ushort InterruptFlags,
    bool InterruptMasterEnable,
    int ActiveObjects,
    int HiddenObjects,
    string Title,
    string GameCode,
    string SaveType,
    int RomSize,
    string Error,
    string CapturePath,
    string Path)
{
    public static CompatibilityResult FromCartridge(
        int index,
        string phase,
        string status,
        string classification,
        int frames,
        int steps,
        long cycles,
        double wallMilliseconds,
        GbaStepProfile stepProfile,
        int distinctFrames,
        int changedFrames,
        int lastChangedFrame,
        int staticTailFrames,
        ulong firstHash,
        ulong lastHash,
        GbaSystem? gba,
        Cartridge? cartridge,
        string error,
        string capturePath,
        string path)
    {
        var oamSummary = gba is null ? new ObjectSummary(0, 0, "") : SummarizeOam(gba.Bus.ObjectAttributeMemory);
        var elapsedSeconds = wallMilliseconds / 1000.0;
        var stepsPerSecond = elapsedSeconds > 0 ? steps / elapsedSeconds : 0;
        var framesPerSecond = elapsedSeconds > 0 ? frames / elapsedSeconds : 0;
        var framesPerMillionSteps = steps > 0 ? frames * 1_000_000.0 / steps : 0;
        var cyclesPerFrame = frames > 0 ? cycles / (double)frames : 0;
        return new CompatibilityResult(
            index,
            phase,
            status,
            classification,
            frames,
            steps,
            cycles,
            wallMilliseconds,
            stepsPerSecond,
            framesPerSecond,
            framesPerMillionSteps,
            cyclesPerFrame,
            stepProfile.CpuPercent,
            stepProfile.BusPercent,
            stepProfile.SchedulerPercent,
            stepProfile.CpuMilliseconds,
            stepProfile.BusMilliseconds,
            stepProfile.SchedulerMilliseconds,
            distinctFrames,
            changedFrames,
            lastChangedFrame,
            staticTailFrames,
            firstHash,
            lastHash,
            gba?.Cpu.Pc ?? 0,
            gba?.Cpu.Cpsr ?? 0,
            gba?.Cpu.Mode.ToString() ?? "",
            gba?.Cpu.ThumbState ?? false,
            gba?.Bus.DisplayControl ?? 0,
            gba?.Bus.DisplayStatus ?? 0,
            gba?.Bus.VerticalCount ?? 0,
            gba?.Bus.InterruptEnable ?? 0,
            gba?.Bus.InterruptFlags ?? 0,
            gba?.Bus.InterruptMasterEnable ?? false,
            oamSummary.Active,
            oamSummary.Hidden,
            cartridge?.Header.Title ?? "",
            cartridge?.Header.GameCode ?? "",
            cartridge?.SaveType.ToString() ?? "",
            cartridge?.Rom.Length ?? 0,
            error,
            capturePath,
            path);
    }

    public string ToCsv()
        => string.Join(',', [
            Index.ToString(),
            Csv(Phase),
            Csv(Status),
            Csv(Classification),
            Frames.ToString(),
            Steps.ToString(),
            Cycles.ToString(),
            WallMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            StepsPerSecond.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
            FramesPerSecond.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            FramesPerMillionSteps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            CyclesPerFrame.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ProfileCpuPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ProfileBusPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ProfileSchedulerPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            DistinctFrames.ToString(),
            ChangedFrames.ToString(),
            LastChangedFrame.ToString(),
            StaticTailFrames.ToString(),
            $"0x{FirstHash:X16}",
            $"0x{LastHash:X16}",
            $"0x{Pc:X8}",
            $"0x{Cpsr:X8}",
            Csv(Mode),
            Thumb ? "1" : "0",
            $"0x{DisplayControl:X4}",
            $"0x{DisplayStatus:X4}",
            VerticalCount.ToString(),
            $"0x{InterruptEnable:X4}",
            $"0x{InterruptFlags:X4}",
            InterruptMasterEnable ? "1" : "0",
            ActiveObjects.ToString(),
            HiddenObjects.ToString(),
            Csv(Title),
            Csv(GameCode),
            Csv(SaveType),
            RomSize.ToString(),
            Csv(Error),
            Csv(CapturePath),
            Csv(Path)
        ]);

    public string ToProfileCsv()
        => string.Join(',', [
            Index.ToString(),
            Csv(Phase),
            Csv(Status),
            Csv(Classification),
            Frames.ToString(),
            Steps.ToString(),
            Cycles.ToString(),
            WallMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            StepsPerSecond.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
            FramesPerSecond.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            ProfileCpuMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ProfileBusMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ProfileSchedulerMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ProfileCpuPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ProfileBusPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ProfileSchedulerPercent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            Csv(Title),
            Csv(GameCode),
            Csv(Path)
        ]);

    private static ObjectSummary SummarizeOam(ReadOnlySpan<byte> oam)
    {
        var count = 0;
        var hidden = 0;
        var firstActive = "none";
        for (var sprite = 0; sprite < 128; sprite++)
        {
            var offset = sprite * 8;
            var attr0 = (ushort)(oam[offset] | (oam[offset + 1] << 8));
            var attr1 = (ushort)(oam[offset + 2] | (oam[offset + 3] << 8));
            var attr2 = (ushort)(oam[offset + 4] | (oam[offset + 5] << 8));
            var affineMode = (attr0 >> 8) & 0x3;
            if (affineMode == 2)
            {
                hidden++;
                continue;
            }

            if (count == 0)
            {
                firstActive = $"#{sprite}:a0=0x{attr0:X4};a1=0x{attr1:X4};a2=0x{attr2:X4}";
            }

            count++;
        }

        return new ObjectSummary(count, hidden, firstActive);
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

internal readonly record struct SaveProbeResult(
    int Index,
    string Status,
    string SaveType,
    int SaveSize,
    int WrittenBytes,
    int VerifiedBytes,
    string Title,
    string GameCode,
    string Error,
    string Path)
{
    public static SaveProbeResult FromCartridge(int index, string status, Cartridge? cartridge, int writtenBytes, int verifiedBytes, string error, string path)
    {
        return new SaveProbeResult(
            index,
            status,
            cartridge?.SaveType.ToString() ?? "",
            cartridge is null ? 0 : cartridge.SaveType switch
            {
                Gba.Core.Cartridges.SaveType.Eeprom => 8 * 1024,
                Gba.Core.Cartridges.SaveType.Flash128K => GbaMemoryMap.SramSize,
                Gba.Core.Cartridges.SaveType.Flash64K => 64 * 1024,
                Gba.Core.Cartridges.SaveType.Sram => 32 * 1024,
                _ => 0
            },
            writtenBytes,
            verifiedBytes,
            cartridge?.Header.Title ?? "",
            cartridge?.Header.GameCode ?? "",
            error,
            path);
    }

    public string ToCsv()
        => string.Join(',', [
            Index.ToString(),
            Csv(Status),
            Csv(SaveType),
            SaveSize.ToString(),
            WrittenBytes.ToString(),
            VerifiedBytes.ToString(),
            Csv(Title),
            Csv(GameCode),
            Csv(Error),
            Csv(Path)
        ]);

    private static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

internal sealed class InputEventState
{
    public int NextStepEvent { get; set; }

    public int NextFrameEvent { get; set; }

    public int NextFramePokeEvent { get; set; }

    public int LastHashFrame { get; set; } = -1;

    public int LastMemoryTriggerFrame { get; set; } = -1;

    public int? HashReleaseFrame { get; set; }

    public HashSet<int> FiredHashEvents { get; } = [];

    public HashSet<int> FiredMemoryTriggerEvents { get; } = [];
}

internal sealed class SnapshotWriter(StreamWriter writer) : IDisposable
{
    public StreamWriter Writer { get; } = writer;

    public int LastFrame { get; set; } = -1;

    public void Dispose() => Writer.Dispose();
}

internal sealed class PcSnapshotWriter(StreamWriter writer) : IDisposable
{
    public StreamWriter Writer { get; } = writer;

    public void Dispose() => Writer.Dispose();
}

internal sealed class AudioSampleWriter(StreamWriter writer) : IDisposable
{
    public StreamWriter Writer { get; } = writer;

    public long Count { get; set; }

    public void Dispose() => Writer.Dispose();
}

internal sealed class PsgSampleWriter(StreamWriter writer) : IDisposable
{
    public StreamWriter Writer { get; } = writer;

    public long Count { get; set; }

    public void Dispose() => Writer.Dispose();
}

internal sealed class AudioWavWriter : IDisposable
{
    private const int Channels = 2;
    private const int BitsPerSample = 16;
    private const int BytesPerSample = BitsPerSample / 8;
    private const int DataSizeOffset = 40;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly MixedPcmResampler _resampler;
    private bool _finished;
    private bool _disposed;

    public AudioWavWriter(string path, int sampleRate, double gain, GbaSystem gba)
    {
        ArgumentNullException.ThrowIfNull(gba);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be greater than zero.");
        }

        if (gain <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "Gain must be greater than zero.");
        }

        SampleRate = sampleRate;
        _stream = File.Create(path);
        _writer = new BinaryWriter(_stream, System.Text.Encoding.ASCII, leaveOpen: false);
        _resampler = new MixedPcmResampler(sampleRate, outputGain: gain);
        WriteHeader(dataBytes: 0);
        gba.Audio.SampleProduced += sample => _resampler.Process(sample, WriteFrame);
        gba.Audio.PsgSampleProduced += sample => _resampler.Process(sample, WriteFrame);
    }

    public int SampleRate { get; }

    public long FrameCount { get; private set; }

    public void Finish(long finalCycle)
    {
        if (_finished)
        {
            return;
        }

        _resampler.Process(new PsgPcmSample(finalCycle, 0, 0), WriteFrame);
        _finished = true;
        RewriteSizes();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_finished)
        {
            RewriteSizes();
        }

        _writer.Dispose();
        _disposed = true;
    }

    private void WriteFrame(short left, short right)
    {
        _writer.Write(left);
        _writer.Write(right);
        FrameCount++;
    }

    private void RewriteSizes()
    {
        var dataBytes = checked((uint)(FrameCount * Channels * BytesPerSample));
        var position = _stream.Position;
        _stream.Position = 4;
        _writer.Write(36u + dataBytes);
        _stream.Position = DataSizeOffset;
        _writer.Write(dataBytes);
        _stream.Position = position;
        _writer.Flush();
    }

    private void WriteHeader(uint dataBytes)
    {
        _writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write(36u + dataBytes);
        _writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        _writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        _writer.Write(16u);
        _writer.Write((ushort)1);
        _writer.Write((ushort)Channels);
        _writer.Write((uint)SampleRate);
        _writer.Write((uint)(SampleRate * Channels * BytesPerSample));
        _writer.Write((ushort)(Channels * BytesPerSample));
        _writer.Write((ushort)BitsPerSample);
        _writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        _writer.Write(dataBytes);
    }
}

internal readonly record struct MemoryDump(uint Address, uint Length);

internal readonly record struct InstructionDump(uint Address, uint Length, InstructionSet Set);

internal enum InstructionSet
{
    Arm,
    Thumb
}

internal readonly record struct AddressRange(uint Start, uint End)
{
    public bool Contains(uint address) => Start <= address && address <= End;
}

internal readonly record struct FrameRange(int Start, int End)
{
    public bool Contains(int frame) => Start <= frame && frame <= End;
}

internal readonly record struct FrameComparison(int DifferentPixels, int MaxChannelDelta, long TotalChannelDelta);
