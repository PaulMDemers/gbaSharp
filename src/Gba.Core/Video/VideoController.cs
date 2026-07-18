using Gba.Core.Memory;
using Gba.Core.Scheduling;
using System.Diagnostics;

namespace Gba.Core.Video;

public sealed class VideoController
{
    public const int VisibleLines = 160;
    public const int Width = 240;
    public const int Height = 160;
    public const int Pixels = Width * Height;
    public const int TotalLines = 228;
    public const int CyclesPerScanline = 1232;
    public const int HDrawCycles = 960;
    public const int HBlankCycles = CyclesPerScanline - HDrawCycles;

    private readonly MemoryBus _bus;
    private readonly Scheduler _scheduler;
    private readonly uint[] _framebuffer = new uint[Pixels];
    private readonly uint[] _secondFramebuffer = new uint[Pixels];
    private readonly uint[] _debugPreBlendFramebuffer = new uint[Pixels];
    private readonly uint[] _debugSecondTargetFramebuffer = new uint[Pixels];
    private readonly uint[][] _debugLayerFramebuffers =
    [
        new uint[Pixels],
        new uint[Pixels],
        new uint[Pixels],
        new uint[Pixels],
        new uint[Pixels]
    ];
    private readonly byte[] _debugTopLayers = new byte[Pixels];
    private readonly byte[] _debugSecondLayers = new byte[Pixels];
    private readonly AffineDebugSample[][] _debugAffineSamples =
    [
        new AffineDebugSample[Pixels],
        new AffineDebugSample[Pixels]
    ];
    private readonly RegularBgDebugSample[][] _debugRegularBgSamples =
    [
        new RegularBgDebugSample[Pixels],
        new RegularBgDebugSample[Pixels],
        new RegularBgDebugSample[Pixels],
        new RegularBgDebugSample[Pixels]
    ];
    private readonly bool[] _objectWindow = new bool[Pixels];
    private readonly bool[] _semiTransparentObject = new bool[Pixels];
    private readonly int[] _affineCurrentX = new int[2];
    private readonly int[] _affineCurrentY = new int[2];
    private bool _debugRenderingEnabled;
    private VideoRenderProfile _renderProfile;
    private int _line;
    private long _scanlineStartCycle;

    public VideoController(MemoryBus bus, Scheduler scheduler)
    {
        _bus = bus;
        _scheduler = scheduler;
        _bus.AddIoWriteObserver(OnIoWrite);
    }

    public int CurrentLine => _line;

    public ReadOnlySpan<uint> Framebuffer => _framebuffer;

    public bool RenderProfilingEnabled { get; set; }

    public bool DebugRenderingEnabled
    {
        get => _debugRenderingEnabled;
        set
        {
            if (_debugRenderingEnabled == value)
            {
                return;
            }

            _debugRenderingEnabled = value;
            if (value)
            {
                ClearDebugLayerFramebuffers();
                ClearDebugCompositionFramebuffers();
                ClearDebugAffineSamples();
                ClearDebugRegularBgSamples();
            }
        }
    }

    public int CyclesUntilNextVBlankStart
    {
        get
        {
            var elapsedInScanline = (int)(_scheduler.Now - _scanlineStartCycle);
            var targetLine = _line < VisibleLines ? VisibleLines : TotalLines + VisibleLines;
            var cycles = (targetLine - _line) * CyclesPerScanline - elapsedInScanline;
            return Math.Max(0, cycles);
        }
    }

    public event Action? VBlankStarted;

    public event Action? HBlankStarted;

    public event Action<bool>? DisplayStartDmaRequested;

    public VideoRenderProfile RenderProfile => _renderProfile;

    public void ResetRenderProfile() => _renderProfile = default;

    public uint[] RenderDebugLayer(int layer)
    {
        if (layer is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), "Layer must be 0-3 for BG or 4 for OBJ.");
        }

        return _debugLayerFramebuffers[layer].ToArray();
    }

    public uint[] RenderDebugPreBlend() => _debugPreBlendFramebuffer.ToArray();

    public uint[] RenderDebugSecondTarget() => _debugSecondTargetFramebuffer.ToArray();

    public uint[] RenderDebugTopLayerMap() => RenderLayerMap(_debugTopLayers);

    public uint[] RenderDebugSecondLayerMap() => RenderLayerMap(_debugSecondLayers);

    public AffineDebugSample[] RenderDebugAffineSamples(int bg)
    {
        if (bg is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(bg), "Affine debug samples are available only for BG2/BG3.");
        }

        return _debugAffineSamples[bg - 2].ToArray();
    }

    public RegularBgDebugSample[] RenderDebugRegularBgSamples(int bg)
    {
        if (bg is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(bg), "Regular BG debug samples are available for BG0-BG3.");
        }

        return _debugRegularBgSamples[bg].ToArray();
    }

    public void Reset()
    {
        _line = 0;
        if (_debugRenderingEnabled)
        {
            ClearDebugLayerFramebuffers();
            ClearDebugCompositionFramebuffers();
            ClearDebugAffineSamples();
            ClearDebugRegularBgSamples();
        }

        ReloadAffineReference(2);
        ReloadAffineReference(3);
        BeginScanline();
    }

    public void ResetSkippedBiosHandoff(int? lineOverride = null, int? cyclesUntilFirstVideoEventOverride = null)
    {
        const int defaultBiosHandoffLine = 0x7E;
        const int defaultCyclesUntilFirstVideoEvent = 117;

        _line = lineOverride ?? defaultBiosHandoffLine;
        if (_debugRenderingEnabled)
        {
            ClearDebugLayerFramebuffers();
            ClearDebugCompositionFramebuffers();
            ClearDebugAffineSamples();
            ClearDebugRegularBgSamples();
        }

        ReloadAffineReference(2);
        ReloadAffineReference(3);
        BeginScanline(cyclesUntilFirstVideoEventOverride ?? defaultCyclesUntilFirstVideoEvent);
    }

    private void BeginScanline(int cyclesUntilHBlank = HDrawCycles)
    {
        _scanlineStartCycle = _scheduler.Now - (HDrawCycles - cyclesUntilHBlank);
        _bus.VerticalCount = (ushort)_line;

        var vblank = _line >= VisibleLines;
        var vcount = _line == _bus.DisplayVCountSetting;
        ushort setMask = 0;
        ushort clearMask = IoRegisters.DispstatHBlank;

        if (vblank)
        {
            setMask |= IoRegisters.DispstatVBlank;
        }
        else
        {
            clearMask |= IoRegisters.DispstatVBlank;
        }

        if (vcount)
        {
            setMask |= IoRegisters.DispstatVCount;
        }
        else
        {
            clearMask |= IoRegisters.DispstatVCount;
        }

        _bus.SetDisplayStatusFlags(setMask, clearMask);

        if (_line == VisibleLines && IsDispstatIrqEnabled(IoRegisters.DispstatVBlankIrq))
        {
            _bus.RequestInterrupt(IoRegisters.InterruptVBlank);
        }

        if (_line == VisibleLines)
        {
            VBlankStarted?.Invoke();
        }

        if (_line == 0)
        {
            ReloadAffineReference(2);
            ReloadAffineReference(3);
        }

        if (vcount && IsDispstatIrqEnabled(IoRegisters.DispstatVCountIrq))
        {
            _bus.RequestInterrupt(IoRegisters.InterruptVCount);
        }

        _scheduler.Schedule(cyclesUntilHBlank, EnterHBlank);
    }

    private void EnterHBlank()
    {
        if (_line < VisibleLines)
        {
            RenderScanline(_line);
            AdvanceAffineReferences();
        }

        _bus.SetDisplayStatusFlags(IoRegisters.DispstatHBlank, 0);
        if (_line < VisibleLines)
        {
            HBlankStarted?.Invoke();
        }

        if (_line >= 2 && _line < VisibleLines + 2)
        {
            DisplayStartDmaRequested?.Invoke(_line == VisibleLines + 1);
        }

        if (IsDispstatIrqEnabled(IoRegisters.DispstatHBlankIrq))
        {
            _bus.RequestInterrupt(IoRegisters.InterruptHBlank);
        }

        _scheduler.Schedule(HBlankCycles, EndScanline);
    }

    private void EndScanline()
    {
        _line++;
        if (_line == TotalLines)
        {
            _line = 0;
        }

        BeginScanline();
    }

    private bool IsDispstatIrqEnabled(ushort irqBit) => (_bus.DisplayStatus & irqBit) != 0;

    private void OnIoWrite(uint address, int bytes)
    {
        var end = address + (uint)Math.Max(bytes, 1) - 1;
        if (Overlaps(address, end, IoRegisters.BG2X, IoRegisters.BG2X + 3)
            || Overlaps(address, end, IoRegisters.BG2Y, IoRegisters.BG2Y + 3))
        {
            ReloadAffineReference(2);
        }

        if (Overlaps(address, end, IoRegisters.BG3X, IoRegisters.BG3X + 3)
            || Overlaps(address, end, IoRegisters.BG3Y, IoRegisters.BG3Y + 3))
        {
            ReloadAffineReference(3);
        }
    }

    private void ReloadAffineReference(int bg)
    {
        var index = bg - 2;
        var baseAddress = bg == 2 ? IoRegisters.BG2X : IoRegisters.BG3X;
        _affineCurrentX[index] = ReadSignedFixed28(baseAddress);
        _affineCurrentY[index] = ReadSignedFixed28(baseAddress + 4);
    }

    private void AdvanceAffineReferences()
    {
        _affineCurrentX[0] += ReadSignedFixed8(IoRegisters.BG2PB);
        _affineCurrentY[0] += ReadSignedFixed8(IoRegisters.BG2PD);
        _affineCurrentX[1] += ReadSignedFixed8(IoRegisters.BG3PB);
        _affineCurrentY[1] += ReadSignedFixed8(IoRegisters.BG3PD);
    }

    private static bool Overlaps(uint start, uint end, uint rangeStart, uint rangeEnd)
        => start <= rangeEnd && end >= rangeStart;

    private void RenderFrame()
    {
        if ((_bus.DisplayControl & (1 << 7)) != 0)
        {
            Array.Fill(_framebuffer, 0xFFFF_FFFFu);
            return;
        }

        switch (_bus.DisplayControl & 0x7)
        {
            case 0:
                RenderMode0();
                break;

            case 1:
                RenderMode1();
                break;

            case 2:
                RenderMode2();
                break;

            case 3:
                RenderMode3();
                break;

            case 4:
                RenderMode4();
                break;

            case 5:
                RenderMode5();
                break;

            default:
                Array.Clear(_framebuffer);
                break;
        }
    }

    private void RenderScanline(int y)
    {
        if (RenderProfilingEnabled)
        {
            RenderScanlineProfiled(y);
            return;
        }

        if (_debugRenderingEnabled)
        {
            ClearDebugLayerRows(y);
        }

        if ((_bus.DisplayControl & (1 << 7)) != 0)
        {
            _framebuffer.AsSpan(y * Width, Width).Fill(0xFFFF_FFFFu);
            return;
        }

        switch (_bus.DisplayControl & 0x7)
        {
            case 0:
                RenderLayeredScanline(y, tileBackgrounds: 0b1111, affineBackgrounds: 0);
                break;

            case 1:
                RenderLayeredScanline(y, tileBackgrounds: 0b0011, affineBackgrounds: 0b0100);
                break;

            case 2:
                RenderLayeredScanline(y, tileBackgrounds: 0, affineBackgrounds: 0b1100);
                break;

            case 3:
                RenderBitmapLayeredScanline(y, 3);
                break;

            case 4:
                RenderBitmapLayeredScanline(y, 4);
                break;

            case 5:
                RenderBitmapLayeredScanline(y, 5);
                break;

            default:
                _framebuffer.AsSpan(y * Width, Width).Clear();
                break;
        }
    }

    private void RenderScanlineProfiled(int y)
    {
        var profileStart = Stopwatch.GetTimestamp();
        if (_debugRenderingEnabled)
        {
            ClearDebugLayerRows(y);
        }

        if ((_bus.DisplayControl & (1 << 7)) != 0)
        {
            _framebuffer.AsSpan(y * Width, Width).Fill(0xFFFF_FFFFu);
            RecordRenderScanline(profileStart);
            return;
        }

        switch (_bus.DisplayControl & 0x7)
        {
            case 0:
                RenderLayeredScanlineProfiled(y, tileBackgrounds: 0b1111, affineBackgrounds: 0);
                break;

            case 1:
                RenderLayeredScanlineProfiled(y, tileBackgrounds: 0b0011, affineBackgrounds: 0b0100);
                break;

            case 2:
                RenderLayeredScanlineProfiled(y, tileBackgrounds: 0, affineBackgrounds: 0b1100);
                break;

            case 3:
                RenderBitmapLayeredScanlineProfiled(y, 3);
                break;

            case 4:
                RenderBitmapLayeredScanlineProfiled(y, 4);
                break;

            case 5:
                RenderBitmapLayeredScanlineProfiled(y, 5);
                break;

            default:
                _framebuffer.AsSpan(y * Width, Width).Clear();
                break;
        }

        RecordRenderScanline(profileStart);
    }

    private void RenderDebugLayerScanline(int y, int layer)
    {
        if ((_bus.DisplayControl & (1 << 7)) != 0)
        {
            _framebuffer.AsSpan(y * Width, Width).Fill(0xFFFF_FFFFu);
            return;
        }

        switch (_bus.DisplayControl & 0x7)
        {
            case 0:
                RenderLayeredScanline(y, tileBackgrounds: 0b1111, affineBackgrounds: 0, debugLayer: layer);
                break;

            case 1:
                RenderLayeredScanline(y, tileBackgrounds: 0b0011, affineBackgrounds: 0b0100, debugLayer: layer);
                break;

            case 2:
                RenderLayeredScanline(y, tileBackgrounds: 0, affineBackgrounds: 0b1100, debugLayer: layer);
                break;

            case 3:
                RenderBitmapLayeredScanline(y, 3, layer);
                break;

            case 4:
                RenderBitmapLayeredScanline(y, 4, layer);
                break;

            case 5:
                RenderBitmapLayeredScanline(y, 5, layer);
                break;

            default:
                _framebuffer.AsSpan(y * Width, Width).Fill(0xFF00_0000u);
                break;
        }
    }

    private void RenderMode0()
        => RenderLayered(tileBackgrounds: 0b1111, affineBackgrounds: 0);

    private void RenderMode1()
        => RenderLayered(tileBackgrounds: 0b0011, affineBackgrounds: 0b0100);

    private void RenderMode2()
        => RenderLayered(tileBackgrounds: 0, affineBackgrounds: 0b1100);

    private void RenderLayered(int tileBackgrounds, int affineBackgrounds)
    {
        var backdrop = ReadPaletteColor(0);
        Array.Fill(_framebuffer, backdrop);

        Span<byte> priorities = stackalloc byte[Pixels];
        priorities.Fill(4);
        Span<byte> layers = stackalloc byte[Pixels];
        layers.Fill(5);
        Span<byte> secondLayers = stackalloc byte[Pixels];
        secondLayers.Fill(5);
        Array.Fill(_secondFramebuffer, backdrop);
        Array.Clear(_objectWindow);
        Array.Clear(_semiTransparentObject);
        if (IsObjectWindowEnabled())
        {
            RenderObjectWindow();
        }

        for (var priority = 3; priority >= 0; priority--)
        {
            for (var bg = 3; bg >= 0; bg--)
            {
                if ((_bus.DisplayControl & (1 << (8 + bg))) == 0)
                {
                    continue;
                }

                var control = _bus.PeekIo16(IoRegisters.BG0CNT + (uint)(bg * 2));
                if ((control & 0x3) != priority)
                {
                    continue;
                }

                if ((tileBackgrounds & (1 << bg)) != 0)
                {
                    RenderRegularBackground(bg, control, priorities, layers, secondLayers);
                }
                else if ((affineBackgrounds & (1 << bg)) != 0)
                {
                    RenderAffineBackground(bg, control, priorities, layers, secondLayers);
                }
            }

            if ((_bus.DisplayControl & (1 << 12)) != 0)
            {
                RenderSpritesForPriority(priority, priorities, layers, secondLayers);
            }
        }

        CaptureDebugCompositionFrame(layers, secondLayers);
        ApplyBlendEffects(layers, secondLayers);
    }

    private void RenderLayeredScanline(int y, int tileBackgrounds, int affineBackgrounds, int? debugLayer = null)
    {
        var backdrop = debugLayer.HasValue ? 0xFF00_0000u : ReadPaletteColor(0);
        var rowOffset = y * Width;
        _framebuffer.AsSpan(rowOffset, Width).Fill(backdrop);

        Span<byte> priorities = stackalloc byte[Width];
        priorities.Fill(4);
        Span<byte> layers = stackalloc byte[Width];
        layers.Fill(5);
        Span<byte> secondLayers = stackalloc byte[Width];
        secondLayers.Fill(5);
        _secondFramebuffer.AsSpan(rowOffset, Width).Fill(backdrop);
        Array.Clear(_objectWindow, rowOffset, Width);
        Array.Clear(_semiTransparentObject, rowOffset, Width);
        if (IsObjectWindowEnabled())
        {
            RenderObjectWindowScanline(y);
        }

        for (var priority = 3; priority >= 0; priority--)
        {
            for (var bg = 3; bg >= 0; bg--)
            {
                if (debugLayer is { } selectedLayer && selectedLayer != bg)
                {
                    continue;
                }

                if ((_bus.DisplayControl & (1 << (8 + bg))) == 0)
                {
                    continue;
                }

                var control = _bus.PeekIo16(IoRegisters.BG0CNT + (uint)(bg * 2));
                if ((control & 0x3) != priority)
                {
                    continue;
                }

                if ((tileBackgrounds & (1 << bg)) != 0)
                {
                    RenderRegularBackgroundScanline(bg, control, y, priorities, layers, secondLayers);
                }
                else if ((affineBackgrounds & (1 << bg)) != 0)
                {
                    RenderAffineBackgroundScanline(bg, control, y, priorities, layers, secondLayers);
                }
            }

            if ((_bus.DisplayControl & (1 << 12)) != 0 && debugLayer is null or 4)
            {
                RenderSpritesForPriorityScanline(priority, y, priorities, layers, secondLayers);
            }
        }

        if (!debugLayer.HasValue)
        {
            CaptureDebugCompositionScanline(y, layers, secondLayers);
            ApplyBlendEffectsScanline(y, layers, secondLayers);
        }
    }

    private void RenderLayeredScanlineProfiled(int y, int tileBackgrounds, int affineBackgrounds, int? debugLayer = null)
    {
        var backdrop = debugLayer.HasValue ? 0xFF00_0000u : ReadPaletteColor(0);
        var rowOffset = y * Width;
        _framebuffer.AsSpan(rowOffset, Width).Fill(backdrop);

        Span<byte> priorities = stackalloc byte[Width];
        priorities.Fill(4);
        Span<byte> layers = stackalloc byte[Width];
        layers.Fill(5);
        Span<byte> secondLayers = stackalloc byte[Width];
        secondLayers.Fill(5);
        _secondFramebuffer.AsSpan(rowOffset, Width).Fill(backdrop);
        Array.Clear(_objectWindow, rowOffset, Width);
        Array.Clear(_semiTransparentObject, rowOffset, Width);
        if (IsObjectWindowEnabled())
        {
            var start = Stopwatch.GetTimestamp();
            RenderObjectWindowScanline(y);
            _renderProfile.ObjectWindowTicks += Stopwatch.GetTimestamp() - start;
        }

        for (var priority = 3; priority >= 0; priority--)
        {
            for (var bg = 3; bg >= 0; bg--)
            {
                if (debugLayer is { } selectedLayer && selectedLayer != bg)
                {
                    continue;
                }

                if ((_bus.DisplayControl & (1 << (8 + bg))) == 0)
                {
                    continue;
                }

                var control = _bus.PeekIo16(IoRegisters.BG0CNT + (uint)(bg * 2));
                if ((control & 0x3) != priority)
                {
                    continue;
                }

                if ((tileBackgrounds & (1 << bg)) != 0)
                {
                    var start = Stopwatch.GetTimestamp();
                    RenderRegularBackgroundScanline(bg, control, y, priorities, layers, secondLayers);
                    _renderProfile.RegularBackgroundTicks += Stopwatch.GetTimestamp() - start;
                }
                else if ((affineBackgrounds & (1 << bg)) != 0)
                {
                    var start = Stopwatch.GetTimestamp();
                    RenderAffineBackgroundScanline(bg, control, y, priorities, layers, secondLayers);
                    _renderProfile.AffineBackgroundTicks += Stopwatch.GetTimestamp() - start;
                }
            }

            if ((_bus.DisplayControl & (1 << 12)) != 0 && debugLayer is null or 4)
            {
                var start = Stopwatch.GetTimestamp();
                RenderSpritesForPriorityScanline(priority, y, priorities, layers, secondLayers);
                _renderProfile.SpriteTicks += Stopwatch.GetTimestamp() - start;
            }
        }

        if (!debugLayer.HasValue)
        {
            CaptureDebugCompositionScanline(y, layers, secondLayers);
            var start = Stopwatch.GetTimestamp();
            ApplyBlendEffectsScanline(y, layers, secondLayers);
            _renderProfile.BlendTicks += Stopwatch.GetTimestamp() - start;
        }
    }

    private void RenderBitmapLayeredScanline(int y, int mode, int? debugLayer = null)
    {
        var backdrop = debugLayer.HasValue ? 0xFF00_0000u : ReadPaletteColor(0);
        var rowOffset = y * Width;
        _framebuffer.AsSpan(rowOffset, Width).Fill(backdrop);

        Span<byte> priorities = stackalloc byte[Width];
        priorities.Fill(4);
        Span<byte> layers = stackalloc byte[Width];
        layers.Fill(5);
        Span<byte> secondLayers = stackalloc byte[Width];
        secondLayers.Fill(5);
        _secondFramebuffer.AsSpan(rowOffset, Width).Fill(backdrop);
        Array.Clear(_objectWindow, rowOffset, Width);
        Array.Clear(_semiTransparentObject, rowOffset, Width);
        if (IsObjectWindowEnabled())
        {
            RenderObjectWindowScanline(y);
        }

        var bg2Enabled = (_bus.DisplayControl & (1 << 10)) != 0;
        var bg2Control = _bus.PeekIo16(IoRegisters.BG2CNT);
        var bg2Priority = (byte)(bg2Control & 0x3);
        for (var priority = 3; priority >= 0; priority--)
        {
            if (bg2Enabled
                && bg2Priority == priority
                && debugLayer is null or 2)
            {
                RenderBitmapBackgroundScanline(mode, y, bg2Priority, priorities, layers, secondLayers);
            }

            if ((_bus.DisplayControl & (1 << 12)) != 0 && debugLayer is null or 4)
            {
                RenderSpritesForPriorityScanline(priority, y, priorities, layers, secondLayers);
            }
        }

        if (!debugLayer.HasValue)
        {
            CaptureDebugCompositionScanline(y, layers, secondLayers);
            ApplyBlendEffectsScanline(y, layers, secondLayers);
        }
    }

    private void RenderBitmapLayeredScanlineProfiled(int y, int mode)
    {
        var backdrop = ReadPaletteColor(0);
        var rowOffset = y * Width;
        _framebuffer.AsSpan(rowOffset, Width).Fill(backdrop);

        Span<byte> priorities = stackalloc byte[Width];
        priorities.Fill(4);
        Span<byte> layers = stackalloc byte[Width];
        layers.Fill(5);
        Span<byte> secondLayers = stackalloc byte[Width];
        secondLayers.Fill(5);
        _secondFramebuffer.AsSpan(rowOffset, Width).Fill(backdrop);
        Array.Clear(_objectWindow, rowOffset, Width);
        Array.Clear(_semiTransparentObject, rowOffset, Width);
        if (IsObjectWindowEnabled())
        {
            var start = Stopwatch.GetTimestamp();
            RenderObjectWindowScanline(y);
            _renderProfile.ObjectWindowTicks += Stopwatch.GetTimestamp() - start;
        }

        var bg2Enabled = (_bus.DisplayControl & (1 << 10)) != 0;
        var bg2Control = _bus.PeekIo16(IoRegisters.BG2CNT);
        var bg2Priority = (byte)(bg2Control & 0x3);
        for (var priority = 3; priority >= 0; priority--)
        {
            if (bg2Enabled && bg2Priority == priority)
            {
                var start = Stopwatch.GetTimestamp();
                RenderBitmapBackgroundScanline(mode, y, bg2Priority, priorities, layers, secondLayers);
                _renderProfile.BitmapTicks += Stopwatch.GetTimestamp() - start;
            }

            if ((_bus.DisplayControl & (1 << 12)) != 0)
            {
                var start = Stopwatch.GetTimestamp();
                RenderSpritesForPriorityScanline(priority, y, priorities, layers, secondLayers);
                _renderProfile.SpriteTicks += Stopwatch.GetTimestamp() - start;
            }
        }

        CaptureDebugCompositionScanline(y, layers, secondLayers);
        var blendStart = Stopwatch.GetTimestamp();
        ApplyBlendEffectsScanline(y, layers, secondLayers);
        _renderProfile.BlendTicks += Stopwatch.GetTimestamp() - blendStart;
    }

    private void RenderBitmapBackgroundScanline(
        int mode,
        int y,
        byte priority,
        Span<byte> priorities,
        Span<byte> layers,
        Span<byte> secondLayers)
    {
        var rowOffset = y * Width;
        for (var x = 0; x < Width; x++)
        {
            if (priority > priorities[x] || !IsLayerVisibleAtPixel(2, x, y))
            {
                continue;
            }

            if (!TryReadBitmapPixel(mode, x, y, out var color))
            {
                continue;
            }

            SetLayerPixel(rowOffset + x, x, priority, 2, color, priorities, layers, secondLayers);
        }
    }

    private void RenderAffineBackground(int bg, ushort control, Span<byte> priorities, Span<byte> layers, Span<byte> secondLayers)
    {
        var charBase = ((control >> 2) & 0x3) * 0x4000;
        var screenBase = ((control >> 8) & 0x1F) * 0x800;
        var wrap = (control & (1 << 13)) != 0;
        var sizePixels = 128 << ((control >> 14) & 0x3);
        var priority = (byte)(control & 0x3);
        var parameterBase = bg == 2 ? IoRegisters.BG2PA : IoRegisters.BG3PA;
        var pa = ReadSignedFixed8(parameterBase);
        var pb = ReadSignedFixed8(parameterBase + 2);
        var pc = ReadSignedFixed8(parameterBase + 4);
        var pd = ReadSignedFixed8(parameterBase + 6);
        var originX = ReadSignedFixed20(parameterBase + 8);
        var originY = ReadSignedFixed20(parameterBase + 12);
        var mosaic = IsBackgroundMosaicEnabled(control);
        var mosaicH = mosaic ? GetBackgroundMosaicHorizontalSize() : 1;
        var mosaicV = mosaic ? GetBackgroundMosaicVerticalSize() : 1;

        for (var y = 0; y < Height; y++)
        {
            var mosaicY = mosaic ? y - y % mosaicV : y;
            var rowX = originX + pb * mosaicY;
            var rowY = originY + pd * mosaicY;
            for (var x = 0; x < Width; x++)
            {
                var mosaicX = mosaic ? x - x % mosaicH : x;
                var currentX = rowX + pa * mosaicX;
                var currentY = rowY + pc * mosaicX;
                var sourceX = currentX >> 8;
                var sourceY = currentY >> 8;

                if (wrap)
                {
                    sourceX = PositiveModulo(sourceX, sizePixels);
                    sourceY = PositiveModulo(sourceY, sizePixels);
                }
                else if (sourceX < 0 || sourceY < 0 || sourceX >= sizePixels || sourceY >= sizePixels)
                {
                    continue;
                }

                var tileX = sourceX / 8;
                var tileY = sourceY / 8;
                var mapWidthTiles = sizePixels / 8;
                var mapOffset = screenBase + tileY * mapWidthTiles + tileX;
                var tileNumber = ReadBgVram8(mapOffset);
                var tileOffset = charBase + tileNumber * 64 + (sourceY & 7) * 8 + (sourceX & 7);
                var paletteIndex = ReadBgVram8(tileOffset);
                var pixel = y * Width + x;
                if (_debugRenderingEnabled)
                {
                    RecordAffineDebugSample(
                        bg,
                        pixel,
                        control,
                        currentX,
                        currentY,
                        sourceX,
                        sourceY,
                        tileX,
                        tileY,
                        mapOffset,
                        tileNumber,
                        tileOffset,
                        paletteIndex,
                        pa,
                        pb,
                        pc,
                        pd,
                        originX,
                        originY);
                }
                if (paletteIndex == 0 || priority > priorities[pixel] || !IsLayerVisibleAtPixel(bg, x, y))
                {
                    continue;
                }

                SetLayerPixel(pixel, priority, (byte)bg, ReadPaletteColor(paletteIndex), priorities, layers, secondLayers);
            }
        }
    }

    private void RenderAffineBackgroundScanline(int bg, ushort control, int y, Span<byte> priorities, Span<byte> layers, Span<byte> secondLayers)
    {
        var charBase = ((control >> 2) & 0x3) * 0x4000;
        var screenBase = ((control >> 8) & 0x1F) * 0x800;
        var wrap = (control & (1 << 13)) != 0;
        var sizePixels = 128 << ((control >> 14) & 0x3);
        var priority = (byte)(control & 0x3);
        var parameterBase = bg == 2 ? IoRegisters.BG2PA : IoRegisters.BG3PA;
        var pa = ReadSignedFixed8(parameterBase);
        var pb = ReadSignedFixed8(parameterBase + 2);
        var pc = ReadSignedFixed8(parameterBase + 4);
        var pd = ReadSignedFixed8(parameterBase + 6);
        var currentX = _affineCurrentX[bg - 2];
        var currentY = _affineCurrentY[bg - 2];
        var mapWidthTiles = sizePixels / 8;
        var mosaic = IsBackgroundMosaicEnabled(control);
        var mosaicH = mosaic ? GetBackgroundMosaicHorizontalSize() : 1;
        var mosaicV = mosaic ? GetBackgroundMosaicVerticalSize() : 1;
        var mosaicYDelta = mosaic ? y - y % mosaicV - y : 0;
        var rowX = currentX + pb * mosaicYDelta;
        var rowY = currentY + pd * mosaicYDelta;

        for (var x = 0; x < Width; x++)
        {
            var mosaicX = mosaic ? x - x % mosaicH : x;
            var pixelX = rowX + pa * mosaicX;
            var pixelY = rowY + pc * mosaicX;
            var sourceX = pixelX >> 8;
            var sourceY = pixelY >> 8;

            if (wrap)
            {
                sourceX = PositiveModulo(sourceX, sizePixels);
                sourceY = PositiveModulo(sourceY, sizePixels);
            }
            else if (sourceX < 0 || sourceY < 0 || sourceX >= sizePixels || sourceY >= sizePixels)
            {
                continue;
            }

            var tileX = sourceX / 8;
            var tileY = sourceY / 8;
            var mapOffset = screenBase + tileY * mapWidthTiles + tileX;
            var tileNumber = ReadBgVram8(mapOffset);
            var tileOffset = charBase + tileNumber * 64 + (sourceY & 7) * 8 + (sourceX & 7);
            var paletteIndex = ReadBgVram8(tileOffset);
            if (_debugRenderingEnabled)
            {
                RecordAffineDebugSample(
                    bg,
                    y * Width + x,
                    control,
                    pixelX,
                    pixelY,
                    sourceX,
                    sourceY,
                    tileX,
                    tileY,
                    mapOffset,
                    tileNumber,
                    tileOffset,
                    paletteIndex,
                    pa,
                    pb,
                    pc,
                    pd,
                    currentX,
                    currentY);
            }
            if (paletteIndex == 0 || priority > priorities[x] || !IsLayerVisibleAtPixel(bg, x, y))
            {
                continue;
            }

            SetLayerPixel(y * Width + x, x, priority, (byte)bg, ReadPaletteColor(paletteIndex), priorities, layers, secondLayers);
        }
    }

    private void RenderRegularBackground(int bg, ushort control, Span<byte> priorities, Span<byte> layers, Span<byte> secondLayers)
    {
        var charBase = ((control >> 2) & 0x3) * 0x4000;
        var eightBitColor = (control & (1 << 7)) != 0;
        var screenBase = ((control >> 8) & 0x1F) * 0x800;
        var size = (control >> 14) & 0x3;
        var widthTiles = (size & 1) != 0 ? 64 : 32;
        var heightTiles = (size & 2) != 0 ? 64 : 32;
        var width = widthTiles * 8;
        var height = heightTiles * 8;
        var hofs = _bus.PeekIo16(IoRegisters.BG0HOFS + (uint)(bg * 4)) & 0x1FF;
        var vofs = _bus.PeekIo16(IoRegisters.BG0VOFS + (uint)(bg * 4)) & 0x1FF;
        var priority = (byte)(control & 0x3);
        var mosaic = IsBackgroundMosaicEnabled(control);
        var mosaicH = mosaic ? GetBackgroundMosaicHorizontalSize() : 1;
        var mosaicV = mosaic ? GetBackgroundMosaicVerticalSize() : 1;

        for (var y = 0; y < Height; y++)
        {
            var mosaicY = mosaic ? y - y % mosaicV : y;
            var bgY = (mosaicY + vofs) % height;
            var tileY = bgY / 8;
            var inTileY = bgY & 7;

            for (var x = 0; x < Width; x++)
            {
                var mosaicX = mosaic ? x - x % mosaicH : x;
                var bgX = (mosaicX + hofs) % width;
                var tileX = bgX / 8;
                var screenOffset = screenBase + GetRegularScreenEntryOffset(tileX, tileY, widthTiles, heightTiles);
                var entry = ReadVram16(screenOffset);
                var paletteIndex = GetTilePaletteIndex(charBase, entry, bgX & 7, inTileY, eightBitColor);
                var pixel = y * Width + x;
                if (_debugRenderingEnabled)
                {
                    RecordRegularBgDebugSample(
                        bg,
                        pixel,
                        control,
                        bgX,
                        bgY,
                        tileX,
                        tileY,
                        screenOffset,
                        entry,
                        paletteIndex,
                        hofs,
                        vofs);
                }
                if (paletteIndex == 0 || priority > priorities[pixel] || !IsLayerVisibleAtPixel(bg, x, y))
                {
                    continue;
                }

                SetLayerPixel(pixel, priority, (byte)bg, ReadPaletteColor(paletteIndex), priorities, layers, secondLayers);
            }
        }
    }

    private void RenderRegularBackgroundScanline(int bg, ushort control, int y, Span<byte> priorities, Span<byte> layers, Span<byte> secondLayers)
    {
        var charBase = ((control >> 2) & 0x3) * 0x4000;
        var eightBitColor = (control & (1 << 7)) != 0;
        var screenBase = ((control >> 8) & 0x1F) * 0x800;
        var size = (control >> 14) & 0x3;
        var widthTiles = (size & 1) != 0 ? 64 : 32;
        var heightTiles = (size & 2) != 0 ? 64 : 32;
        var width = widthTiles * 8;
        var height = heightTiles * 8;
        var widthMask = width - 1;
        var heightMask = height - 1;
        var hofs = _bus.PeekIo16(IoRegisters.BG0HOFS + (uint)(bg * 4)) & 0x1FF;
        var vofs = _bus.PeekIo16(IoRegisters.BG0VOFS + (uint)(bg * 4)) & 0x1FF;
        var priority = (byte)(control & 0x3);
        var mosaic = IsBackgroundMosaicEnabled(control);
        var rowOffset = y * Width;
        var mosaicV = mosaic ? GetBackgroundMosaicVerticalSize() : 1;
        var mosaicY = mosaic ? y - y % mosaicV : y;
        var bgY = (mosaicY + vofs) & heightMask;
        var tileY = bgY >> 3;
        var inTileY = bgY & 7;

        if (!mosaic)
        {
            var bgX = hofs & widthMask;
            for (var x = 0; x < Width; x++)
            {
                var tileX = bgX >> 3;
                var screenOffset = screenBase + GetRegularScreenEntryOffset(tileX, tileY, widthTiles, heightTiles);
                var entry = ReadVram16(screenOffset);
                var paletteIndex = GetTilePaletteIndex(charBase, entry, bgX & 7, inTileY, eightBitColor);
                var pixel = rowOffset + x;
                if (_debugRenderingEnabled)
                {
                    RecordRegularBgDebugSample(
                        bg,
                        pixel,
                        control,
                        bgX,
                        bgY,
                        tileX,
                        tileY,
                        screenOffset,
                        entry,
                        paletteIndex,
                        hofs,
                        vofs);
                }
                if (paletteIndex != 0 && priority <= priorities[x] && IsLayerVisibleAtPixel(bg, x, y))
                {
                    SetLayerPixel(pixel, x, priority, (byte)bg, ReadPaletteColor(paletteIndex), priorities, layers, secondLayers);
                }

                bgX = (bgX + 1) & widthMask;
            }

            return;
        }

        var mosaicH = GetBackgroundMosaicHorizontalSize();
        for (var x = 0; x < Width; x++)
        {
            var mosaicX = x - x % mosaicH;
            var bgX = (mosaicX + hofs) & widthMask;
            var tileX = bgX >> 3;
            var screenOffset = screenBase + GetRegularScreenEntryOffset(tileX, tileY, widthTiles, heightTiles);
            var entry = ReadVram16(screenOffset);
            var paletteIndex = GetTilePaletteIndex(charBase, entry, bgX & 7, inTileY, eightBitColor);
            if (_debugRenderingEnabled)
            {
                RecordRegularBgDebugSample(
                    bg,
                    rowOffset + x,
                    control,
                    bgX,
                    bgY,
                    tileX,
                    tileY,
                    screenOffset,
                    entry,
                    paletteIndex,
                    hofs,
                    vofs);
            }
            if (paletteIndex == 0 || priority > priorities[x] || !IsLayerVisibleAtPixel(bg, x, y))
            {
                continue;
            }

            SetLayerPixel(rowOffset + x, x, priority, (byte)bg, ReadPaletteColor(paletteIndex), priorities, layers, secondLayers);
        }
    }

    private void RenderSpritesForPriority(int priority, Span<byte> priorities, Span<byte> layers, Span<byte> secondLayers)
    {
        var oam = _bus.ObjectAttributeMemory;
        for (var sprite = 127; sprite >= 0; sprite--)
        {
            var offset = sprite * 8;
            var attr0 = (ushort)(oam[offset] | (oam[offset + 1] << 8));
            var attr1 = (ushort)(oam[offset + 2] | (oam[offset + 3] << 8));
            var attr2 = (ushort)(oam[offset + 4] | (oam[offset + 5] << 8));
            var affineMode = (attr0 >> 8) & 0x3;
            var objectMode = (attr0 >> 10) & 0x3;
            if (affineMode == 2 || objectMode == 2 || ((attr2 >> 10) & 0x3) != priority)
            {
                continue;
            }

            var color256 = (attr0 & (1 << 13)) != 0;
            var y = attr0 & 0xFF;
            var x = attr1 & 0x1FF;
            if (y >= 160)
            {
                y -= 256;
            }

            if (x >= 240)
            {
                x -= 512;
            }

            var shape = (attr0 >> 14) & 0x3;
            var size = (attr1 >> 14) & 0x3;
            var (spriteWidth, spriteHeight) = GetSpriteDimensions(shape, size);
            var tileBase = attr2 & 0x3FF;
            var paletteBank = (attr2 >> 12) & 0xF;
            var oneDimensional = (_bus.DisplayControl & (1 << 6)) != 0;
            var affine = affineMode != 0;
            var displayWidth = affineMode == 3 ? spriteWidth * 2 : spriteWidth;
            var displayHeight = affineMode == 3 ? spriteHeight * 2 : spriteHeight;
            var hflip = !affine && (attr1 & (1 << 12)) != 0;
            var vflip = !affine && (attr1 & (1 << 13)) != 0;
            var matrixIndex = (attr1 >> 9) & 0x1F;
            var pa = affine ? ReadObjectAffineParameter(matrixIndex, 0) : 0;
            var pb = affine ? ReadObjectAffineParameter(matrixIndex, 1) : 0;
            var pc = affine ? ReadObjectAffineParameter(matrixIndex, 2) : 0;
            var pd = affine ? ReadObjectAffineParameter(matrixIndex, 3) : 0;
            var mosaic = IsObjectMosaicEnabled(attr0);

            for (var sy = 0; sy < displayHeight; sy++)
            {
                var screenY = y + sy;
                if (screenY is < 0 or >= Height)
                {
                    continue;
                }

                for (var sx = 0; sx < displayWidth; sx++)
                {
                    var screenX = x + sx;
                    if (screenX is < 0 or >= Width)
                    {
                        continue;
                    }

                    var pixel = screenY * Width + screenX;
                    if (priority > priorities[pixel])
                    {
                        continue;
                    }

                    var (sourceX, sourceY) = GetObjectSourcePixel(
                        sx,
                        sy,
                        x,
                        y,
                        displayWidth,
                        displayHeight,
                        spriteWidth,
                        spriteHeight,
                        affine,
                        hflip,
                        vflip,
                        mosaic,
                        pa,
                        pb,
                        pc,
                        pd);
                    if (sourceX < 0 || sourceY < 0 || sourceX >= spriteWidth || sourceY >= spriteHeight)
                    {
                        continue;
                    }

                    var paletteIndex = GetSpritePaletteIndex(tileBase, sourceX, sourceY, spriteWidth, color256, oneDimensional, paletteBank);
                    if (paletteIndex == 0 || !IsLayerVisibleAtPixel(4, screenX, screenY))
                    {
                        continue;
                    }

                    SetLayerPixel(
                        pixel,
                        (byte)priority,
                        4,
                        ReadPaletteColor(0x100 + paletteIndex),
                        priorities,
                        layers,
                        secondLayers,
                        objectMode == 1);
                }
            }
        }
    }

    private void RenderSpritesForPriorityScanline(int priority, int scanline, Span<byte> priorities, Span<byte> layers, Span<byte> secondLayers)
    {
        var oam = _bus.ObjectAttributeMemory;
        for (var sprite = 127; sprite >= 0; sprite--)
        {
            var offset = sprite * 8;
            var attr0 = (ushort)(oam[offset] | (oam[offset + 1] << 8));
            var attr1 = (ushort)(oam[offset + 2] | (oam[offset + 3] << 8));
            var attr2 = (ushort)(oam[offset + 4] | (oam[offset + 5] << 8));
            var affineMode = (attr0 >> 8) & 0x3;
            var objectMode = (attr0 >> 10) & 0x3;
            if (affineMode == 2 || objectMode == 2 || ((attr2 >> 10) & 0x3) != priority)
            {
                continue;
            }

            var color256 = (attr0 & (1 << 13)) != 0;
            var y = attr0 & 0xFF;
            var x = attr1 & 0x1FF;
            if (y >= 160)
            {
                y -= 256;
            }

            if (x >= 240)
            {
                x -= 512;
            }

            var shape = (attr0 >> 14) & 0x3;
            var size = (attr1 >> 14) & 0x3;
            var (spriteWidth, spriteHeight) = GetSpriteDimensions(shape, size);
            var tileBase = attr2 & 0x3FF;
            var paletteBank = (attr2 >> 12) & 0xF;
            var oneDimensional = (_bus.DisplayControl & (1 << 6)) != 0;
            var affine = affineMode != 0;
            var displayWidth = affineMode == 3 ? spriteWidth * 2 : spriteWidth;
            var displayHeight = affineMode == 3 ? spriteHeight * 2 : spriteHeight;
            var sy = scanline - y;
            if (sy < 0 || sy >= displayHeight)
            {
                continue;
            }

            var hflip = !affine && (attr1 & (1 << 12)) != 0;
            var vflip = !affine && (attr1 & (1 << 13)) != 0;
            var matrixIndex = (attr1 >> 9) & 0x1F;
            var pa = affine ? ReadObjectAffineParameter(matrixIndex, 0) : 0;
            var pb = affine ? ReadObjectAffineParameter(matrixIndex, 1) : 0;
            var pc = affine ? ReadObjectAffineParameter(matrixIndex, 2) : 0;
            var pd = affine ? ReadObjectAffineParameter(matrixIndex, 3) : 0;
            var mosaic = IsObjectMosaicEnabled(attr0);

            for (var sx = 0; sx < displayWidth; sx++)
            {
                var screenX = x + sx;
                if (screenX is < 0 or >= Width || priority > priorities[screenX])
                {
                    continue;
                }

                var (sourceX, sourceY) = GetObjectSourcePixel(
                    sx,
                    sy,
                    x,
                    y,
                    displayWidth,
                    displayHeight,
                    spriteWidth,
                    spriteHeight,
                    affine,
                    hflip,
                    vflip,
                    mosaic,
                    pa,
                    pb,
                    pc,
                    pd);
                if (sourceX < 0 || sourceY < 0 || sourceX >= spriteWidth || sourceY >= spriteHeight)
                {
                    continue;
                }

                var paletteIndex = GetSpritePaletteIndex(tileBase, sourceX, sourceY, spriteWidth, color256, oneDimensional, paletteBank);
                if (paletteIndex == 0 || !IsLayerVisibleAtPixel(4, screenX, scanline))
                {
                    continue;
                }

                SetLayerPixel(
                    scanline * Width + screenX,
                    screenX,
                    (byte)priority,
                    4,
                    ReadPaletteColor(0x100 + paletteIndex),
                    priorities,
                    layers,
                    secondLayers,
                    objectMode == 1);
            }
        }
    }

    private void RenderMode3()
    {
        var vram = _bus.VideoRam;
        for (var pixel = 0; pixel < Pixels; pixel++)
        {
            var offset = pixel * 2;
            var color = (ushort)(vram[offset] | (vram[offset + 1] << 8));
            _framebuffer[pixel] = Bgr555ToRgba8888(color);
        }
    }

    private void RenderMode3Scanline(int y)
    {
        var vram = _bus.VideoRam;
        var sourceOffset = y * Width * 2;
        var targetOffset = y * Width;
        for (var x = 0; x < Width; x++)
        {
            var offset = sourceOffset + x * 2;
            var color = (ushort)(vram[offset] | (vram[offset + 1] << 8));
            var outputColor = Bgr555ToRgba8888(color);
            _framebuffer[targetOffset + x] = outputColor;
            if (_debugRenderingEnabled)
            {
                RecordDebugLayerPixel(targetOffset + x, 2, outputColor);
            }
        }
    }

    private void RenderMode4()
    {
        var vram = _bus.VideoRam;
        var palette = _bus.PaletteRam;
        var pageOffset = (_bus.DisplayControl & (1 << 4)) != 0 ? 0xA000 : 0;

        for (var pixel = 0; pixel < Pixels; pixel++)
        {
            var paletteIndex = vram[pageOffset + pixel];
            var paletteOffset = paletteIndex * 2;
            var color = (ushort)(palette[paletteOffset] | (palette[paletteOffset + 1] << 8));
            _framebuffer[pixel] = Bgr555ToRgba8888(color);
        }
    }

    private void RenderMode4Scanline(int y)
    {
        var vram = _bus.VideoRam;
        var palette = _bus.PaletteRam;
        var pageOffset = (_bus.DisplayControl & (1 << 4)) != 0 ? 0xA000 : 0;
        var sourceOffset = pageOffset + y * Width;
        var targetOffset = y * Width;
        for (var x = 0; x < Width; x++)
        {
            var paletteIndex = vram[sourceOffset + x];
            var paletteOffset = paletteIndex * 2;
            var color = (ushort)(palette[paletteOffset] | (palette[paletteOffset + 1] << 8));
            var outputColor = Bgr555ToRgba8888(color);
            _framebuffer[targetOffset + x] = outputColor;
            if (_debugRenderingEnabled)
            {
                RecordDebugLayerPixel(targetOffset + x, 2, outputColor);
            }
        }
    }

    private void RenderMode5()
    {
        Array.Clear(_framebuffer);

        var vram = _bus.VideoRam;
        var pageOffset = (_bus.DisplayControl & (1 << 4)) != 0 ? 0xA000 : 0;
        const int mode5Width = 160;
        const int mode5Height = 128;

        for (var y = 0; y < mode5Height; y++)
        {
            for (var x = 0; x < mode5Width; x++)
            {
                var sourceOffset = pageOffset + ((y * mode5Width + x) * 2);
                var color = (ushort)(vram[sourceOffset] | (vram[sourceOffset + 1] << 8));
                _framebuffer[y * Width + x] = Bgr555ToRgba8888(color);
            }
        }
    }

    private void RenderMode5Scanline(int y)
    {
        var targetOffset = y * Width;
        _framebuffer.AsSpan(targetOffset, Width).Clear();
        const int mode5Width = 160;
        const int mode5Height = 128;
        if (y >= mode5Height)
        {
            return;
        }

        var vram = _bus.VideoRam;
        var pageOffset = (_bus.DisplayControl & (1 << 4)) != 0 ? 0xA000 : 0;
        var sourceOffset = pageOffset + y * mode5Width * 2;
        for (var x = 0; x < mode5Width; x++)
        {
            var offset = sourceOffset + x * 2;
            var color = (ushort)(vram[offset] | (vram[offset + 1] << 8));
            var outputColor = Bgr555ToRgba8888(color);
            _framebuffer[targetOffset + x] = outputColor;
            if (_debugRenderingEnabled)
            {
                RecordDebugLayerPixel(targetOffset + x, 2, outputColor);
            }
        }
    }

    private bool TryReadBitmapPixel(int mode, int x, int y, out uint color)
    {
        color = 0;
        var vram = _bus.VideoRam;
        switch (mode)
        {
            case 3:
            {
                var offset = (y * Width + x) * 2;
                color = Bgr555ToRgba8888((ushort)(vram[offset] | (vram[offset + 1] << 8)));
                return true;
            }

            case 4:
            {
                var pageOffset = (_bus.DisplayControl & (1 << 4)) != 0 ? 0xA000 : 0;
                var paletteIndex = vram[pageOffset + y * Width + x];
                var paletteOffset = paletteIndex * 2;
                var palette = _bus.PaletteRam;
                color = Bgr555ToRgba8888((ushort)(palette[paletteOffset] | (palette[paletteOffset + 1] << 8)));
                return true;
            }

            case 5:
            {
                const int mode5Width = 160;
                const int mode5Height = 128;
                if (x >= mode5Width || y >= mode5Height)
                {
                    return false;
                }

                var pageOffset = (_bus.DisplayControl & (1 << 4)) != 0 ? 0xA000 : 0;
                var offset = pageOffset + (y * mode5Width + x) * 2;
                color = Bgr555ToRgba8888((ushort)(vram[offset] | (vram[offset + 1] << 8)));
                return true;
            }

            default:
                return false;
        }
    }

    private void RecordRenderScanline(long profileStart)
    {
        _renderProfile.Scanlines++;
        _renderProfile.ScanlineTicks += Stopwatch.GetTimestamp() - profileStart;
    }

    private void ProfileBitmapScanline(Action render)
    {
        var start = Stopwatch.GetTimestamp();
        render();
        _renderProfile.BitmapTicks += Stopwatch.GetTimestamp() - start;
    }

    private static uint Bgr555ToRgba8888(ushort color)
    {
        var r5 = color & 0x1F;
        var g5 = (color >> 5) & 0x1F;
        var b5 = (color >> 10) & 0x1F;
        var r8 = (uint)((r5 << 3) | (r5 >> 2));
        var g8 = (uint)((g5 << 3) | (g5 >> 2));
        var b8 = (uint)((b5 << 3) | (b5 >> 2));
        return 0xFF00_0000 | (r8 << 16) | (g8 << 8) | b8;
    }

    private int GetRegularScreenEntryOffset(int tileX, int tileY, int widthTiles, int heightTiles)
    {
        var blockX = tileX / 32;
        var blockY = tileY / 32;
        var localX = tileX & 31;
        var localY = tileY & 31;
        var block = blockY * (widthTiles / 32) + blockX;
        return block * 0x800 + (localY * 32 + localX) * 2;
    }

    private int GetTilePaletteIndex(int charBase, ushort entry, int x, int y, bool eightBitColor)
    {
        var tileNumber = entry & 0x3FF;
        var hflip = (entry & (1 << 10)) != 0;
        var vflip = (entry & (1 << 11)) != 0;
        var paletteBank = (entry >> 12) & 0xF;
        var sourceX = hflip ? 7 - x : x;
        var sourceY = vflip ? 7 - y : y;

        if (eightBitColor)
        {
            var offset = charBase + tileNumber * 64 + sourceY * 8 + sourceX;
            return ReadBgVram8(offset);
        }

        var byteOffset = charBase + tileNumber * 32 + sourceY * 4 + sourceX / 2;
        var packed = ReadBgVram8(byteOffset);
        var color = (sourceX & 1) == 0 ? packed & 0xF : packed >> 4;
        return color == 0 ? 0 : (int)(paletteBank * 16 + color);
    }

    private int GetSpritePaletteIndex(int tileBase, int x, int y, int spriteWidth, bool color256, bool oneDimensional, int paletteBank)
    {
        var tileX = x / 8;
        var tileY = y / 8;
        var inTileX = x & 7;
        var inTileY = y & 7;
        var tileStep = color256 ? 2 : 1;
        var tileStride = oneDimensional ? (spriteWidth / 8) * tileStep : 32;
        var tileNumber = tileBase + tileY * tileStride + tileX * tileStep;

        if (color256)
        {
            var offset = MapObjectTileOffset(tileNumber, inTileY * 8 + inTileX);
            return offset < 0 ? 0 : _bus.VideoRam[offset];
        }

        var packedOffset = MapObjectTileOffset(tileNumber, inTileY * 4 + inTileX / 2);
        if (packedOffset < 0)
        {
            return 0;
        }

        var packed = _bus.VideoRam[packedOffset];
        var color = (inTileX & 1) == 0 ? packed & 0xF : packed >> 4;
        return color == 0 ? 0 : paletteBank * 16 + color;
    }

    private int MapObjectTileOffset(int tileNumber, int byteOffsetInTile)
    {
        var offset = tileNumber * 32 + byteOffsetInTile;
        var displayMode = _bus.DisplayControl & 0x7;
        if (displayMode is >= 3 and <= 5)
        {
            return offset < 0x4000
                ? -1
                : 0x14000 + ((offset - 0x4000) & 0x3FFF);
        }

        return 0x10000 + (offset & 0x7FFF);
    }

    private uint ReadPaletteColor(int index)
    {
        var offset = index * 2;
        var palette = _bus.PaletteRam;
        return Bgr555ToRgba8888((ushort)(palette[offset] | (palette[offset + 1] << 8)));
    }

    private void ClearDebugLayerRows(int y)
    {
        var rowOffset = y * Width;
        for (var layer = 0; layer < _debugLayerFramebuffers.Length; layer++)
        {
            _debugLayerFramebuffers[layer].AsSpan(rowOffset, Width).Fill(0xFF00_0000u);
        }

        for (var bg = 0; bg < _debugAffineSamples.Length; bg++)
        {
            _debugAffineSamples[bg].AsSpan(rowOffset, Width).Clear();
        }

        for (var bg = 0; bg < _debugRegularBgSamples.Length; bg++)
        {
            _debugRegularBgSamples[bg].AsSpan(rowOffset, Width).Clear();
        }
    }

    private void ClearDebugLayerFramebuffers()
    {
        for (var layer = 0; layer < _debugLayerFramebuffers.Length; layer++)
        {
            Array.Fill(_debugLayerFramebuffers[layer], 0xFF00_0000u);
        }
    }

    private void CaptureDebugCompositionFrame(ReadOnlySpan<byte> layers, ReadOnlySpan<byte> secondLayers)
    {
        if (!_debugRenderingEnabled)
        {
            return;
        }

        _framebuffer.CopyTo(_debugPreBlendFramebuffer, 0);
        _secondFramebuffer.CopyTo(_debugSecondTargetFramebuffer, 0);
        layers.CopyTo(_debugTopLayers);
        secondLayers.CopyTo(_debugSecondLayers);
    }

    private void CaptureDebugCompositionScanline(int y, ReadOnlySpan<byte> layers, ReadOnlySpan<byte> secondLayers)
    {
        if (!_debugRenderingEnabled)
        {
            return;
        }

        var rowOffset = y * Width;
        _framebuffer.AsSpan(rowOffset, Width).CopyTo(_debugPreBlendFramebuffer.AsSpan(rowOffset, Width));
        _secondFramebuffer.AsSpan(rowOffset, Width).CopyTo(_debugSecondTargetFramebuffer.AsSpan(rowOffset, Width));
        layers.CopyTo(_debugTopLayers.AsSpan(rowOffset, Width));
        secondLayers.CopyTo(_debugSecondLayers.AsSpan(rowOffset, Width));
    }

    private void ClearDebugCompositionFramebuffers()
    {
        Array.Fill(_debugPreBlendFramebuffer, 0xFF00_0000u);
        Array.Fill(_debugSecondTargetFramebuffer, 0xFF00_0000u);
        Array.Fill(_debugTopLayers, (byte)5);
        Array.Fill(_debugSecondLayers, (byte)5);
    }

    private void ClearDebugAffineSamples()
    {
        for (var bg = 0; bg < _debugAffineSamples.Length; bg++)
        {
            Array.Clear(_debugAffineSamples[bg]);
        }
    }

    private void ClearDebugRegularBgSamples()
    {
        for (var bg = 0; bg < _debugRegularBgSamples.Length; bg++)
        {
            Array.Clear(_debugRegularBgSamples[bg]);
        }
    }

    private void RecordRegularBgDebugSample(
        int bg,
        int pixel,
        ushort control,
        int sourceX,
        int sourceY,
        int tileX,
        int tileY,
        int screenOffset,
        ushort screenEntry,
        int paletteIndex,
        int hofs,
        int vofs)
    {
        if (bg is < 0 or > 3)
        {
            return;
        }

        if (!_debugRenderingEnabled)
        {
            return;
        }

        _debugRegularBgSamples[bg][pixel] = new RegularBgDebugSample(
            true,
            (byte)bg,
            control,
            sourceX,
            sourceY,
            tileX,
            tileY,
            screenOffset,
            screenEntry,
            paletteIndex,
            hofs,
            vofs);
    }

    private void RecordAffineDebugSample(
        int bg,
        int pixel,
        ushort control,
        int fixedX,
        int fixedY,
        int sourceX,
        int sourceY,
        int tileX,
        int tileY,
        int mapOffset,
        int tileNumber,
        int tileOffset,
        int paletteIndex,
        int pa,
        int pb,
        int pc,
        int pd,
        int referenceX,
        int referenceY)
    {
        if (bg is not (2 or 3))
        {
            return;
        }

        if (!_debugRenderingEnabled)
        {
            return;
        }

        _debugAffineSamples[bg - 2][pixel] = new AffineDebugSample(
            true,
            (byte)bg,
            control,
            fixedX,
            fixedY,
            sourceX,
            sourceY,
            tileX,
            tileY,
            mapOffset,
            tileNumber,
            tileOffset,
            paletteIndex,
            pa,
            pb,
            pc,
            pd,
            referenceX,
            referenceY);
    }

    private static uint[] RenderLayerMap(ReadOnlySpan<byte> layers)
    {
        var output = new uint[Pixels];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = LayerMapColor(layers[i]);
        }

        return output;
    }

    private static uint LayerMapColor(byte layer) => layer switch
    {
        0 => 0xFFFF_0000u,
        1 => 0xFF00_FF00u,
        2 => 0xFF00_00FFu,
        3 => 0xFFFF_FF00u,
        4 => 0xFFFF_00FFu,
        5 => 0xFF40_4040u,
        _ => 0xFF00_0000u
    };

    private void RecordDebugLayerPixel(int pixel, byte layer, uint color)
    {
        if (!_debugRenderingEnabled)
        {
            return;
        }

        if (layer < _debugLayerFramebuffers.Length)
        {
            _debugLayerFramebuffers[layer][pixel] = color;
        }
    }

    private void SetLayerPixel(
        int pixel,
        byte priority,
        byte layer,
        uint color,
        Span<byte> priorities,
        Span<byte> layers,
        Span<byte> secondLayers,
        bool semiTransparentObject = false)
    {
        if (layer != 4 || layers[pixel] != 4)
        {
            _secondFramebuffer[pixel] = _framebuffer[pixel];
            secondLayers[pixel] = layers[pixel];
        }

        priorities[pixel] = priority;
        layers[pixel] = layer;
        _framebuffer[pixel] = color;
        if (_debugRenderingEnabled)
        {
            RecordDebugLayerPixel(pixel, layer, color);
        }

        _semiTransparentObject[pixel] = semiTransparentObject;
    }

    private void SetLayerPixel(
        int pixel,
        int rowPixel,
        byte priority,
        byte layer,
        uint color,
        Span<byte> priorities,
        Span<byte> layers,
        Span<byte> secondLayers,
        bool semiTransparentObject = false)
    {
        if (layer != 4 || layers[rowPixel] != 4)
        {
            _secondFramebuffer[pixel] = _framebuffer[pixel];
            secondLayers[rowPixel] = layers[rowPixel];
        }

        priorities[rowPixel] = priority;
        layers[rowPixel] = layer;
        _framebuffer[pixel] = color;
        if (_debugRenderingEnabled)
        {
            RecordDebugLayerPixel(pixel, layer, color);
        }

        _semiTransparentObject[pixel] = semiTransparentObject;
    }

    private void ApplyBlendEffects(ReadOnlySpan<byte> layers, ReadOnlySpan<byte> secondLayers)
    {
        var blendControl = _bus.PeekIo16(IoRegisters.BLDCNT);
        var effect = (blendControl >> 6) & 0x3;
        var targetMask = blendControl & 0x3F;
        var secondTargetMask = (blendControl >> 8) & 0x3F;
        if (effect == 1 || HasSemiTransparentObject(Pixels, 0))
        {
            var alpha = _bus.PeekIo16(IoRegisters.BLDALPHA);
            var eva = Math.Min(alpha & 0x1F, 16);
            var evb = Math.Min((alpha >> 8) & 0x1F, 16);
            for (var pixel = 0; pixel < Pixels; pixel++)
            {
                var firstTarget = (effect == 1 && (targetMask & (1 << layers[pixel])) != 0)
                    || _semiTransparentObject[pixel];
                if (!firstTarget || (secondTargetMask & (1 << secondLayers[pixel])) == 0)
                {
                    continue;
                }

                var x = pixel % Width;
                var y = pixel / Width;
                if (!AreEffectsEnabledAtPixel(x, y))
                {
                    continue;
                }

                _framebuffer[pixel] = AlphaBlend(_framebuffer[pixel], _secondFramebuffer[pixel], eva, evb);
            }

            if (effect is 0 or 1)
            {
                return;
            }
        }

        if (effect is not (2 or 3))
        {
            return;
        }

        var coefficient = Math.Min(_bus.PeekIo16(IoRegisters.BLDY) & 0x1F, 16);
        if (coefficient == 0)
        {
            return;
        }

        for (var pixel = 0; pixel < Pixels; pixel++)
        {
            if ((targetMask & (1 << layers[pixel])) == 0)
            {
                continue;
            }

            var x = pixel % Width;
            var y = pixel / Width;
            if (!AreEffectsEnabledAtPixel(x, y))
            {
                continue;
            }

            if (SemiTransparentObjectHasSecondTarget(pixel, secondLayers[pixel], secondTargetMask, x, y))
            {
                continue;
            }

            _framebuffer[pixel] = effect == 2
                ? IncreaseBrightness(_framebuffer[pixel], coefficient)
                : DecreaseBrightness(_framebuffer[pixel], coefficient);
        }
    }

    private void ApplyBlendEffectsScanline(int y, ReadOnlySpan<byte> layers, ReadOnlySpan<byte> secondLayers)
    {
        var blendControl = _bus.PeekIo16(IoRegisters.BLDCNT);
        var effect = (blendControl >> 6) & 0x3;
        if (effect == 0 && (_bus.DisplayControl & (1 << 12)) == 0)
        {
            return;
        }

        var targetMask = blendControl & 0x3F;
        var secondTargetMask = (blendControl >> 8) & 0x3F;
        var rowOffset = y * Width;
        if (effect == 1 || HasSemiTransparentObject(Width, rowOffset))
        {
            var alpha = _bus.PeekIo16(IoRegisters.BLDALPHA);
            var eva = Math.Min(alpha & 0x1F, 16);
            var evb = Math.Min((alpha >> 8) & 0x1F, 16);
            for (var x = 0; x < Width; x++)
            {
                var firstTarget = (effect == 1 && (targetMask & (1 << layers[x])) != 0)
                    || _semiTransparentObject[rowOffset + x];
                if (!firstTarget || (secondTargetMask & (1 << secondLayers[x])) == 0 || !AreEffectsEnabledAtPixel(x, y))
                {
                    continue;
                }

                _framebuffer[rowOffset + x] = AlphaBlend(_framebuffer[rowOffset + x], _secondFramebuffer[rowOffset + x], eva, evb);
            }

            if (effect is 0 or 1)
            {
                return;
            }
        }

        if (effect is not (2 or 3))
        {
            return;
        }

        var coefficient = Math.Min(_bus.PeekIo16(IoRegisters.BLDY) & 0x1F, 16);
        if (coefficient == 0)
        {
            return;
        }

        for (var x = 0; x < Width; x++)
        {
            if ((targetMask & (1 << layers[x])) == 0 || !AreEffectsEnabledAtPixel(x, y))
            {
                continue;
            }

            var pixel = rowOffset + x;
            if (SemiTransparentObjectHasSecondTarget(pixel, secondLayers[x], secondTargetMask, x, y))
            {
                continue;
            }

            _framebuffer[pixel] = effect == 2
                ? IncreaseBrightness(_framebuffer[pixel], coefficient)
                : DecreaseBrightness(_framebuffer[pixel], coefficient);
        }
    }

    private bool HasSemiTransparentObject(int count, int start)
    {
        for (var i = 0; i < count; i++)
        {
            if (_semiTransparentObject[start + i])
            {
                return true;
            }
        }

        return false;
    }

    private bool SemiTransparentObjectHasSecondTarget(int pixel, byte secondLayer, int secondTargetMask, int x, int y)
        => _semiTransparentObject[pixel]
            && (secondTargetMask & (1 << secondLayer)) != 0
            && AreEffectsEnabledAtPixel(x, y);

    private static uint AlphaBlend(uint first, uint second, int eva, int evb)
    {
        var r = Math.Min((((int)(first >> 16) & 0xFF) * eva + ((int)(second >> 16) & 0xFF) * evb) / 16, 255);
        var g = Math.Min((((int)(first >> 8) & 0xFF) * eva + ((int)(second >> 8) & 0xFF) * evb) / 16, 255);
        var b = Math.Min(((int)(first & 0xFF) * eva + (int)(second & 0xFF) * evb) / 16, 255);
        return 0xFF00_0000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private bool IsLayerVisibleAtPixel(int layer, int x, int y)
    {
        var displayControl = _bus.DisplayControl;
        return (displayControl & 0xE000) == 0
            || (GetWindowMaskAtPixel(displayControl, x, y) & (1 << layer)) != 0;
    }

    private bool AreEffectsEnabledAtPixel(int x, int y)
    {
        var displayControl = _bus.DisplayControl;
        return (displayControl & 0xE000) == 0
            || (GetWindowMaskAtPixel(displayControl, x, y) & (1 << 5)) != 0;
    }

    private int GetWindowMaskAtPixel(int x, int y)
        => GetWindowMaskAtPixel(_bus.DisplayControl, x, y);

    private int GetWindowMaskAtPixel(ushort displayControl, int x, int y)
    {
        var win0Enabled = (displayControl & (1 << 13)) != 0;
        var win1Enabled = (displayControl & (1 << 14)) != 0;

        if (win0Enabled && IsInsideWindow(x, y, IoRegisters.WIN0H, IoRegisters.WIN0V))
        {
            return _bus.PeekIo16(IoRegisters.WININ) & 0x3F;
        }

        if (win1Enabled && IsInsideWindow(x, y, IoRegisters.WIN1H, IoRegisters.WIN1V))
        {
            return (_bus.PeekIo16(IoRegisters.WININ) >> 8) & 0x3F;
        }

        if ((displayControl & (1 << 15)) != 0 && _objectWindow[y * Width + x])
        {
            return (_bus.PeekIo16(IoRegisters.WINOUT) >> 8) & 0x3F;
        }

        if (win0Enabled || win1Enabled || (displayControl & (1 << 15)) != 0)
        {
            return _bus.PeekIo16(IoRegisters.WINOUT) & 0x3F;
        }

        return 0x3F;
    }

    private bool IsInsideWindow(int x, int y, uint horizontalRegister, uint verticalRegister)
    {
        var horizontal = _bus.PeekIo16(horizontalRegister);
        var vertical = _bus.PeekIo16(verticalRegister);
        var right = horizontal & 0xFF;
        var left = (horizontal >> 8) & 0xFF;
        var bottom = vertical & 0xFF;
        var top = (vertical >> 8) & 0xFF;
        return IsInsideWindowRange(x, left, right) && IsInsideWindowRange(y, top, bottom);
    }

    private static bool IsInsideWindowRange(int value, int start, int end)
    {
        if (start == end)
        {
            return false;
        }

        return start < end
            ? value >= start && value < end
            : value >= start || value < end;
    }

    private void RenderObjectWindow()
    {
        var oam = _bus.ObjectAttributeMemory;
        for (var sprite = 127; sprite >= 0; sprite--)
        {
            var offset = sprite * 8;
            var attr0 = (ushort)(oam[offset] | (oam[offset + 1] << 8));
            var attr1 = (ushort)(oam[offset + 2] | (oam[offset + 3] << 8));
            var attr2 = (ushort)(oam[offset + 4] | (oam[offset + 5] << 8));
            var affineMode = (attr0 >> 8) & 0x3;
            var objectMode = (attr0 >> 10) & 0x3;
            if (objectMode != 2 || affineMode == 2)
            {
                continue;
            }

            var color256 = (attr0 & (1 << 13)) != 0;
            var y = attr0 & 0xFF;
            var x = attr1 & 0x1FF;
            if (y >= 160)
            {
                y -= 256;
            }

            if (x >= 240)
            {
                x -= 512;
            }

            var shape = (attr0 >> 14) & 0x3;
            var size = (attr1 >> 14) & 0x3;
            var (spriteWidth, spriteHeight) = GetSpriteDimensions(shape, size);
            var tileBase = attr2 & 0x3FF;
            var paletteBank = (attr2 >> 12) & 0xF;
            var oneDimensional = (_bus.DisplayControl & (1 << 6)) != 0;
            var affine = affineMode != 0;
            var displayWidth = affineMode == 3 ? spriteWidth * 2 : spriteWidth;
            var displayHeight = affineMode == 3 ? spriteHeight * 2 : spriteHeight;
            var hflip = !affine && (attr1 & (1 << 12)) != 0;
            var vflip = !affine && (attr1 & (1 << 13)) != 0;
            var matrixIndex = (attr1 >> 9) & 0x1F;
            var pa = affine ? ReadObjectAffineParameter(matrixIndex, 0) : 0;
            var pb = affine ? ReadObjectAffineParameter(matrixIndex, 1) : 0;
            var pc = affine ? ReadObjectAffineParameter(matrixIndex, 2) : 0;
            var pd = affine ? ReadObjectAffineParameter(matrixIndex, 3) : 0;

            for (var sy = 0; sy < displayHeight; sy++)
            {
                var screenY = y + sy;
                if (screenY is < 0 or >= Height)
                {
                    continue;
                }

                for (var sx = 0; sx < displayWidth; sx++)
                {
                    var screenX = x + sx;
                    if (screenX is < 0 or >= Width)
                    {
                        continue;
                    }

                    var (sourceX, sourceY) = GetObjectSourcePixel(
                        sx,
                        sy,
                        x,
                        y,
                        displayWidth,
                        displayHeight,
                        spriteWidth,
                        spriteHeight,
                        affine,
                        hflip,
                        vflip,
                        false,
                        pa,
                        pb,
                        pc,
                        pd);
                    if (sourceX < 0 || sourceY < 0 || sourceX >= spriteWidth || sourceY >= spriteHeight)
                    {
                        continue;
                    }

                    if (GetSpritePaletteIndex(tileBase, sourceX, sourceY, spriteWidth, color256, oneDimensional, paletteBank) == 0)
                    {
                        continue;
                    }

                    _objectWindow[screenY * Width + screenX] = true;
                }
            }
        }
    }

    private void RenderObjectWindowScanline(int scanline)
    {
        var oam = _bus.ObjectAttributeMemory;
        for (var sprite = 127; sprite >= 0; sprite--)
        {
            var offset = sprite * 8;
            var attr0 = (ushort)(oam[offset] | (oam[offset + 1] << 8));
            var attr1 = (ushort)(oam[offset + 2] | (oam[offset + 3] << 8));
            var attr2 = (ushort)(oam[offset + 4] | (oam[offset + 5] << 8));
            var affineMode = (attr0 >> 8) & 0x3;
            var objectMode = (attr0 >> 10) & 0x3;
            if (objectMode != 2 || affineMode == 2)
            {
                continue;
            }

            var color256 = (attr0 & (1 << 13)) != 0;
            var y = attr0 & 0xFF;
            var x = attr1 & 0x1FF;
            if (y >= 160)
            {
                y -= 256;
            }

            if (x >= 240)
            {
                x -= 512;
            }

            var shape = (attr0 >> 14) & 0x3;
            var size = (attr1 >> 14) & 0x3;
            var (spriteWidth, spriteHeight) = GetSpriteDimensions(shape, size);
            var oneDimensional = (_bus.DisplayControl & (1 << 6)) != 0;
            var affine = affineMode != 0;
            var displayWidth = affineMode == 3 ? spriteWidth * 2 : spriteWidth;
            var displayHeight = affineMode == 3 ? spriteHeight * 2 : spriteHeight;
            var sy = scanline - y;
            if (sy < 0 || sy >= displayHeight)
            {
                continue;
            }

            var tileBase = attr2 & 0x3FF;
            var paletteBank = (attr2 >> 12) & 0xF;
            var hflip = !affine && (attr1 & (1 << 12)) != 0;
            var vflip = !affine && (attr1 & (1 << 13)) != 0;
            var matrixIndex = (attr1 >> 9) & 0x1F;
            var pa = affine ? ReadObjectAffineParameter(matrixIndex, 0) : 0;
            var pb = affine ? ReadObjectAffineParameter(matrixIndex, 1) : 0;
            var pc = affine ? ReadObjectAffineParameter(matrixIndex, 2) : 0;
            var pd = affine ? ReadObjectAffineParameter(matrixIndex, 3) : 0;

            for (var sx = 0; sx < displayWidth; sx++)
            {
                var screenX = x + sx;
                if (screenX is < 0 or >= Width)
                {
                    continue;
                }

                var (sourceX, sourceY) = GetObjectSourcePixel(
                    sx,
                    sy,
                    x,
                    y,
                    displayWidth,
                    displayHeight,
                    spriteWidth,
                    spriteHeight,
                    affine,
                    hflip,
                    vflip,
                    false,
                    pa,
                    pb,
                    pc,
                    pd);
                if (sourceX < 0 || sourceY < 0 || sourceX >= spriteWidth || sourceY >= spriteHeight)
                {
                    continue;
                }

                if (GetSpritePaletteIndex(tileBase, sourceX, sourceY, spriteWidth, color256, oneDimensional, paletteBank) == 0)
                {
                    continue;
                }

                _objectWindow[scanline * Width + screenX] = true;
            }
        }
    }

    private int ReadObjectAffineParameter(int matrixIndex, int parameter)
    {
        var offset = matrixIndex * 32 + 6 + parameter * 8;
        var oam = _bus.ObjectAttributeMemory;
        return unchecked((short)(oam[offset] | (oam[offset + 1] << 8)));
    }

    private (int X, int Y) GetObjectSourcePixel(
        int x,
        int y,
        int objectX,
        int objectY,
        int displayWidth,
        int displayHeight,
        int spriteWidth,
        int spriteHeight,
        bool affine,
        bool hflip,
        bool vflip,
        bool mosaic,
        int pa,
        int pb,
        int pc,
        int pd)
    {
        if (mosaic)
        {
            x = GetObjectMosaicCoordinate(objectX, x, displayWidth, GetObjectMosaicHorizontalSize());
            y = GetObjectMosaicCoordinate(objectY, y, displayHeight, GetObjectMosaicVerticalSize());
        }

        return affine
            ? TransformObjectPixel(x, y, displayWidth, displayHeight, spriteWidth, spriteHeight, pa, pb, pc, pd)
            : (hflip ? spriteWidth - 1 - x : x, vflip ? spriteHeight - 1 - y : y);
    }

    private static int GetObjectMosaicCoordinate(int objectPosition, int localCoordinate, int extent, int mosaicSize)
    {
        var screenCoordinate = objectPosition + localCoordinate;
        var blockOffset = ((screenCoordinate % mosaicSize) + mosaicSize) % mosaicSize;
        return Math.Clamp(localCoordinate - blockOffset, 0, extent - 1);
    }

    private bool IsObjectWindowEnabled()
        => (_bus.DisplayControl & ((1 << 12) | (1 << 15))) == ((1 << 12) | (1 << 15));

    private static bool IsObjectMosaicEnabled(ushort attr0) => (attr0 & (1 << 12)) != 0;

    private int GetObjectMosaicHorizontalSize() => ((_bus.PeekIo16(IoRegisters.MOSAIC) >> 8) & 0xF) + 1;

    private int GetObjectMosaicVerticalSize() => ((_bus.PeekIo16(IoRegisters.MOSAIC) >> 12) & 0xF) + 1;

    private static (int X, int Y) TransformObjectPixel(
        int x,
        int y,
        int displayWidth,
        int displayHeight,
        int spriteWidth,
        int spriteHeight,
        int pa,
        int pb,
        int pc,
        int pd)
    {
        var centeredX = x - displayWidth / 2;
        var centeredY = y - displayHeight / 2;
        var sourceX = ((pa * centeredX + pb * centeredY) >> 8) + spriteWidth / 2;
        var sourceY = ((pc * centeredX + pd * centeredY) >> 8) + spriteHeight / 2;
        return (sourceX, sourceY);
    }

    private static uint IncreaseBrightness(uint color, int coefficient)
    {
        var r = (int)((color >> 16) & 0xFF);
        var g = (int)((color >> 8) & 0xFF);
        var b = (int)(color & 0xFF);
        r += (255 - r) * coefficient / 16;
        g += (255 - g) * coefficient / 16;
        b += (255 - b) * coefficient / 16;
        return 0xFF00_0000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static uint DecreaseBrightness(uint color, int coefficient)
    {
        var r = (int)((color >> 16) & 0xFF);
        var g = (int)((color >> 8) & 0xFF);
        var b = (int)(color & 0xFF);
        r -= r * coefficient / 16;
        g -= g * coefficient / 16;
        b -= b * coefficient / 16;
        return 0xFF00_0000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private ushort ReadVram16(int offset)
    {
        return (ushort)(ReadBgVram8(offset) | (ReadBgVram8(offset + 1) << 8));
    }

    private byte ReadBgVram8(int offset) => _bus.VideoRam[offset & 0xFFFF];

    private int ReadSignedFixed8(uint address)
        => unchecked((short)_bus.PeekIo16(address));

    private int ReadSignedFixed20(uint address)
    {
        var value = (int)_bus.PeekIo32(address) & 0x0FFF_FFFF;
        if ((value & 0x0800_0000) != 0)
        {
            value |= unchecked((int)0xF000_0000);
        }

        return value;
    }

    private int ReadSignedFixed28(uint address) => ReadSignedFixed20(address);

    private bool IsBackgroundMosaicEnabled(ushort control) => (control & (1 << 6)) != 0;

    private int GetBackgroundMosaicHorizontalSize() => (_bus.PeekIo16(IoRegisters.MOSAIC) & 0xF) + 1;

    private int GetBackgroundMosaicVerticalSize() => ((_bus.PeekIo16(IoRegisters.MOSAIC) >> 4) & 0xF) + 1;

    private static int PositiveModulo(int value, int modulo)
    {
        var result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static (int Width, int Height) GetSpriteDimensions(int shape, int size)
        => (shape, size) switch
        {
            (0, 0) => (8, 8),
            (0, 1) => (16, 16),
            (0, 2) => (32, 32),
            (0, 3) => (64, 64),
            (1, 0) => (16, 8),
            (1, 1) => (32, 8),
            (1, 2) => (32, 16),
            (1, 3) => (64, 32),
            (2, 0) => (8, 16),
            (2, 1) => (8, 32),
            (2, 2) => (16, 32),
            (2, 3) => (32, 64),
            _ => (8, 8)
        };
}

public readonly record struct AffineDebugSample(
    bool Valid,
    byte Bg,
    ushort Control,
    int FixedX,
    int FixedY,
    int SourceX,
    int SourceY,
    int TileX,
    int TileY,
    int MapOffset,
    int TileNumber,
    int TileOffset,
    int PaletteIndex,
    int Pa,
    int Pb,
    int Pc,
    int Pd,
    int ReferenceX,
    int ReferenceY);

public readonly record struct RegularBgDebugSample(
    bool Valid,
    byte Bg,
    ushort Control,
    int SourceX,
    int SourceY,
    int TileX,
    int TileY,
    int ScreenOffset,
    ushort ScreenEntry,
    int PaletteIndex,
    int HOffset,
    int VOffset);

public struct VideoRenderProfile
{
    public long Scanlines;
    public long ScanlineTicks;
    public long RegularBackgroundTicks;
    public long AffineBackgroundTicks;
    public long SpriteTicks;
    public long BlendTicks;
    public long BitmapTicks;
    public long ObjectWindowTicks;

    public long AccountedTicks
        => RegularBackgroundTicks
            + AffineBackgroundTicks
            + SpriteTicks
            + BlendTicks
            + BitmapTicks
            + ObjectWindowTicks;

    public long OtherTicks => Math.Max(0, ScanlineTicks - AccountedTicks);

    public double ScanlineMilliseconds => TicksToMilliseconds(ScanlineTicks);

    public double RegularBackgroundMilliseconds => TicksToMilliseconds(RegularBackgroundTicks);

    public double AffineBackgroundMilliseconds => TicksToMilliseconds(AffineBackgroundTicks);

    public double SpriteMilliseconds => TicksToMilliseconds(SpriteTicks);

    public double BlendMilliseconds => TicksToMilliseconds(BlendTicks);

    public double BitmapMilliseconds => TicksToMilliseconds(BitmapTicks);

    public double ObjectWindowMilliseconds => TicksToMilliseconds(ObjectWindowTicks);

    public double OtherMilliseconds => TicksToMilliseconds(OtherTicks);

    public double RegularBackgroundPercent => Percent(RegularBackgroundTicks);

    public double AffineBackgroundPercent => Percent(AffineBackgroundTicks);

    public double SpritePercent => Percent(SpriteTicks);

    public double BlendPercent => Percent(BlendTicks);

    public double BitmapPercent => Percent(BitmapTicks);

    public double ObjectWindowPercent => Percent(ObjectWindowTicks);

    public double OtherPercent => Percent(OtherTicks);

    private double Percent(long ticks) => ScanlineTicks == 0 ? 0 : ticks * 100.0 / ScanlineTicks;

    private static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
