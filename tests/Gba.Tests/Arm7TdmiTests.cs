using Gba.Core.Cpu;
using Gba.Core.Memory;

namespace Gba.Tests;

public sealed class Arm7TdmiTests
{
    [Fact]
    public void ResetWithoutBiosStartsAtRomEntryPointWithBootHandoffState()
    {
        var cpu = new Arm7Tdmi(new MemoryBus());

        cpu.Reset(useBios: false);

        Assert.Equal(GbaMemoryMap.RomEntryPoint, cpu.Pc);
        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.False(cpu.ThumbState);
        Assert.False(cpu.IrqDisabled);
        Assert.Equal(0x0300_7F00u, cpu[13]);
        Assert.Equal(GbaMemoryMap.RomEntryPoint, cpu[14]);
    }

    [Fact]
    public void Fetch32ReadsInstructionAndAdvancesPc()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE3A0_0001);
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        var instruction = cpu.Fetch32();

        Assert.Equal(0xE3A0_0001u, instruction);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void StepExecutesArmMovImmediate()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE3A0_0042); // mov r0, #0x42
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        var cycles = cpu.Step();

        Assert.Equal(1, cycles);
        Assert.Equal(0x42u, cpu[0]);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void ArmSequentialFetchUsesPrefetchedInstructionAfterSelfModifyingStore()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE580_1000); // str r1, [r0]
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE3A0_2001); // mov r2, #1
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.EwramStart + 4,
            [1] = 0xE3A0_2002, // mov r2, #2
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0xE3A0_2002u, bus.Read32(GbaMemoryMap.EwramStart + 4));
        Assert.Equal(1u, cpu[2]);
    }

    [Fact]
    public void ArmRegisterShiftReadsPcAsInstructionPlusTwelveForRm()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE1B0_431F); // movs r4, r15, lsl r3
        var cpu = new Arm7Tdmi(bus)
        {
            [3] = 0,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(GbaMemoryMap.EwramStart + 12, cpu[4]);
    }

    [Fact]
    public void ArmLoadWithWritebackDoesNotOverwriteLoadedValueWhenBaseIsDestination()
    {
        var bus = new MemoryBus();
        var dataAddress = GbaMemoryMap.EwramStart + 0x100;
        bus.Write32(GbaMemoryMap.EwramStart, 0xE5B0_0004); // ldr r0, [r0, #4]!
        bus.Write32(dataAddress + 4, 0x1234_5678);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = dataAddress,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x1234_5678u, cpu[0]);
    }

    [Fact]
    public void ArmLoadMultipleWithWritebackDoesNotOverwriteLoadedBaseRegister()
    {
        var bus = new MemoryBus();
        var dataAddress = GbaMemoryMap.EwramStart + 0x100;
        bus.Write32(GbaMemoryMap.EwramStart, 0xE8B1_0003); // ldmia r1!, {r0,r1}
        bus.Write32(dataAddress, 0x1111_1111);
        bus.Write32(dataAddress + 4, 0x2222_2222);
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = dataAddress,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x1111_1111u, cpu[0]);
        Assert.Equal(0x2222_2222u, cpu[1]);
    }

    [Fact]
    public void StepExecutesArmCmpAndConditionalInstruction()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE3A0_0001);     // mov r0, #1
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE350_0001); // cmp r0, #1
        bus.Write32(GbaMemoryMap.EwramStart + 8, 0x03A0_1007); // moveq r1, #7
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.True(cpu.ZeroFlag);
        Assert.Equal(7u, cpu[1]);
    }

    [Fact]
    public void StepExecutesArmBranch()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEA00_0000); // b +0, target is PC + 8
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(GbaMemoryMap.EwramStart + 8, cpu.Pc);
    }

    [Fact]
    public void StepExecutesThumbMovImmediateAfterBranchExchange()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        bus.Write16(GbaMemoryMap.IwramStart, 0x212A);      // mov r1, #42
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.True(cpu.ThumbState);
        Assert.Equal(42u, cpu[1]);
        Assert.Equal(GbaMemoryMap.IwramStart + 2, cpu.Pc);
    }

    [Fact]
    public void StepExecutesThumbBranchExchangePcWithPipelineOffset()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10);       // bx r0
        bus.Write16(GbaMemoryMap.IwramStart, 0x4778);            // bx pc
        bus.Write16(GbaMemoryMap.IwramStart + 2, 0x46C0);        // nop/alignment
        bus.Write32(GbaMemoryMap.IwramStart + 4, 0xE3A0_1007);   // mov r1, #7
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.False(cpu.ThumbState);
        Assert.Equal(7u, cpu[1]);
        Assert.Equal(GbaMemoryMap.IwramStart + 8, cpu.Pc);
    }

    [Fact]
    public void StepExecutesArmLogicalShiftedRegisterOperand()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE1A0_1200); // mov r1, r0, lsl #4
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0x12,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x120u, cpu[1]);
    }

    [Fact]
    public void StepExecutesArmStoreAndLoadWord()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE580_1004);     // str r1, [r0, #4]
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE590_2004); // ldr r2, [r0, #4]
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = 0xCAFE_BABEu,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0xCAFE_BABEu, bus.Read32(GbaMemoryMap.IwramStart + 4));
        Assert.Equal(0xCAFE_BABEu, cpu[2]);
    }

    [Fact]
    public void StepExecutesArmStoreAndLoadByte()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE5C0_1001);     // strb r1, [r0, #1]
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE5D0_2001); // ldrb r2, [r0, #1]
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = 0x1234_56AB,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0xABu, cpu[2]);
    }

    [Fact]
    public void StepExecutesArmBranchWithLink()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEB00_0000); // bl +0
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu[14]);
        Assert.Equal(GbaMemoryMap.EwramStart + 8, cpu.Pc);
    }

    [Fact]
    public void StepExecutesArmMultiplyAndMultiplyAccumulate()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE002_0190);     // mul r2, r0, r1
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE023_2190); // mla r3, r0, r1, r2
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 6,
            [1] = 7,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(42u, cpu[2]);
        Assert.Equal(84u, cpu[3]);
    }

    [Fact]
    public void ArmMultiplyUsesMultiplierOperandTiming()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE000_0192); // mul r0, r2, r1
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = 0x0100_0000,
            [2] = 3,
            [15] = GbaMemoryMap.EwramStart
        };

        var cycles = cpu.Step();

        Assert.Equal(5, cycles);
        Assert.Equal(0x0300_0000u, cpu[0]);
    }

    [Fact]
    public void StepExecutesThumbAddSubtractRegister()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x1851);      // add r1, r2, r1
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = 3,
            [2] = 4,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(7u, cpu[1]);
    }

    [Fact]
    public void StepExecutesThumbAluShiftAndUpdatesCarry()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x4088);      // lsl r0, r1
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0x8000_0000,
            [1] = 1,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0u, cpu[0]);
        Assert.True(cpu.CarryFlag);
        Assert.True(cpu.ZeroFlag);
    }

    [Fact]
    public void StepExecutesThumbAluCmpWithoutClobberingDestination()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x4286);      // cmp r6, r0
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0x6873_6D53,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [6] = 0x6873_6D53,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.True(cpu.ZeroFlag);
        Assert.Equal(0x6873_6D53u, cpu[6]);
    }

    [Fact]
    public void StepExecutesThumbAluTstAsBitwiseAnd()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x4211);      // tst r1, r2
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = 0b0001,
            [2] = 0b0001,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.False(cpu.ZeroFlag);
        Assert.Equal(0b0001u, cpu[1]);
    }

    [Theory]
    [InlineData(0x8000_0000u, 0u, 0x8000_0000u, true)]
    [InlineData(0x8000_0000u, 16u, 0x0000_8000u, false)]
    [InlineData(0x8000_0000u, 32u, 0x8000_0000u, true)]
    [InlineData(0x8000_0000u, 66u, 0x2000_0000u, false)]
    public void StepExecutesThumbAluRotateRightEdgeCases(uint value, uint amount, uint expected, bool expectedCarry)
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x0852);      // lsr r2, r2, #1
        bus.Write16(GbaMemoryMap.IwramStart + 2, 0x41D8);  // ror r0, r3
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = value,
            [2] = 1,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu[3] = amount;
        cpu.Step();
        cpu.Step();

        Assert.Equal(expected, cpu[0]);
        Assert.Equal(expectedCarry, cpu.CarryFlag);
    }

    [Fact]
    public void StepExecutesThumbStoreAndLoadImmediateWord()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x6051);      // str r1, [r2, #4]
        bus.Write16(GbaMemoryMap.IwramStart + 2, 0x6850);  // ldr r0, [r2, #4]
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = 0x0102_0304,
            [2] = GbaMemoryMap.IwramStart + 0x100,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(0x0102_0304u, cpu[0]);
    }

    [Fact]
    public void StepExecutesThumbConditionalBranch()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x2001);      // mov r0, #1
        bus.Write16(GbaMemoryMap.IwramStart + 2, 0x2801);  // cmp r0, #1
        bus.Write16(GbaMemoryMap.IwramStart + 4, 0xD000);  // beq +0
        var cpu = new Arm7Tdmi(bus)
        {
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(GbaMemoryMap.IwramStart + 8, cpu.Pc);
    }

    [Fact]
    public void StepExecutesThumbPushAndPop()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0xB503);      // push {r0, r1, lr}
        bus.Write16(GbaMemoryMap.IwramStart + 2, 0xBD0C);  // pop {r2, r3, pc}
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0x1111_1111,
            [1] = 0x2222_2222,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [13] = GbaMemoryMap.IwramStart + 0x200,
            [14] = GbaMemoryMap.IwramStart + 0x40,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(0x1111_1111u, cpu[2]);
        Assert.Equal(0x2222_2222u, cpu[3]);
        Assert.Equal(GbaMemoryMap.IwramStart + 0x40, cpu.Pc);
        Assert.Equal(GbaMemoryMap.IwramStart + 0x200, cpu[13]);
    }

    [Fact]
    public void StepExecutesThumbStoreAndLoadMultiple()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0xC006);      // stmia r0!, {r1, r2}
        bus.Write16(GbaMemoryMap.IwramStart + 2, 0xC818);  // ldmia r0!, {r3, r4}
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x100,
            [1] = 0xAAAA_BBBB,
            [2] = 0xCCCC_DDDD,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu[0] = GbaMemoryMap.IwramStart + 0x100;
        cpu.Step();

        Assert.Equal(0xAAAA_BBBBu, cpu[3]);
        Assert.Equal(0xCCCC_DDDDu, cpu[4]);
        Assert.Equal(GbaMemoryMap.IwramStart + 0x108, cpu[0]);
    }

    [Fact]
    public void StepExecutesThumbLoadMultipleFromWordAlignedAddress()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0xC801);      // ldmia r0!, {r0}
        bus.Write32(GbaMemoryMap.IwramStart + 0x100, 0x1122_3344);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x101,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x1122_3344u, cpu[0]);
    }

    [Fact]
    public void StepExecutesThumbLoadMultipleWithoutWritebackWhenBaseIsLoaded()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0xC902);      // ldmia r1!, {r1}
        bus.Write32(GbaMemoryMap.IwramStart + 0x100, 0x1234_5678);
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x1234_5678u, cpu[1]);
    }

    [Fact]
    public void ThumbStoreMultipleStoresUpdatedBaseWhenBaseIsNotFirst()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0xC103);      // stmia r1!, {r0, r1}
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0x1234_5678,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x1234_5678u, bus.Read32(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(GbaMemoryMap.IwramStart + 0x108, bus.Read32(GbaMemoryMap.IwramStart + 0x104));
        Assert.Equal(GbaMemoryMap.IwramStart + 0x108, cpu[1]);
    }

    [Fact]
    public void ThumbStoreMultipleWithEmptyListStoresPipelinePcAndAdvancesBase()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0xC000);      // stmia r0!, {}
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x100,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(GbaMemoryMap.IwramStart + 6, bus.Read32(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(GbaMemoryMap.IwramStart + 0x140, cpu[0]);
    }

    [Fact]
    public void StepExecutesArmHalfwordAndSignedTransfers()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE1C0_10B0);      // strh r1, [r0]
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE1D0_20B0);  // ldrh r2, [r0]
        bus.Write32(GbaMemoryMap.EwramStart + 8, 0xE1D0_30D0);  // ldrsb r3, [r0]
        bus.Write32(GbaMemoryMap.EwramStart + 12, 0xE1D0_40F0); // ldrsh r4, [r0]
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x300,
            [1] = 0xFF80,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(0xFF80u, cpu[2]);
        Assert.Equal(0xFFFF_FF80u, cpu[3]);
        Assert.Equal(0xFFFF_FF80u, cpu[4]);
    }

    [Fact]
    public void ArmImmediatePostIndexedSignedByteLoadIsNotDecodedAsLongMultiply()
    {
        var bus = new MemoryBus();
        var dataAddress = GbaMemoryMap.IwramStart + 0x300;
        bus.Write32(GbaMemoryMap.EwramStart, 0xE0D1_30D1); // ldrsb r3, [r1], #1
        bus.Write8(dataAddress, 0x80);
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = dataAddress,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(dataAddress + 1, cpu[1]);
        Assert.Equal(0xFFFF_FF80u, cpu[3]);
    }

    [Fact]
    public void ArmSignedHalfwordStoreEncodingEntersUndefinedException()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE180_10F2); // undefined signed-halfword store encoding
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x300,
            [1] = 0x1234,
            [2] = 2,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(CpuMode.Undefined, cpu.Mode);
        Assert.Equal(0x04u, cpu.Pc);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu[14]);
        Assert.Equal(0u, bus.Read16(GbaMemoryMap.IwramStart + 0x300));
    }

    [Fact]
    public void ArmLongMultiplyExecutesValidLowOpcodeBits()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE08C_629F); // umull r6, r12, r15, r2
        var cpu = new Arm7Tdmi(bus)
        {
            [2] = 0x0A25,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        var expected = (ulong)(GbaMemoryMap.EwramStart + 4) * 0x0A25UL;
        Assert.Equal((uint)expected, cpu[6]);
        Assert.Equal((uint)(expected >> 32), cpu[12]);
        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void ArmLongMultiplyAcceptsGeneratedCodeLowOpcodeBitQuirkWhenBitTwentyIsClear()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE08C_62FF); // observed generated umull-family encoding with low bits 6-5 set
        var cpu = new Arm7Tdmi(bus)
        {
            [2] = 0x0A25,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        var expected = (ulong)(GbaMemoryMap.EwramStart + 4) * 0x0A25UL;
        Assert.Equal((uint)expected, cpu[6]);
        Assert.Equal((uint)(expected >> 32), cpu[12]);
        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void StepExecutesArmSingleDataSwapWord()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE100_1092); // swp r1, r2, [r0]
        bus.Write32(GbaMemoryMap.IwramStart + 0x300, 0x1234_5678);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x300,
            [2] = 0xAABB_CCDD,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x1234_5678u, cpu[1]);
        Assert.Equal(0xAABB_CCDDu, bus.Read32(GbaMemoryMap.IwramStart + 0x300));
    }

    [Fact]
    public void StepExecutesArmSingleDataSwapByte()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE140_1092); // swpb r1, r2, [r0]
        bus.Write8(GbaMemoryMap.IwramStart + 0x300, 0x78);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x300,
            [2] = 0xAABB_CCDD,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x78u, cpu[1]);
        Assert.Equal(0xDD, bus.Read8(GbaMemoryMap.IwramStart + 0x300));
    }

    [Fact]
    public void StepExecutesArmBlockDataTransfer()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE880_0006);     // stmia r0, {r1, r2}
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE890_0018); // ldmia r0, {r3, r4}
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x380,
            [1] = 0x1357_9BDF,
            [2] = 0x2468_ACE0,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x1357_9BDFu, cpu[3]);
        Assert.Equal(0x2468_ACE0u, cpu[4]);
    }

    [Fact]
    public void ArmStoreMultipleWithWritebackStoresUpdatedBaseWhenBaseIsNotFirst()
    {
        var bus = new MemoryBus();
        var dataAddress = GbaMemoryMap.IwramStart + 0x380;
        bus.Write32(GbaMemoryMap.EwramStart, 0xE8A1_0003); // stmia r1!, {r0,r1}
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0xCAFE_BABE,
            [1] = dataAddress,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0xCAFE_BABEu, bus.Read32(dataAddress));
        Assert.Equal(dataAddress + 8, bus.Read32(dataAddress + 4));
        Assert.Equal(dataAddress + 8, cpu[1]);
    }

    [Fact]
    public void ArmStoreMultipleDecrementBeforeWritesFullDescendingStackLayout()
    {
        var bus = new MemoryBus();
        var stackTop = GbaMemoryMap.IwramStart + 0x400;
        bus.Write32(GbaMemoryMap.EwramStart, 0xE92D_4003); // stmdb sp!, {r0,r1,lr}
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0x1111_1111,
            [1] = 0x2222_2222,
            [13] = stackTop,
            [14] = 0xEEEE_EEEE,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(stackTop - 12, cpu[13]);
        Assert.Equal(0x1111_1111u, bus.Read32(stackTop - 12));
        Assert.Equal(0x2222_2222u, bus.Read32(stackTop - 8));
        Assert.Equal(0xEEEE_EEEEu, bus.Read32(stackTop - 4));
    }

    [Fact]
    public void ArmLoadMultipleIncrementAfterRestoresFullDescendingStackLayout()
    {
        var bus = new MemoryBus();
        var stackBase = GbaMemoryMap.IwramStart + 0x400;
        bus.Write32(GbaMemoryMap.EwramStart, 0xE8BD_4003); // ldmia sp!, {r0,r1,lr}
        bus.Write32(stackBase, 0x1111_1111);
        bus.Write32(stackBase + 4, 0x2222_2222);
        bus.Write32(stackBase + 8, 0xEEEE_EEEE);
        var cpu = new Arm7Tdmi(bus)
        {
            [13] = stackBase,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x1111_1111u, cpu[0]);
        Assert.Equal(0x2222_2222u, cpu[1]);
        Assert.Equal(stackBase + 12, cpu[13]);
        Assert.Equal(0xEEEE_EEEEu, cpu[14]);
    }

    [Fact]
    public void StepExecutesArmSoftwareInterruptExceptionEntry()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        bios[0] = 1;
        var bus = new MemoryBus(bios);
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF00_0000); // swi 0
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(CpuMode.Supervisor, cpu.Mode);
        Assert.False(cpu.ThumbState);
        Assert.True(cpu.IrqDisabled);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu[14]);
        Assert.Equal(0x08u, cpu.Pc);
    }

    [Fact]
    public void StepExecutesThumbSignedLoads()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x5650);      // ldrsb r0, [r2, r1]
        bus.Write16(GbaMemoryMap.IwramStart + 2, 0x5ED4);  // ldrsh r4, [r2, r3]
        bus.Write8(GbaMemoryMap.IwramStart + 0x401, 0x80);
        bus.Write16(GbaMemoryMap.IwramStart + 0x402, 0x8001);
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = 1,
            [2] = GbaMemoryMap.IwramStart + 0x400,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu[3] = 2;
        cpu.Step();

        Assert.Equal(0xFFFF_FF80u, cpu[0]);
        Assert.Equal(0xFFFF_8001u, cpu[4]);
    }

    [Fact]
    public void StepExecutesThumbUnalignedHalfwordLoadsWithGbaSemantics()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF13); // bx r3
        bus.Write16(GbaMemoryMap.IwramStart, 0x5A50);      // ldrh r0, [r2, r1]
        bus.Write16(GbaMemoryMap.IwramStart + 2, 0x5E54);  // ldrsh r4, [r2, r1]
        bus.Write16(GbaMemoryMap.IwramStart + 0x400, 0x80AB);
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = 1,
            [2] = GbaMemoryMap.IwramStart + 0x400,
            [3] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(0xAB00_0080u, cpu[0]);
        Assert.Equal(0xFFFF_FF80u, cpu[4]);
    }

    [Fact]
    public void StepExecutesArmUnalignedSignedHalfwordAsSignedByte()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE1D0_10F1); // ldrsh r1, [r0, #1]
        bus.Write16(GbaMemoryMap.IwramStart, 0x807F);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0xFFFF_FF80u, cpu[1]);
    }

    [Fact]
    public void StepExecutesThumbSoftwareInterruptExceptionEntry()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        bios[0] = 1;
        var bus = new MemoryBus(bios);
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        bus.Write16(GbaMemoryMap.IwramStart, 0xDF00);      // swi 0
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.Supervisor, cpu.Mode);
        Assert.False(cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.IwramStart + 2, cpu[14]);
        Assert.Equal(0x08u, cpu.Pc);
    }

    [Fact]
    public void NoBiosSoftResetJumpsToRomAndRestoresStacks()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF00_0000); // swi 0
        bus.Write8(0x0300_7FFA, 0);
        bus.Write32(0x0300_7E00, 0xDEAD_BEEF);
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = 0x1234_5678,
            [13] = 0x0300_7000,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.False(cpu.ThumbState);
        Assert.False(cpu.IrqDisabled);
        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.Equal(GbaMemoryMap.GamePakRomStart, cpu.Pc);
        Assert.Equal(0u, cpu[0]);
        Assert.Equal(0u, cpu[1]);
        Assert.Equal(0x0300_7F00u, cpu[13]);
        Assert.Equal(0u, cpu[14]);
        Assert.Equal(0u, bus.Read32(0x0300_7E00));
    }

    [Fact]
    public void NoBiosSoftResetCanJumpToEwram()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart + 0x100, 0xEF00_0000); // swi 0
        bus.Write8(0x0300_7FFA, 1);
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart + 0x100
        };

        cpu.Step();

        Assert.False(cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.EwramStart, cpu.Pc);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesCpuSet()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0B_0000); // swi CpuSet
        bus.Write16(GbaMemoryMap.IwramStart, 0x1234);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [2] = 1,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x1234, bus.Read16(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void HaltWaitsWithoutExecutingAndWakesOnEnabledInterruptWhenImeIsClear()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF02_0000); // swi Halt
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE3A0_002A); // mov r0, #42
        bus.InterruptEnable = IoRegisters.InterruptVBlank;
        bus.InterruptMasterEnable = false;
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.True(cpu.IsHalted);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);

        cpu.Step();

        Assert.True(cpu.IsHalted);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
        Assert.Equal(0u, cpu[0]);

        bus.RequestInterrupt(IoRegisters.InterruptVBlank);
        cpu.Step();

        Assert.False(cpu.IsHalted);
        Assert.Equal(42u, cpu[0]);
        Assert.Equal(GbaMemoryMap.EwramStart + 8, cpu.Pc);
    }

    [Fact]
    public void HaltDoesNotWakeForRequestedButDisabledInterrupt()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF02_0000); // swi Halt
        bus.InterruptEnable = IoRegisters.InterruptHBlank;
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        bus.RequestInterrupt(IoRegisters.InterruptVBlank);
        cpu.Step();

        Assert.True(cpu.IsHalted);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesArcTan()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF09_0000); // swi ArcTan
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0x4000, // 1.0 in signed 2.14 fixed-point
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x2000u, cpu[0]);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesArcTan2()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0A_0000); // swi ArcTan2
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0,
            [1] = 0x4000, // y = 1.0
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x4000u, cpu[0]);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesMidiKey2Freq()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF1F_0000); // swi MidiKey2Freq
        var waveData = GbaMemoryMap.IwramStart + 0x100;
        bus.Write32(waveData + 4, 0x0010_0000);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = waveData,
            [1] = 168,
            [2] = 0,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x0008_0000u, cpu[0]);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesGetBiosChecksum()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0D_0000); // swi GetBiosChecksum
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0xBAAE_187Fu, cpu[0]);
        Assert.Equal(1u, cpu[1]);
        Assert.Equal((uint)GbaMemoryMap.BiosSize, cpu[3]);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesSoundBias()
    {
        var bus = new MemoryBus();
        bus.PokeIo16(IoRegisters.SOUNDBIAS, 0xC123);
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF19_0000); // swi SoundBias
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 1,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0xC200, bus.PeekIo16(IoRegisters.SOUNDBIAS));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosCpuSetAlignsWordSourceAndDestination()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0B_0000); // swi CpuSet
        bus.Write32(GbaMemoryMap.IwramStart + 0x20, 0x4657_B5F0);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x21,
            [1] = GbaMemoryMap.IwramStart + 0x101,
            [2] = (1u << 26) | 1,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x4657_B5F0u, bus.Read32(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosCpuSetAdvancesScratchAddressRegisters()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0B_0000); // swi CpuSet
        bus.Write32(GbaMemoryMap.IwramStart + 0x20, 0x1111_1111);
        bus.Write32(GbaMemoryMap.IwramStart + 0x24, 0x2222_2222);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x20,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [2] = (1u << 26) | 2,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(GbaMemoryMap.IwramStart + 0x28, cpu[0]);
        Assert.Equal(GbaMemoryMap.IwramStart + 0x108, cpu[1]);
        Assert.Equal((1u << 26) | 2, cpu[2]);
        Assert.Equal(0x2222_2222u, cpu[3]);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosCpuSetFillKeepsSourceAndAdvancesDestination()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0B_0000); // swi CpuSet
        bus.Write32(GbaMemoryMap.IwramStart + 0x20, 0xCAFE_BABE);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x20,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [2] = (1u << 26) | (1u << 24) | 2,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(GbaMemoryMap.IwramStart + 0x20, cpu[0]);
        Assert.Equal(GbaMemoryMap.IwramStart + 0x108, cpu[1]);
        Assert.Equal((1u << 26) | (1u << 24) | 2, cpu[2]);
        Assert.Equal(0xCAFE_BABEu, cpu[3]);
        Assert.Equal(0xCAFE_BABEu, bus.Read32(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(0xCAFE_BABEu, bus.Read32(GbaMemoryMap.IwramStart + 0x104));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosCpuSetAlignsHalfwordSourceAndDestination()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0B_0000); // swi CpuSet
        bus.Write16(GbaMemoryMap.IwramStart + 0x20, 0xB5F0);

        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x21,
            [1] = GbaMemoryMap.IwramStart + 0x101,
            [2] = 1,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0xB5F0, bus.Read16(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosCpuSetRejectsBiosSource()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0B_0000); // swi CpuSet
        bus.Write32(GbaMemoryMap.IwramStart + 0x100, 0xDEAD_BEEF);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [2] = (1u << 26) | 1,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0xDEAD_BEEFu, bus.Read32(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesCpuFastSetWordCount()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0C_0000); // swi CpuFastSet
        for (var i = 0; i < 8; i++)
        {
            bus.Write32(GbaMemoryMap.IwramStart + (uint)(i * 4), 0x1111_0000u + (uint)i);
        }

        bus.Write32(GbaMemoryMap.IwramStart + 8 * 4, 0xDEAD_BEEF);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [2] = 8,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(0x1111_0000u + (uint)i, bus.Read32(GbaMemoryMap.IwramStart + 0x100 + (uint)(i * 4)));
        }

        Assert.NotEqual(0xDEAD_BEEF, bus.Read32(GbaMemoryMap.IwramStart + 0x100 + 8 * 4));
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosCpuFastSetRoundsCountToEightWords()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0C_0000); // swi CpuFastSet
        for (var i = 0; i < 16; i++)
        {
            bus.Write32(GbaMemoryMap.IwramStart + (uint)(i * 4), 0x2222_0000u + (uint)i);
        }

        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [2] = 9,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(0x2222_0000u + (uint)i, bus.Read32(GbaMemoryMap.IwramStart + 0x100 + (uint)(i * 4)));
        }
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosCpuSetConsumesTransferCycles()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0B_0000); // swi CpuSet
        bus.Write16(GbaMemoryMap.IwramStart + 0x20, 0xB5F0);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart + 0x20,
            [1] = GbaMemoryMap.PaletteStart,
            [2] = 1,
            [15] = GbaMemoryMap.EwramStart
        };

        var cycles = cpu.Step();

        Assert.True(cycles > 3);
        Assert.Equal(0xB5F0, bus.Read16(GbaMemoryMap.PaletteStart));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosCpuFastSetRejectsBiosSource()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0C_0000); // swi CpuFastSet
        bus.Write32(GbaMemoryMap.IwramStart + 0x100, 0xDEAD_BEEF);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [2] = 8,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0xDEAD_BEEFu, bus.Read32(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesHuffUncomp()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF13_0000); // swi HuffUnComp
        var source = GbaMemoryMap.IwramStart;
        bus.Write32(source, 0x0000_0428); // four bytes, Huffman type, 8-bit symbols
        bus.Write8(source + 4, 1);        // three tree bytes after the size byte
        bus.Write8(source + 5, 0xC0);     // root: left and right children are leaves
        bus.Write8(source + 6, 0x12);
        bus.Write8(source + 7, 0x34);
        bus.Write32(source + 8, 0x5000_0000); // left, right, left, right
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = source,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x3412_3412u, bus.Read32(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosVBlankIntrWaitWaitsInSmallChunks()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF05_0000); // swi VBlankIntrWait
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        var cycles = cpu.Step();

        Assert.Equal(1024, cycles);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosIntrWaitReturnsWhenBiosMirrorHasRequestedFlag()
    {
        var bus = new MemoryBus
        {
            BiosInterruptFlags = IoRegisters.InterruptTimer0
        };
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF04_0000); // swi IntrWait
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0,
            [1] = IoRegisters.InterruptTimer0,
            [15] = GbaMemoryMap.EwramStart
        };

        var cycles = cpu.Step();

        Assert.Equal(3, cycles);
        Assert.Equal(0, bus.BiosInterruptFlags & IoRegisters.InterruptTimer0);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosVBlankIntrWaitClearsOldBiosMirrorFlag()
    {
        var bus = new MemoryBus
        {
            BiosInterruptFlags = IoRegisters.InterruptVBlank
        };
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF05_0000); // swi VBlankIntrWait
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        var cycles = cpu.Step();

        Assert.Equal(1024, cycles);
        Assert.Equal(0, bus.BiosInterruptFlags & IoRegisters.InterruptVBlank);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosIntrWaitEnablesIme()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = IoRegisters.InterruptTimer0,
            InterruptFlags = IoRegisters.InterruptTimer0,
            InterruptMasterEnable = false
        };
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF04_0000); // swi IntrWait
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0,
            [1] = IoRegisters.InterruptTimer0,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.True(bus.InterruptMasterEnable);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosIntrWaitBlocksFollowingInstructionUntilRequestedFlag()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF04_0000); // swi IntrWait
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE3A0_2007); // mov r2, #7
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0,
            [1] = IoRegisters.InterruptTimer0,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();

        Assert.Equal(0u, cpu[2]);
        Assert.Equal(GbaMemoryMap.EwramStart + 4, cpu.Pc);

        bus.BiosInterruptFlags = IoRegisters.InterruptTimer0;
        var releaseCycles = cpu.Step();
        cpu.Step();

        Assert.Equal(3, releaseCycles);
        Assert.Equal(0, bus.BiosInterruptFlags & IoRegisters.InterruptTimer0);
        Assert.Equal(7u, cpu[2]);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosIntrWaitAllowsNoBiosIrqHandlerToReleaseWait()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = IoRegisters.InterruptTimer0
        };
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF04_0000); // swi IntrWait
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE3A0_2007); // mov r2, #7
        bus.Write32(GbaMemoryMap.IwramStart, 0xE12F_FF1E); // bx lr
        bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0,
            [1] = IoRegisters.InterruptTimer0,
            [15] = GbaMemoryMap.EwramStart
        };
        cpu.SetIrqEnabled(true);

        cpu.Step();
        bus.RequestInterrupt(IoRegisters.InterruptTimer0);
        cpu.Step();

        Assert.Equal(CpuMode.Irq, cpu.Mode);
        Assert.Equal(GbaMemoryMap.IwramStart, cpu.Pc);

        bus.BiosInterruptFlags = IoRegisters.InterruptTimer0;
        bus.InterruptFlags = 0;
        cpu.Step();
        var releaseCycles = cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.Equal(3, releaseCycles);
        Assert.Equal(0, bus.BiosInterruptFlags & IoRegisters.InterruptTimer0);
        Assert.Equal(7u, cpu[2]);
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesLz77UncompWram()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF11_0000); // swi LZ77UnCompWram
        bus.Write32(GbaMemoryMap.IwramStart, 0x0000_0510); // LZ77 header, 5 bytes
        bus.Write8(GbaMemoryMap.IwramStart + 4, 0x40); // one literal, then one compressed block
        bus.Write8(GbaMemoryMap.IwramStart + 5, 0xAB);
        bus.Write8(GbaMemoryMap.IwramStart + 6, 0x10); // length 4, disp 1
        bus.Write8(GbaMemoryMap.IwramStart + 7, 0x00);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        for (uint i = 0; i < 5; i++)
        {
            Assert.Equal(0xAB, bus.Read8(GbaMemoryMap.IwramStart + 0x100 + i));
        }
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosLz77UncompWramIgnoresInvalidHeader()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF11_0000); // swi LZ77UnCompWram
        bus.Write32(GbaMemoryMap.IwramStart, 0xE1E1_E1E1); // invalid LZ77 header
        bus.Write32(GbaMemoryMap.IwramStart + 0x100, 0xCAFE_BABE);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0xCAFE_BABEu, bus.Read32(GbaMemoryMap.IwramStart + 0x100));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesLz77UncompVram()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF12_0000); // swi LZ77UnCompVram
        bus.Write32(GbaMemoryMap.IwramStart, 0x0000_0410); // LZ77 header, 4 bytes
        bus.Write8(GbaMemoryMap.IwramStart + 4, 0x00); // four literal bytes
        bus.Write8(GbaMemoryMap.IwramStart + 5, 0x12);
        bus.Write8(GbaMemoryMap.IwramStart + 6, 0x34);
        bus.Write8(GbaMemoryMap.IwramStart + 7, 0x56);
        bus.Write8(GbaMemoryMap.IwramStart + 8, 0x78);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.VramStart,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x3412, bus.Read16(GbaMemoryMap.VramStart));
        Assert.Equal(0x7856, bus.Read16(GbaMemoryMap.VramStart + 2));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesBitUnpack()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF10_0000); // swi BitUnPack
        bus.Write8(GbaMemoryMap.IwramStart, 0b1110_0100);
        bus.Write16(GbaMemoryMap.IwramStart + 0x20, 1); // one source byte
        bus.Write8(GbaMemoryMap.IwramStart + 0x22, 2); // 2-bit source values
        bus.Write8(GbaMemoryMap.IwramStart + 0x23, 8); // 8-bit destination values
        bus.Write32(GbaMemoryMap.IwramStart + 0x24, 1); // add one to non-zero values
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [2] = GbaMemoryMap.IwramStart + 0x20,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x00u, bus.Read8(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(0x02u, bus.Read8(GbaMemoryMap.IwramStart + 0x101));
        Assert.Equal(0x03u, bus.Read8(GbaMemoryMap.IwramStart + 0x102));
        Assert.Equal(0x04u, bus.Read8(GbaMemoryMap.IwramStart + 0x103));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesRunLengthUncompWram()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF14_0000); // swi RLUnCompWram
        bus.Write32(GbaMemoryMap.IwramStart, 0x0000_0530); // RL header, 5 bytes
        bus.Write8(GbaMemoryMap.IwramStart + 4, 0x01); // two literal bytes
        bus.Write8(GbaMemoryMap.IwramStart + 5, 0x12);
        bus.Write8(GbaMemoryMap.IwramStart + 6, 0x34);
        bus.Write8(GbaMemoryMap.IwramStart + 7, 0x82); // five repeated bytes
        bus.Write8(GbaMemoryMap.IwramStart + 8, 0x56);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x12u, bus.Read8(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(0x34u, bus.Read8(GbaMemoryMap.IwramStart + 0x101));
        Assert.Equal(0x56u, bus.Read8(GbaMemoryMap.IwramStart + 0x102));
        Assert.Equal(0x56u, bus.Read8(GbaMemoryMap.IwramStart + 0x104));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesRunLengthUncompVram()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF15_0000); // swi RLUnCompVram
        bus.Write32(GbaMemoryMap.IwramStart, 0x0000_0430); // RL header, 4 bytes
        bus.Write8(GbaMemoryMap.IwramStart + 4, 0x83); // six repeated bytes, clipped to four
        bus.Write8(GbaMemoryMap.IwramStart + 5, 0x7A);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.VramStart,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x7A7A, bus.Read16(GbaMemoryMap.VramStart));
        Assert.Equal(0x7A7A, bus.Read16(GbaMemoryMap.VramStart + 2));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesDiff8bitUnfilterWram()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF16_0000); // swi Diff8bitUnFilterWram
        bus.Write32(GbaMemoryMap.IwramStart, 0x0000_0480); // Diff8 header, 4 bytes
        bus.Write8(GbaMemoryMap.IwramStart + 4, 10);
        bus.Write8(GbaMemoryMap.IwramStart + 5, 2);
        bus.Write8(GbaMemoryMap.IwramStart + 6, 0xFE);
        bus.Write8(GbaMemoryMap.IwramStart + 7, 5);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(10u, bus.Read8(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(12u, bus.Read8(GbaMemoryMap.IwramStart + 0x101));
        Assert.Equal(10u, bus.Read8(GbaMemoryMap.IwramStart + 0x102));
        Assert.Equal(15u, bus.Read8(GbaMemoryMap.IwramStart + 0x103));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesDiff8bitUnfilterVram()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF17_0000); // swi Diff8bitUnFilterVram
        bus.Write32(GbaMemoryMap.IwramStart, 0x0000_0480); // Diff8 header, 4 bytes
        bus.Write8(GbaMemoryMap.IwramStart + 4, 1);
        bus.Write8(GbaMemoryMap.IwramStart + 5, 2);
        bus.Write8(GbaMemoryMap.IwramStart + 6, 3);
        bus.Write8(GbaMemoryMap.IwramStart + 7, 4);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.VramStart,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x0301, bus.Read16(GbaMemoryMap.VramStart));
        Assert.Equal(0x0A06, bus.Read16(GbaMemoryMap.VramStart + 2));
    }

    [Fact]
    public void SoftwareInterruptWithoutBiosHandlesDiff16bitUnfilter()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF18_0000); // swi Diff16bitUnFilter
        bus.Write32(GbaMemoryMap.IwramStart, 0x0000_0681); // Diff16 header, 6 bytes
        bus.Write16(GbaMemoryMap.IwramStart + 4, 10);
        bus.Write16(GbaMemoryMap.IwramStart + 6, 2);
        bus.Write16(GbaMemoryMap.IwramStart + 8, unchecked((ushort)-3));
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = GbaMemoryMap.IwramStart + 0x100,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(10, bus.Read16(GbaMemoryMap.IwramStart + 0x100));
        Assert.Equal(12, bus.Read16(GbaMemoryMap.IwramStart + 0x102));
        Assert.Equal(9, bus.Read16(GbaMemoryMap.IwramStart + 0x104));
    }

    [Fact]
    public void SoftwareInterruptWithBiosStillEntersExceptionVector()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        bios[0] = 1;
        var bus = new MemoryBus(bios);
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF0B_0000); // swi CpuSet
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(0x08u, cpu.Pc);
        Assert.Equal(CpuMode.Supervisor, cpu.Mode);
    }

    [Fact]
    public void RealBiosIrqReturnFromThumbContinuesAtNextInstruction()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        Write32(bios, 0x18, 0xE25E_F004); // subs pc, lr, #4
        var bus = new MemoryBus(bios);
        const uint thumbCode = GbaMemoryMap.EwramStart + 0x100;
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        bus.Write16(thumbCode, 0x2001); // movs r0, #1
        bus.Write16(thumbCode + 2, 0x2002); // movs r0, #2
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = thumbCode | 1,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        bus.InterruptEnable = 0x0001;
        bus.InterruptFlags = 0x0001;
        bus.InterruptMasterEnable = true;
        cpu.SetIrqEnabled(true);

        cpu.Step();
        bus.InterruptFlags = 0;
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.True(cpu.ThumbState);
        Assert.False(cpu.IrqDisabled);
        Assert.Equal(1u, cpu[0]);
        Assert.Equal(thumbCode + 2, cpu.Pc);
    }

    [Fact]
    public void StepEntersIrqWhenInterruptIsPendingAndEnabled()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = 0x0001,
            InterruptFlags = 0x0001,
            InterruptMasterEnable = true
        };
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };
        cpu.SetIrqEnabled(true);

        var cycles = cpu.Step();

        Assert.Equal(76, cycles);
        Assert.Equal(CpuMode.Irq, cpu.Mode);
        Assert.True(cpu.IrqDisabled);
        Assert.Equal(0x0000_0138u, cpu[14]);
        Assert.Equal(0x18u, cpu.Pc);
    }

    [Fact]
    public void StepDispatchesNoBiosIrqThroughGameHandlerPointer()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = 0x0001,
            InterruptFlags = 0x0001,
            InterruptMasterEnable = true
        };
        bus.Write32(0x0300_7FFC, GbaMemoryMap.EwramStart | 1);
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.RomEntryPoint
        };
        cpu.SetIrqEnabled(true);

        var cycles = cpu.Step();

        Assert.Equal(76, cycles);
        Assert.Equal(CpuMode.Irq, cpu.Mode);
        Assert.True(cpu.ThumbState);
        Assert.True(cpu.IrqDisabled);
        Assert.Equal(0x0400_0000u, cpu[0]);
        Assert.Equal(0x0000_0138u, cpu[14]);
        Assert.Equal(GbaMemoryMap.EwramStart, cpu.Pc);
    }

    [Fact]
    public void StepDispatchesNoBiosIrqDoesNotAutoMirrorPendingInterrupts()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = IoRegisters.InterruptVBlank,
            InterruptFlags = IoRegisters.InterruptVBlank | IoRegisters.InterruptHBlank,
            InterruptMasterEnable = true,
            BiosInterruptFlags = IoRegisters.InterruptTimer0
        };
        bus.Write32(0x0300_7FFC, GbaMemoryMap.EwramStart | 1);
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.RomEntryPoint
        };
        cpu.SetIrqEnabled(true);

        cpu.Step();

        Assert.Equal(IoRegisters.InterruptTimer0, bus.BiosInterruptFlags);
    }

    [Fact]
    public void NoBiosIrqHandlerReturnAcknowledgesOriginalHardwareInterrupts()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = IoRegisters.InterruptVBlank,
            InterruptFlags = IoRegisters.InterruptVBlank | IoRegisters.InterruptHBlank,
            InterruptMasterEnable = true
        };
        bus.Write32(GbaMemoryMap.IwramStart, 0xE12F_FF1E); // bx lr
        bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.RomEntryPoint
        };
        cpu.SetIrqEnabled(true);

        cpu.Step();
        cpu.Step();

        Assert.Equal(0, bus.InterruptFlags & IoRegisters.InterruptVBlank);
        Assert.Equal(IoRegisters.InterruptHBlank, bus.InterruptFlags & IoRegisters.InterruptHBlank);
    }

    [Fact]
    public void NoBiosIrqHandlerReturnRestoresInterruptedThumbState()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = 0x0001,
            InterruptMasterEnable = true
        };
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        bus.Write32(GbaMemoryMap.IwramStart, 0xE3A0_0000); // mov r0, #0
        bus.Write32(GbaMemoryMap.IwramStart + 4, 0xE12F_FF1E); // bx lr
        bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.RomEntryPoint | 1,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        bus.InterruptFlags = 0x0001;
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.True(cpu.ThumbState);
        Assert.False(cpu.IrqDisabled);
        Assert.Equal(GbaMemoryMap.RomEntryPoint, cpu.Pc);
        Assert.Equal(GbaMemoryMap.RomEntryPoint | 1, cpu[0]);
    }

    [Fact]
    public void NoBiosIrqHandlerReturnCanUseCopiedLinkRegister()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = 0x0001,
            InterruptMasterEnable = true
        };
        bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE1A0_000E); // mov r0, lr
        bus.Write32(GbaMemoryMap.EwramStart + 8, 0xE12F_FF10); // bx r0
        bus.Write16(GbaMemoryMap.IwramStart, 0x2001); // movs r0, #1
        bus.Write32(0x0300_7FFC, GbaMemoryMap.EwramStart + 4);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart | 1u,
            [15] = GbaMemoryMap.EwramStart
        };
        cpu.SetIrqEnabled(true);

        cpu.Step();
        bus.InterruptFlags = 0x0001;
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.True(cpu.ThumbState);
        Assert.False(cpu.IrqDisabled);
        Assert.Equal(GbaMemoryMap.IwramStart, cpu.Pc);
    }

    [Fact]
    public void NoBiosIrqStackFrameStoresExceptionLinkRegister()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = 0x0001,
            InterruptFlags = 0x0001,
            InterruptMasterEnable = true
        };
        bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.RomEntryPoint
        };
        cpu.SetIrqEnabled(true);

        cpu.Step();

        Assert.Equal(GbaMemoryMap.RomEntryPoint + 4, bus.Read32(cpu[13] + 20));
    }

    [Fact]
    public void NoBiosIrqSubsPcLrReturnRestoresSavedRegisters()
    {
        var bus = new MemoryBus
        {
            InterruptEnable = 0x0001,
            InterruptFlags = 0x0001,
            InterruptMasterEnable = true
        };
        bus.Write32(GbaMemoryMap.IwramStart, 0xE3A0_0042); // mov r0, #0x42
        bus.Write32(GbaMemoryMap.IwramStart + 4, 0xE25E_F004); // subs pc, lr, #4
        bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0x1234_5678,
            [15] = GbaMemoryMap.RomEntryPoint
        };
        cpu.SetIrqEnabled(true);

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.False(cpu.ThumbState);
        Assert.False(cpu.IrqDisabled);
        Assert.Equal(GbaMemoryMap.RomEntryPoint, cpu.Pc);
        Assert.Equal(0x1234_5678u, cpu[0]);
    }

    [Fact]
    public void StepExecutesMrsFromCpsr()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE10F_0000); // mrs r0, cpsr
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(cpu.Cpsr, cpu[0]);
        Assert.Equal((uint)CpuMode.System, cpu[0]);
    }

    [Fact]
    public void StepExecutesMsrCpsrControlFieldAndSwitchesBankedRegisters()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE129_F001); // msr cpsr_c, r1
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE129_F002); // msr cpsr_c, r2
        var cpu = new Arm7Tdmi(bus);
        cpu.Reset(useBios: true);
        cpu[1] = (uint)CpuMode.Irq | (1u << 7);
        cpu[2] = (uint)CpuMode.Supervisor | (1u << 7);
        cpu[13] = 0x1111_0000;
        cpu[14] = 0x2222_0000;
        cpu[15] = GbaMemoryMap.EwramStart;

        cpu.Step();
        cpu[13] = 0x3333_0000;
        cpu[14] = 0x4444_0000;
        cpu.Step();

        Assert.Equal(CpuMode.Supervisor, cpu.Mode);
        Assert.Equal(0x1111_0000u, cpu[13]);
        Assert.Equal(0x2222_0000u, cpu[14]);
    }

    [Fact]
    public void ArmDataProcessingRegisterShiftReadsPcWithExtraPipelineWord()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE08F_0010); // add r0, pc, r0, lsl r0
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = 0,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(GbaMemoryMap.EwramStart + 12, cpu[0]);
    }

    [Fact]
    public void ArmStoreReadsPcOneWordBeyondNormalPipelineValue()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE580_F000); // str pc, [r0]
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();

        Assert.Equal(GbaMemoryMap.EwramStart + 12, bus.Read32(GbaMemoryMap.IwramStart));
    }

    [Fact]
    public void ArmBiosPrefetchLatchesPipelineWordForProtectedReads()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        BitConverter.GetBytes(0xE12F_FF1Eu).CopyTo(bios, 0xDC); // bx lr
        BitConverter.GetBytes(0xE3A0_00D3u).CopyTo(bios, 0xE0);
        BitConverter.GetBytes(0xE129_F000u).CopyTo(bios, 0xE4);
        var bus = new MemoryBus(bios);
        var cpu = new Arm7Tdmi(bus)
        {
            [14] = GbaMemoryMap.EwramStart,
            [15] = 0xDC
        };

        cpu.Step();
        cpu.Fetch32();

        Assert.Equal(0xE129_F000u, bus.Read32(0));
    }

    [Fact]
    public void ArmPipelineKeepsFetchStageInstructionAfterMemoryWrite()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE1A0_0000);     // nop
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE1A0_0000); // nop
        bus.Write32(GbaMemoryMap.EwramStart + 8, 0xE3A0_0001); // mov r0, #1
        var cpu = new Arm7Tdmi(bus)
        {
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        bus.Write32(GbaMemoryMap.EwramStart + 8, 0xE3A0_0002); // mov r0, #2
        cpu.Step();
        cpu.Step();

        Assert.Equal(1u, cpu[0]);
    }

    [Fact]
    public void ArmTestOpcodeWithPcDestinationRestoresCpsrWithoutBranching()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE129_F000);     // msr cpsr_c, r0
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE169_F001); // msr spsr_c, r1
        bus.Write32(GbaMemoryMap.EwramStart + 8, 0xE15F_F000); // cmp pc, pc, r0
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = (uint)CpuMode.Fiq,
            [1] = (uint)CpuMode.System,
            [8] = 0x1111_1111,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu[8] = 0x2222_2222;
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.Equal(0x1111_1111u, cpu[8]);
        Assert.Equal(GbaMemoryMap.EwramStart + 12, cpu.Pc);
    }

    [Fact]
    public void SwitchingFiqModeBanksRegistersEightThroughTwelve()
    {
        var bus = new MemoryBus();
        bus.Write32(GbaMemoryMap.EwramStart, 0xE129_F000);     // msr cpsr_c, r0
        bus.Write32(GbaMemoryMap.EwramStart + 4, 0xE129_F001); // msr cpsr_c, r1
        bus.Write32(GbaMemoryMap.EwramStart + 8, 0xE129_F000); // msr cpsr_c, r0
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = (uint)CpuMode.Fiq,
            [1] = (uint)CpuMode.System,
            [8] = 0x1111_1111,
            [12] = 0x2222_2222,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        Assert.Equal(0u, cpu[8]);
        Assert.Equal(0u, cpu[12]);
        cpu[8] = 0xAAAA_AAAA;
        cpu[12] = 0xBBBB_BBBB;

        cpu.Step();
        Assert.Equal(0x1111_1111u, cpu[8]);
        Assert.Equal(0x2222_2222u, cpu[12]);

        cpu.Step();
        Assert.Equal(0xAAAA_AAAAu, cpu[8]);
        Assert.Equal(0xBBBB_BBBBu, cpu[12]);
    }

    [Fact]
    public void StepExecutesMrsAndMsrSpsr()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        Write32(bios, 0x08, 0xE14F_0000); // mrs r0, spsr
        Write32(bios, 0x0C, 0xE169_F001); // msr spsr_c, r1
        Write32(bios, 0x10, 0xE14F_2000); // mrs r2, spsr
        var bus = new MemoryBus(bios);
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF00_0000); // swi 0, saves CPSR to SPSR_svc
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = (uint)CpuMode.System,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal((uint)CpuMode.System, cpu[0]);
        Assert.Equal((uint)CpuMode.System, cpu[2] & 0x1F);
    }

    [Fact]
    public void DataProcessingWriteToPcWithSRestoresCpsrFromSpsr()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        Write32(bios, 0x08, 0xE169_F001); // msr spsr_c, r1
        Write32(bios, 0x0C, 0xE3B0_F020); // movs pc, #0x20
        var bus = new MemoryBus(bios);
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF00_0000); // swi 0
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = (uint)CpuMode.System,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.False(cpu.IrqDisabled);
        Assert.Equal(0x20u, cpu.Pc);
    }

    [Fact]
    public void DataProcessingWriteToPcWithSUsesRestoredThumbStateForAlignment()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        Write32(bios, 0x08, 0xE169_F001); // msr spsr_c, r1
        Write32(bios, 0x0C, 0xE1B0_F002); // movs pc, r2
        var bus = new MemoryBus(bios);
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF00_0000); // swi 0
        var cpu = new Arm7Tdmi(bus)
        {
            [1] = (uint)CpuMode.System | (1u << 5),
            [2] = GbaMemoryMap.EwramStart + 0x102,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.True(cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.EwramStart + 0x102, cpu.Pc);
    }

    [Fact]
    public void BlockDataTransferWithPcAndSRestoresCpsrFromSpsr()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        Write32(bios, 0x08, 0xE169_F001); // msr spsr_c, r1
        Write32(bios, 0x0C, 0xE8D0_8000); // ldmia r0, {pc}^
        var bus = new MemoryBus(bios);
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF00_0000); // swi 0
        bus.Write32(GbaMemoryMap.IwramStart, GbaMemoryMap.EwramStart + 0x102);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = GbaMemoryMap.IwramStart,
            [1] = (uint)CpuMode.System | (1u << 5),
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.True(cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.EwramStart + 0x102, cpu.Pc);
    }

    [Fact]
    public void BlockDataTransferWithPcAndSWritebackUsesOriginalBank()
    {
        var bios = new byte[GbaMemoryMap.BiosSize];
        Write32(bios, 0x08, 0xE1A0_D000); // mov sp, r0
        Write32(bios, 0x0C, 0xE169_F001); // msr spsr_c, r1
        Write32(bios, 0x10, 0xE8FD_8000); // ldmia sp!, {pc}^
        var bus = new MemoryBus(bios);
        const uint systemStack = GbaMemoryMap.IwramStart + 0x1000;
        const uint exceptionStack = GbaMemoryMap.IwramStart + 0x2000;
        bus.Write32(GbaMemoryMap.EwramStart, 0xEF00_0000); // swi 0
        bus.Write32(exceptionStack, GbaMemoryMap.EwramStart + 0x102);
        var cpu = new Arm7Tdmi(bus)
        {
            [0] = exceptionStack,
            [1] = (uint)CpuMode.System | (1u << 5),
            [13] = systemStack,
            [15] = GbaMemoryMap.EwramStart
        };

        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(CpuMode.System, cpu.Mode);
        Assert.True(cpu.ThumbState);
        Assert.Equal(systemStack, cpu[13]);
        Assert.Equal(GbaMemoryMap.EwramStart + 0x102, cpu.Pc);
    }

    [Fact]
    public void NoBiosIrqReturnViaPlainPcWriteRestoresInterruptedThumbState()
    {
        var gba = new Gba.Core.GbaSystem();
        gba.Bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0xE1A0_F00E); // mov pc, lr
        gba.Bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        gba.Bus.Write16(GbaMemoryMap.EwramStart + 0x100, 0x46C0); // nop
        gba.Cpu[0] = GbaMemoryMap.EwramStart + 0x100 | 1u;
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Cpu.Step();
        gba.Bus.InterruptEnable = IoRegisters.InterruptVBlank;
        gba.Bus.InterruptMasterEnable = true;
        gba.Bus.RequestInterrupt(IoRegisters.InterruptVBlank);

        gba.Cpu.Step();
        gba.Cpu.Step();

        Assert.Equal(CpuMode.System, gba.Cpu.Mode);
        Assert.True(gba.Cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.EwramStart + 0x100, gba.Cpu.Pc);
    }

    [Fact]
    public void NoBiosIrqReturnViaAdjustedPcWriteRestoresInterruptedThumbState()
    {
        var gba = new Gba.Core.GbaSystem();
        gba.Bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0xE12F_FF1E); // bx lr
        gba.Bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        gba.Bus.Write16(GbaMemoryMap.EwramStart + 0x100, 0x46C0); // nop
        gba.Cpu[0] = GbaMemoryMap.EwramStart + 0x100 | 1u;
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Cpu.Step();
        gba.Bus.InterruptEnable = IoRegisters.InterruptVBlank;
        gba.Bus.InterruptMasterEnable = true;
        gba.Bus.RequestInterrupt(IoRegisters.InterruptVBlank);

        gba.Cpu.Step();
        gba.Cpu.Step();

        Assert.Equal(CpuMode.System, gba.Cpu.Mode);
        Assert.True(gba.Cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.EwramStart + 0x100, gba.Cpu.Pc);
    }

    [Fact]
    public void NoBiosIrqReturnIgnoresHandlerSpsrScratchChanges()
    {
        var gba = new Gba.Core.GbaSystem();
        gba.Bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0xE14F_0000);     // mrs r0, spsr
        gba.Bus.Write32(GbaMemoryMap.IwramStart + 4, 0xE3C0_0020); // bic r0, r0, #0x20
        gba.Bus.Write32(GbaMemoryMap.IwramStart + 8, 0xE169_F000); // msr spsr_c, r0
        gba.Bus.Write32(GbaMemoryMap.IwramStart + 12, 0xE12F_FF1E); // bx lr
        gba.Bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        gba.Bus.Write16(GbaMemoryMap.EwramStart + 0x100, 0x46C0); // nop
        gba.Cpu[0] = GbaMemoryMap.EwramStart + 0x100 | 1u;
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Cpu.Step();
        gba.Bus.InterruptEnable = IoRegisters.InterruptVBlank;
        gba.Bus.InterruptMasterEnable = true;
        gba.Bus.RequestInterrupt(IoRegisters.InterruptVBlank);

        gba.Cpu.Step();
        gba.Cpu.Step();
        gba.Cpu.Step();
        gba.Cpu.Step();
        gba.Cpu.Step();

        Assert.Equal(CpuMode.System, gba.Cpu.Mode);
        Assert.True(gba.Cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.EwramStart + 0x100, gba.Cpu.Pc);
    }

    [Fact]
    public void NestedNoBiosIrqReturnsPreserveOuterInterruptedThumbState()
    {
        var gba = new Gba.Core.GbaSystem();
        gba.Bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0xE1A0_F00E); // mov pc, lr
        gba.Bus.Write32(GbaMemoryMap.EwramStart, 0xE12F_FF10); // bx r0
        gba.Bus.Write16(GbaMemoryMap.EwramStart + 0x100, 0x46C0); // nop
        gba.Cpu[0] = GbaMemoryMap.EwramStart + 0x100 | 1u;
        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Cpu.Step();
        gba.Bus.InterruptEnable = IoRegisters.InterruptVBlank;
        gba.Bus.InterruptMasterEnable = true;
        gba.Bus.RequestInterrupt(IoRegisters.InterruptVBlank);
        gba.Cpu.Step();

        gba.Cpu.SetIrqEnabled(true);
        gba.Bus.RequestInterrupt(IoRegisters.InterruptVBlank);
        gba.Cpu.Step();
        gba.Bus.InterruptFlags = 0;
        gba.Cpu.Step();
        gba.Cpu.Step();

        Assert.Equal(CpuMode.System, gba.Cpu.Mode);
        Assert.True(gba.Cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.EwramStart + 0x100, gba.Cpu.Pc);
    }

    [Fact]
    public void NoBiosIrqDefersWhileExecutingInstalledIwramHandlerWindow()
    {
        var gba = new Gba.Core.GbaSystem();
        gba.Bus.Write32(0x0300_7FFC, GbaMemoryMap.IwramStart);
        gba.Bus.Write32(GbaMemoryMap.IwramStart, 0xE1A0_F00E); // mov pc, lr
        gba.Bus.Write32(GbaMemoryMap.IwramStart + 0x400, 0xE1A0_0000); // mov r0, r0
        gba.Bus.Write32(GbaMemoryMap.EwramStart, 0xE1A0_0000); // mov r0, r0
        gba.Cpu[15] = GbaMemoryMap.IwramStart + 0x400;
        gba.Bus.InterruptEnable = IoRegisters.InterruptVBlank;
        gba.Bus.InterruptMasterEnable = true;
        gba.Bus.RequestInterrupt(IoRegisters.InterruptVBlank);

        gba.Cpu.Step();

        Assert.Equal(CpuMode.System, gba.Cpu.Mode);
        Assert.False(gba.Cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.IwramStart + 0x404, gba.Cpu.Pc);
        Assert.Equal(IoRegisters.InterruptVBlank, gba.Bus.InterruptFlags);

        gba.Cpu[15] = GbaMemoryMap.EwramStart;
        gba.Cpu.Step();

        Assert.Equal(CpuMode.Irq, gba.Cpu.Mode);
        Assert.False(gba.Cpu.ThumbState);
        Assert.Equal(GbaMemoryMap.IwramStart, gba.Cpu.Pc);
    }

    private static void Write32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
        target[offset + 3] = (byte)(value >> 24);
    }
}
