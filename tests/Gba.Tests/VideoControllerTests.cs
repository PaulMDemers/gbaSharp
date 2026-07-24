using Gba.Core;
using Gba.Core.Cpu;
using Gba.Core.Memory;
using Gba.Core.Video;

namespace Gba.Tests;

public sealed class VideoControllerTests
{
    [Fact]
    public void ResetInitializesVCountAndVCountStatus()
    {
        var gba = new GbaSystem();

        Assert.Equal(0, gba.Bus.VerticalCount);
        Assert.Equal(IoRegisters.DispstatVCount, gba.Bus.DisplayStatus & IoRegisters.DispstatVCount);
    }

    [Fact]
    public void SchedulerAdvancesIntoAndOutOfHBlank()
    {
        var gba = new GbaSystem();

        gba.Scheduler.Advance(VideoController.HDrawCycles);

        Assert.Equal(IoRegisters.DispstatHBlank, gba.Bus.DisplayStatus & IoRegisters.DispstatHBlank);

        gba.Scheduler.Advance(VideoController.HBlankCycles);

        Assert.Equal(1, gba.Bus.VerticalCount);
        Assert.Equal(0, gba.Bus.DisplayStatus & IoRegisters.DispstatHBlank);
    }

    [Fact]
    public void VBlankBeginsAtLine160AndRequestsInterruptWhenEnabled()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayStatus = IoRegisters.DispstatVBlankIrq;

        gba.Scheduler.Advance(VideoController.CyclesPerScanline * VideoController.VisibleLines);

        Assert.Equal(VideoController.VisibleLines, gba.Bus.VerticalCount);
        Assert.Equal(IoRegisters.DispstatVBlank, gba.Bus.DisplayStatus & IoRegisters.DispstatVBlank);
        Assert.Equal(IoRegisters.InterruptVBlank, gba.Bus.InterruptFlags & IoRegisters.InterruptVBlank);
    }

    [Fact]
    public void VideoReportsCyclesUntilNextVBlankStart()
    {
        var gba = new GbaSystem();

        Assert.Equal(VideoController.CyclesPerScanline * VideoController.VisibleLines, gba.Video.CyclesUntilNextVBlankStart);

        gba.Scheduler.Advance(VideoController.CyclesPerScanline * 5 + VideoController.HDrawCycles);

        Assert.Equal(
            VideoController.CyclesPerScanline * (VideoController.VisibleLines - 5) - VideoController.HDrawCycles,
            gba.Video.CyclesUntilNextVBlankStart);
    }

    [Fact]
    public void NoBiosVBlankIntrWaitAdvancesInChunksUntilNextVBlank()
    {
        var gba = new GbaSystem();
        gba.Bus.Write32(GbaMemoryMap.EwramStart, 0xEF05_0000); // swi VBlankIntrWait
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Scheduler.Advance(VideoController.CyclesPerScanline * 5);
        var before = gba.Scheduler.Now;

        var cycles = 0;
        while (gba.Bus.VerticalCount != VideoController.VisibleLines)
        {
            cycles += gba.Step();
        }

        Assert.InRange(cycles, VideoController.CyclesPerScanline * (VideoController.VisibleLines - 5), VideoController.CyclesPerScanline * (VideoController.VisibleLines - 5) + 1023);
        Assert.Equal(before + cycles, gba.Scheduler.Now);
        Assert.Equal(VideoController.VisibleLines, gba.Bus.VerticalCount);
    }

    [Fact]
    public void VCountMatchRequestsInterruptWhenEnabled()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayStatus = (ushort)(IoRegisters.DispstatVCountIrq | (3 << 8));

        gba.Scheduler.Advance(VideoController.CyclesPerScanline * 3);

        Assert.Equal(3, gba.Bus.VerticalCount);
        Assert.Equal(IoRegisters.DispstatVCount, gba.Bus.DisplayStatus & IoRegisters.DispstatVCount);
        Assert.Equal(IoRegisters.InterruptVCount, gba.Bus.InterruptFlags & IoRegisters.InterruptVCount);
    }

    [Fact]
    public void WritingOneToInterruptFlagsClearsThoseBits()
    {
        var gba = new GbaSystem();
        gba.Bus.RequestInterrupt((ushort)(IoRegisters.InterruptVBlank | IoRegisters.InterruptHBlank));

        gba.Bus.Write16(IoRegisters.IF, IoRegisters.InterruptVBlank);

        Assert.Equal(0, gba.Bus.InterruptFlags & IoRegisters.InterruptVBlank);
        Assert.Equal(IoRegisters.InterruptHBlank, gba.Bus.InterruptFlags & IoRegisters.InterruptHBlank);
    }

    [Fact]
    public void VBlankInterruptCanEnterCpuIrq()
    {
        var gba = new GbaSystem();
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Cpu.SetIrqEnabled(true);
        gba.Bus.InterruptEnable = IoRegisters.InterruptVBlank;
        gba.Bus.InterruptMasterEnable = true;
        gba.Bus.DisplayStatus = IoRegisters.DispstatVBlankIrq;

        gba.Scheduler.Advance(VideoController.CyclesPerScanline * VideoController.VisibleLines);
        gba.Step();

        Assert.Equal(CpuMode.Irq, gba.Cpu.Mode);
        Assert.Equal(0x18u, gba.Cpu.Pc);
    }

    [Fact]
    public void Mode3RendersBgr555PixelsToFramebuffer()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 3 | (1 << 10);
        gba.Bus.Write16(GbaMemoryMap.VramStart, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 2, 0x03E0);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 4, 0x7C00);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFF00_00FFu, gba.Video.Framebuffer[2]);
    }

    [Fact]
    public void Mode4RendersPaletteIndexedPixels()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 4 | (1 << 10);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 1);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 1, 2);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void Mode4UsesSecondFramePageWhenSelected()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 4 | (1 << 4) | (1 << 10);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0xA000, 1);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_00FFu, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void Mode5Renders160By128BitmapInsideFramebuffer()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 5 | (1 << 10);
        gba.Bus.Write16(GbaMemoryMap.VramStart, 0x7FFF);
        gba.Bus.Write16(GbaMemoryMap.VramStart + ((127 * 160 + 159) * 2), 0x001F);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_FFFFu, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[127 * VideoController.Width + 159]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[128 * VideoController.Width]);
    }

    [Fact]
    public void Mode4DoesNotRenderBitmapWhenBg2Disabled()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 4;
        gba.Bus.Write16(GbaMemoryMap.PaletteStart, 0x0000);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 1);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void Mode4RendersObjectsWhenBg2Disabled()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)(4 | (1 << 12) | (1 << 6));
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x14000, 0x01);
        for (var sprite = 1; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 512);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void BitmapModeObjectsIgnoreTilesBelow512()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)(4 | (1 << 12) | (1 << 6));
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        for (var sprite = 1; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void Mode0RendersRegularTextBackgroundTile()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 1 << 8; // BG0 enabled, mode 0
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8); // screen block 1, char block 0
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x01);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void Mode0RegularBackgroundTileFetchWrapsWithinBgVram()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 1 << 8; // BG0 enabled, mode 0
        gba.Bus.Write16(IoRegisters.BG0CNT, (ushort)((3 << 2) | (1 << 8))); // screen block 1, char block 3
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x03E0);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0x03FF);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x3FE0, 0x01);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void DebugRegularBgSamplesExposeScrollAndTileMetadata()
    {
        var gba = new GbaSystem();
        gba.Video.DebugRenderingEnabled = true;
        gba.Bus.DisplayControl = 1 << 8; // BG0 enabled, mode 0
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8); // screen block 1, char block 0
        gba.Bus.Write16(IoRegisters.BG0HOFS, 1);
        gba.Bus.Write16(IoRegisters.BG0VOFS, 2);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0x0000);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 2 * 4, 0x10);

        AdvanceToVBlank(gba);

        var sample = gba.Video.RenderDebugRegularBgSamples(0)[0];

        Assert.True(sample.Valid);
        Assert.Equal(0, sample.Bg);
        Assert.Equal(1, sample.SourceX);
        Assert.Equal(2, sample.SourceY);
        Assert.Equal(0, sample.TileX);
        Assert.Equal(0, sample.TileY);
        Assert.Equal(0x800, sample.ScreenOffset);
        Assert.Equal(0, sample.ScreenEntry);
        Assert.Equal(1, sample.PaletteIndex);
        Assert.Equal(1, sample.HOffset);
        Assert.Equal(2, sample.VOffset);
    }

    [Fact]
    public void Mode0BackgroundMosaicRepeatsBlockOriginPixel()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 1 << 8; // BG0 enabled, mode 0
        gba.Bus.Write16(IoRegisters.BG0CNT, (ushort)((1 << 6) | (1 << 8))); // mosaic, screen block 1
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x0011); // 2x2 BG mosaic
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x03E0);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 6, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x21);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 1, 0x02);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 8, 0x03);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[VideoController.Width]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[2]);
    }

    [Fact]
    public void Mode0RendersBasicObject()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6)); // OBJ enabled, 1D mapping
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        gba.Bus.Write16(GbaMemoryMap.OamStart, 0); // y, regular 4bpp square
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0); // x, 8x8
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void ObjectFetchBudgetSuppressesLateSpriteOnOverloadedScanline()
    {
        var gba = CreateObjectFetchBudgetScene(targetIndex: 19);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void HBlankFreeObjectFetchBudgetSuppressesSpritesEarlier()
    {
        var normal = CreateObjectFetchBudgetScene(targetIndex: 15);
        var hblankFree = CreateObjectFetchBudgetScene(targetIndex: 15, hblankFree: true);

        AdvanceToVBlank(normal);
        AdvanceToVBlank(hblankFree);

        Assert.Equal(0xFF00_FF00u, normal.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, hblankFree.Video.Framebuffer[0]);
    }

    [Fact]
    public void HBlankFreeObjectFetchBudgetCompletesTerminalSprite()
    {
        var gba = CreateObjectFetchBudgetScene(targetIndex: 14, hblankFree: true);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[63]);
    }

    [Fact]
    public void AffineObjectsConsumeAdditionalFetchCycles()
    {
        var normal = CreateObjectFetchBudgetScene(targetIndex: 9);
        var affine = CreateObjectFetchBudgetScene(targetIndex: 9, affineFillers: true);

        AdvanceToVBlank(normal);
        AdvanceToVBlank(affine);

        Assert.Equal(0xFF00_FF00u, normal.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, affine.Video.Framebuffer[0]);
    }

    [Fact]
    public void LeftClippedObjectsCanExhaustFetchBudget()
    {
        var gba = CreateObjectFetchBudgetScene(targetIndex: 38, fillerX: 448);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void ObjectMosaicRepeatsBlockOriginPixel()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6)); // OBJ enabled, 1D mapping
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x1100); // 2x2 OBJ mosaic
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 4, 0x03E0);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 6, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x21);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000 + 1, 0x02);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000 + 8, 0x03);
        for (var sprite = 1; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 12); // mosaic OBJ at y 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0); // x 0, 8x8
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[VideoController.Width]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[2]);
    }

    [Fact]
    public void ObjectMosaicLatchCarriesAcrossTransparentPixels()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6));
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x0300); // 4-pixel OBJ mosaic width
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x202, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        HideObjectsExceptFirst(gba.Bus);
        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 12);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[2]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[3]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[4]);
    }

    [Fact]
    public void ObjectMosaicUsesDisplayAlignedBlocksForOffsetSprite()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6));
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x1100); // 2x2 OBJ mosaic
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x202, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x204, 0x03E0);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x206, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x21);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10004, 0x03);
        HideObjectsExceptFirst(gba.Bus);
        gba.Bus.Write16(GbaMemoryMap.OamStart, (ushort)((1 << 12) | 1)); // mosaic, y 1
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 1); // x 1
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[VideoController.Width + 1]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[VideoController.Width + 2]);
        Assert.Equal(0xFF00_00FFu, gba.Video.Framebuffer[2 * VideoController.Width + 1]);
    }

    [Fact]
    public void FlippedObjectMosaicUsesDisplayAlignedBlocks()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6));
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x0100); // 2-pixel OBJ mosaic width
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x202, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x204, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10003, 0x12);
        HideObjectsExceptFirst(gba.Bus);
        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 12);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, (ushort)((1 << 12) | 1)); // hflip, x 1
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[2]);
    }

    [Fact]
    public void AffineObjectMosaicUsesDisplayAlignedBlocks()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6));
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x0100);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x202, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x204, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x21);
        HideObjectsExceptFirst(gba.Bus);
        gba.Bus.Write16(GbaMemoryMap.OamStart, (ushort)((1 << 8) | (1 << 12)));
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 1); // x 1, matrix 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 6, 0x0100); // PA
        gba.Bus.Write16(GbaMemoryMap.OamStart + 30, 0x0100); // PD

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[2]);
    }

    [Fact]
    public void LowerPriorityObjectDoesNotReplaceMosaicLatchMidBlock()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6));
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x0300); // 4-pixel OBJ mosaic width
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x202, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x204, 0x03E0);
        FillObjectTile(gba.Bus, 0, 0x11);
        FillObjectTile(gba.Bus, 1, 0x22);
        for (var sprite = 2; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 12); // priority 0 mosaic OBJ, visible at x 0-1
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 506); // x = -6
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 8, 1 << 12); // priority 1 mosaic OBJ behind it
        gba.Bus.Write16(GbaMemoryMap.OamStart + 10, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 12, (ushort)(1 | (1 << 10)));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[2]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[3]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[4]);
    }

    [Fact]
    public void HigherPriorityObjectReplacesMosaicLatchMidBlock()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6));
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x0300); // 4-pixel OBJ mosaic width
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x202, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x204, 0x03E0);
        FillObjectTile(gba.Bus, 0, 0x11);
        FillObjectTile(gba.Bus, 1, 0x22);
        for (var sprite = 2; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 12); // priority 0 mosaic OBJ starts at x 2
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 2);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 8, 1 << 12); // priority 1 mosaic OBJ
        gba.Bus.Write16(GbaMemoryMap.OamStart + 10, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 12, (ushort)(1 | (1 << 10)));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[2]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[3]);
    }

    [Fact]
    public void Mode0Renders256ColorObjectRowsWithOneDimensionalStride()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6)); // OBJ enabled, 1D mapping
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 4, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 1);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000 + 4 * 32, 2);
        for (var sprite = 1; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, (ushort)(1 << 13)); // 8bpp square at y 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 1 << 14); // x 0, 16x16
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[8 * VideoController.Width]);
    }

    [Fact]
    public void Mode0SpriteTileFetchWrapsWithinObjectVram()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 1 << 12; // OBJ enabled, 2D mapping
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000 + 992, 0x01);
        gba.Bus.Write16(GbaMemoryMap.OamStart, 0); // y, regular 4bpp square
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 3 << 14); // x 0, 64x64
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0x3FF);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_00FFu, gba.Video.Framebuffer[8 * VideoController.Width]);
    }

    [Fact]
    public void ForcedBlankRendersWhiteFrame()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 7) | 3);
        gba.Bus.Write16(GbaMemoryMap.VramStart, 0x001F);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_FFFFu, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void Mode0RendersAffineObjectWithIdentityMatrix()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6)); // OBJ enabled, 1D mapping
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 8); // affine OBJ at y 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0); // x 0, matrix 0, 8x8
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 6, 0x0100); // PA
        gba.Bus.Write16(GbaMemoryMap.OamStart + 30, 0x0100); // PD

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void Mode0RendersDoubleSizeAffineObjectInsideExpandedBounds()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6)); // OBJ enabled, 1D mapping
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        gba.Bus.Write16(GbaMemoryMap.OamStart, 3 << 8); // double-size affine OBJ at y 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0); // x 0, matrix 0, 8x8 source, 16x16 bounds
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 6, 0x0080); // PA, 0.5 scale
        gba.Bus.Write16(GbaMemoryMap.OamStart + 30, 0x0080); // PD, 0.5 scale

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[15 * VideoController.Width + 15]);
    }

    [Fact]
    public void Mode2RendersAffineBackgroundWithIdentityMatrix()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 2 | (1 << 10); // BG2 enabled, mode 2
        gba.Bus.Write16(IoRegisters.BG2CNT, (ushort)((1 << 8) | (1 << 13))); // screen block 1, wrap
        gba.Bus.Write16(IoRegisters.BG2PA, 0x0100);
        gba.Bus.Write16(IoRegisters.BG2PD, 0x0100);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 1);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_00FFu, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void Mode2AffineBackgroundCanWrap()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 2 | (1 << 10); // BG2 enabled, mode 2
        gba.Bus.Write16(IoRegisters.BG2CNT, (ushort)((1 << 8) | (1 << 13))); // screen block 1, wrap
        gba.Bus.Write16(IoRegisters.BG2PA, 0x0100);
        gba.Bus.Write16(IoRegisters.BG2PD, 0x0100);
        gba.Bus.Write32(IoRegisters.BG2X, 127 << 8);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 7, 1);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void DebugAffineSamplesExposeSourceAndTileMetadata()
    {
        var gba = new GbaSystem();
        gba.Video.DebugRenderingEnabled = true;
        gba.Bus.DisplayControl = 2 | (1 << 10); // BG2 enabled, mode 2
        gba.Bus.Write16(IoRegisters.BG2CNT, (ushort)((1 << 8) | (1 << 13))); // screen block 1, wrap
        gba.Bus.Write16(IoRegisters.BG2PA, 0x0100);
        gba.Bus.Write16(IoRegisters.BG2PD, 0x0100);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 1);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 1, 2);

        AdvanceToVBlank(gba);

        var samples = gba.Video.RenderDebugAffineSamples(2);
        var first = samples[0];
        var second = samples[1];

        Assert.True(first.Valid);
        Assert.Equal(2, first.Bg);
        Assert.Equal(0, first.SourceX);
        Assert.Equal(0, first.SourceY);
        Assert.Equal(0, first.TileX);
        Assert.Equal(0, first.TileY);
        Assert.Equal(0x800, first.MapOffset);
        Assert.Equal(0, first.TileNumber);
        Assert.Equal(0, first.TileOffset);
        Assert.Equal(1, first.PaletteIndex);
        Assert.Equal(0x0100, first.Pa);
        Assert.Equal(0x0100, first.Pd);
        Assert.True(second.Valid);
        Assert.Equal(1, second.SourceX);
        Assert.Equal(2, second.PaletteIndex);
    }

    [Fact]
    public void Mode2AffineBackgroundMosaicRepeatsBlockOriginPixel()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 2 | (1 << 10); // BG2 enabled, mode 2
        gba.Bus.Write16(IoRegisters.BG2CNT, (ushort)((1 << 6) | (1 << 8) | (1 << 13))); // mosaic, screen block 1, wrap
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x0011); // 2x2 BG mosaic
        gba.Bus.Write16(IoRegisters.BG2PA, 0x0100);
        gba.Bus.Write16(IoRegisters.BG2PD, 0x0100);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x03E0);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 6, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 1);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 2, 2);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 8, 3);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[VideoController.Width]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[2]);
    }

    [Fact]
    public void AffineBackgroundReferenceWriteDuringHBlankSetsNextScanlineOrigin()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 2 | (1 << 10); // BG2 enabled, mode 2
        gba.Bus.Write16(IoRegisters.BG2CNT, (ushort)((1 << 8) | (1 << 13))); // screen block 1, wrap
        gba.Bus.Write16(IoRegisters.BG2PA, 0x0100);
        gba.Bus.Write16(IoRegisters.BG2PD, 0x0100);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 1);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 1, 2);

        gba.Scheduler.Advance(VideoController.HDrawCycles);
        gba.Bus.Write32(IoRegisters.BG2X, 1 << 8);
        gba.Bus.Write32(IoRegisters.BG2Y, 0);
        gba.Scheduler.Advance(VideoController.HBlankCycles + VideoController.HDrawCycles);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_FF00u, gba.Video.Framebuffer[VideoController.Width]);
    }

    [Fact]
    public void DebugLayerReturnsCapturedAffineScanlines()
    {
        var gba = new GbaSystem();
        gba.Video.DebugRenderingEnabled = true;
        gba.Bus.DisplayControl = 2 | (1 << 10); // BG2 enabled, mode 2
        gba.Bus.Write16(IoRegisters.BG2CNT, (ushort)((1 << 8) | (1 << 13))); // screen block 1, wrap
        gba.Bus.Write16(IoRegisters.BG2PA, 0x0100);
        gba.Bus.Write16(IoRegisters.BG2PD, 0x0100);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 1);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 1, 2);

        gba.Scheduler.Advance(VideoController.HDrawCycles);
        gba.Bus.Write32(IoRegisters.BG2X, 1 << 8);
        gba.Bus.Write32(IoRegisters.BG2Y, 0);
        gba.Scheduler.Advance(VideoController.HBlankCycles + VideoController.HDrawCycles);

        var before = gba.Video.Framebuffer.ToArray();
        var bg2 = gba.Video.RenderDebugLayer(2);

        Assert.Equal(0xFFFF_0000u, bg2[0]);
        Assert.Equal(0xFF00_FF00u, bg2[VideoController.Width]);
        Assert.Equal(before, gba.Video.Framebuffer.ToArray());
    }

    [Fact]
    public void BrightnessDecreaseCanFadeTargetLayer()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = 1 << 8; // BG0 enabled, mode 0
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x7FFF);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x01);
        gba.Bus.Write16(IoRegisters.BLDCNT, (ushort)((3 << 6) | 1)); // darken BG0
        gba.Bus.Write16(IoRegisters.BLDY, 8);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF80_8080u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void AlphaBlendCombinesTopAndSecondTargetLayers()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (1 << 8) | (1 << 9); // BG0/BG1 enabled, mode 0
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8); // priority 0, screen block 1
        gba.Bus.Write16(IoRegisters.BG1CNT, (ushort)((1 << 0) | (2 << 8))); // priority 1, screen block 2
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x01);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 32, 0x02);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x1000, 1);
        gba.Bus.Write16(IoRegisters.BLDCNT, (ushort)((1 << 6) | 0x0001 | (0x0002 << 8)));
        gba.Bus.Write16(IoRegisters.BLDALPHA, 8 | (8 << 8));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF7F_007Fu, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void DebugCompositionCapturesPreBlendAndSecondTarget()
    {
        var gba = new GbaSystem();
        gba.Video.DebugRenderingEnabled = true;
        gba.Bus.DisplayControl = (1 << 8) | (1 << 9); // BG0/BG1 enabled, mode 0
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8); // priority 0, screen block 1
        gba.Bus.Write16(IoRegisters.BG1CNT, (ushort)((1 << 0) | (2 << 8))); // priority 1, screen block 2
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x01);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 32, 0x02);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x1000, 1);
        gba.Bus.Write16(IoRegisters.BLDCNT, (ushort)((1 << 6) | 0x0001 | (0x0002 << 8)));
        gba.Bus.Write16(IoRegisters.BLDALPHA, 8 | (8 << 8));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.RenderDebugPreBlend()[0]);
        Assert.Equal(0xFF00_00FFu, gba.Video.RenderDebugSecondTarget()[0]);
        Assert.Equal(0xFFFF_0000u, gba.Video.RenderDebugTopLayerMap()[0]);
        Assert.Equal(0xFF00_FF00u, gba.Video.RenderDebugSecondLayerMap()[0]);
        Assert.Equal(0xFF7F_007Fu, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void SemiTransparentObjectForcesAlphaBlendWithSecondTarget()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 8) | (1 << 12) | (1 << 6)); // BG0/OBJ, 1D OBJ mapping
        gba.Bus.Write16(IoRegisters.BG0CNT, (ushort)((1 << 0) | (1 << 8))); // priority 1, screen block 1
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x7C00);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x01);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        for (var sprite = 1; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 10); // semi-transparent OBJ
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(IoRegisters.BLDCNT, (ushort)((1 << 6) | (0x0001 << 8))); // alpha, BG0 second target only
        gba.Bus.Write16(IoRegisters.BLDALPHA, 8 | (8 << 8));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF7F_007Fu, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void SemiTransparentObjectDoesNotBlendWithAnotherObject()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6)); // OBJ, 1D OBJ mapping
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 4, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x11);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10020, 0x22);
        for (var sprite = 2; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 10); // top, semi-transparent OBJ 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 8, 0); // normal OBJ 1 behind OBJ 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 10, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 12, 1);
        gba.Bus.Write16(IoRegisters.BLDCNT, 1 << 12); // OBJ second target only
        gba.Bus.Write16(IoRegisters.BLDALPHA, 8 | (8 << 8));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void SemiTransparentObjectBlendsWithBackgroundBehindAnotherObject()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 8) | (1 << 12) | (1 << 6)); // BG0/OBJ, 1D OBJ mapping
        gba.Bus.Write16(IoRegisters.BG0CNT, (ushort)((1 << 0) | (1 << 8))); // priority 1, screen block 1
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x7C00);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 4, 0x03E0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x11);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x11);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10020, 0x22);
        for (var sprite = 2; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 10); // top, semi-transparent OBJ 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 8, 0); // normal OBJ 1 behind OBJ 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 10, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 12, 1);
        gba.Bus.Write16(IoRegisters.BLDCNT, 1 << 8); // BG0 second target only
        gba.Bus.Write16(IoRegisters.BLDALPHA, 8 | (8 << 8));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF7F_007Fu, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void SemiTransparentObjectBlendsWhenSpecialEffectIsDisabled()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 8) | (1 << 12) | (1 << 6)); // BG0/OBJ, 1D OBJ mapping
        gba.Bus.Write16(IoRegisters.BG0CNT, (ushort)((1 << 0) | (1 << 8))); // priority 1, screen block 1
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x7C00);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x01);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        for (var sprite = 1; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 10); // semi-transparent OBJ
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(IoRegisters.BLDCNT, 0x0001 << 8); // BG0 second target only, no selected special effect
        gba.Bus.Write16(IoRegisters.BLDALPHA, 8 | (8 << 8));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF7F_007Fu, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void SemiTransparentObjectBlendTakesPriorityOverBrightness()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 8) | (1 << 12) | (1 << 6)); // BG0/OBJ, 1D OBJ mapping
        gba.Bus.Write16(IoRegisters.BG0CNT, (ushort)((1 << 0) | (1 << 8))); // priority 1, screen block 1
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x7C00);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x200 + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x01);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        for (var sprite = 1; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }

        gba.Bus.Write16(GbaMemoryMap.OamStart, 1 << 10); // semi-transparent OBJ
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(IoRegisters.BLDCNT, (ushort)((3 << 6) | (1 << 4) | (0x0001 << 8))); // darken OBJ, BG0 second target
        gba.Bus.Write16(IoRegisters.BLDALPHA, 8 | (8 << 8));
        gba.Bus.Write16(IoRegisters.BLDY, 16);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF7F_007Fu, gba.Video.Framebuffer[0]);
    }

    [Fact]
    public void Win0CanMaskBackgroundOutsideWindow()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (1 << 8) | (1 << 13); // BG0 and WIN0 enabled
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x11);
        gba.Bus.Write16(IoRegisters.WIN0H, 1); // left 0, right 1
        gba.Bus.Write16(IoRegisters.WIN0V, VideoController.Height); // top 0, bottom 160
        gba.Bus.Write16(IoRegisters.WININ, 1); // BG0 visible inside WIN0
        gba.Bus.Write16(IoRegisters.WINOUT, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void Win0WrappedRangeCanMaskBackgroundAcrossScreenEdge()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (1 << 8) | (1 << 13); // BG0 and WIN0 enabled
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        for (var i = 0; i < 32; i++)
        {
            WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + (uint)i, 0x11);
        }

        gba.Bus.Write16(IoRegisters.WIN0H, (ushort)((239 << 8) | 2)); // x >= 239 or x < 2
        gba.Bus.Write16(IoRegisters.WIN0V, VideoController.Height);
        gba.Bus.Write16(IoRegisters.WININ, 1);
        gba.Bus.Write16(IoRegisters.WINOUT, 0);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[1]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[2]);
        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[239]);
    }

    [Fact]
    public void Win0CanMaskAlphaBlendEffect()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (1 << 8) | (1 << 9) | (1 << 13); // BG0/BG1 and WIN0 enabled
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8);
        gba.Bus.Write16(IoRegisters.BG1CNT, (ushort)((1 << 0) | (2 << 8)));
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 4, 0x7C00);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x11);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 32, 0x22);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x800, 0);
        gba.Bus.Write16(GbaMemoryMap.VramStart + 0x1000, 1);
        gba.Bus.Write16(IoRegisters.WIN0H, 1); // left 0, right 1
        gba.Bus.Write16(IoRegisters.WIN0V, VideoController.Height);
        gba.Bus.Write16(IoRegisters.WININ, 0x0003); // BG0/BG1 visible, effects disabled
        gba.Bus.Write16(IoRegisters.WINOUT, 0x0023); // BG0/BG1 visible, effects enabled
        gba.Bus.Write16(IoRegisters.BLDCNT, (ushort)((1 << 6) | 0x0001 | (0x0002 << 8)));
        gba.Bus.Write16(IoRegisters.BLDALPHA, 8 | (8 << 8));

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF7F_007Fu, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void ObjectWindowCanMaskBackground()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (1 << 8) | (1 << 12) | (1 << 15) | (1 << 6); // BG0/OBJ/OBJWIN, 1D OBJ mapping
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x11);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        gba.Bus.Write16(GbaMemoryMap.OamStart, 2 << 10); // OBJ-window sprite at y 0
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(IoRegisters.WINOUT, 1 << 8); // BG0 visible only inside OBJ window

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void ObjectWindowIgnoresObjectMosaic()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (1 << 8) | (1 << 12) | (1 << 15) | (1 << 6);
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8);
        gba.Bus.Write16(IoRegisters.MOSAIC, 0x0100);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x11);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        HideObjectsExceptFirst(gba.Bus);
        gba.Bus.Write16(GbaMemoryMap.OamStart, (ushort)((2 << 10) | (1 << 12)));
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(IoRegisters.WINOUT, 1 << 8);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFFFF_0000u, gba.Video.Framebuffer[0]);
        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[1]);
    }

    [Fact]
    public void ObjectWindowRequiresObjectMasterEnable()
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (1 << 8) | (1 << 15) | (1 << 6); // BG0/OBJWIN, OBJ disabled
        gba.Bus.Write16(IoRegisters.BG0CNT, 1 << 8);
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 2, 0x001F);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart, 0x11);
        WriteVideoByte(gba.Bus, GbaMemoryMap.VramStart + 0x10000, 0x01);
        HideObjectsExceptFirst(gba.Bus);
        gba.Bus.Write16(GbaMemoryMap.OamStart, 2 << 10);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 2, 0);
        gba.Bus.Write16(GbaMemoryMap.OamStart + 4, 0);
        gba.Bus.Write16(IoRegisters.WINOUT, 1 << 8);

        AdvanceToVBlank(gba);

        Assert.Equal(0xFF00_0000u, gba.Video.Framebuffer[0]);
    }

    private static void AdvanceToVBlank(GbaSystem gba)
        => gba.Scheduler.Advance(VideoController.CyclesPerScanline * VideoController.VisibleLines);

    private static GbaSystem CreateObjectFetchBudgetScene(
        int targetIndex,
        bool hblankFree = false,
        bool affineFillers = false,
        int fillerX = 0)
    {
        var gba = new GbaSystem();
        gba.Bus.DisplayControl = (ushort)((1 << 12) | (1 << 6) | (hblankFree ? 1 << 5 : 0));
        gba.Bus.Write16(GbaMemoryMap.PaletteStart + 0x202, 0x03E0);
        for (var tile = 1; tile <= 64; tile++)
        {
            FillObjectTile(gba.Bus, tile, 0x11);
        }

        for (var sprite = 0; sprite < 128; sprite++)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 2 << 8);
        }

        for (var sprite = 0; sprite < targetIndex; sprite++)
        {
            var offset = GbaMemoryMap.OamStart + (uint)(sprite * 8);
            gba.Bus.Write16(offset, (ushort)(affineFillers ? 1 << 8 : 0));
            gba.Bus.Write16(offset + 2, (ushort)((3 << 14) | fillerX));
            gba.Bus.Write16(offset + 4, 0);
        }

        var targetOffset = GbaMemoryMap.OamStart + (uint)(targetIndex * 8);
        gba.Bus.Write16(targetOffset, 0);
        gba.Bus.Write16(targetOffset + 2, 3 << 14);
        gba.Bus.Write16(targetOffset + 4, 1);
        if (affineFillers)
        {
            gba.Bus.Write16(GbaMemoryMap.OamStart + 6, 0x0100);
            gba.Bus.Write16(GbaMemoryMap.OamStart + 30, 0x0100);
        }

        return gba;
    }

    private static void HideObjectsExceptFirst(MemoryBus bus)
    {
        for (var sprite = 1; sprite < 128; sprite++)
        {
            bus.Write16(GbaMemoryMap.OamStart + (uint)(sprite * 8), 160);
        }
    }

    private static void FillObjectTile(MemoryBus bus, int tile, byte packedColor)
    {
        var start = GbaMemoryMap.VramStart + 0x10000u + (uint)(tile * 32);
        for (uint offset = 0; offset < 32; offset++)
        {
            WriteVideoByte(bus, start + offset, packedColor);
        }
    }

    private static void WriteVideoByte(MemoryBus bus, uint address, byte value)
    {
        var aligned = address & ~1u;
        var current = bus.Read16(aligned);
        var merged = (address & 1) == 0
            ? (ushort)((current & 0xFF00) | value)
            : (ushort)((current & 0x00FF) | (value << 8));
        bus.Write16(aligned, merged);
    }

    [Fact]
    public void CpuPollingDispstatCanObserveVBlank()
    {
        var gba = new GbaSystem();
        gba.Cpu[0] = IoRegisters.DISPSTAT;
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Bus.Write32(GbaMemoryMap.EwramStart, 0xE1D0_10B0);     // ldrh r1, [r0]
        gba.Bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE211_1001); // ands r1, r1, #1
        gba.Bus.Write32(GbaMemoryMap.EwramStart + 8, 0x0AFF_FFFC); // beq -4

        for (var i = 0; i < 250_000 && gba.Cpu.Pc != GbaMemoryMap.EwramStart + 12; i++)
        {
            gba.Step();
        }

        Assert.Equal(GbaMemoryMap.EwramStart + 12, gba.Cpu.Pc);
        Assert.NotEqual(0u, gba.Cpu[1]);
    }
}
