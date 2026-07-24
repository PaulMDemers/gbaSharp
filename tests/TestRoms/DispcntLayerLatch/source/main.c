#include <stdint.h>

#define REG_DISPCNT (*(volatile uint16_t*)0x04000000)
#define REG_DISPSTAT (*(volatile uint16_t*)0x04000004)
#define REG_VCOUNT (*(volatile uint16_t*)0x04000006)
#define REG_BG0CNT (*(volatile uint16_t*)0x04000008)
#define BG_PALETTE ((volatile uint16_t*)0x05000000)
#define BG_VRAM ((volatile uint16_t*)0x06000000)

#define DISPCNT_BG0_ENABLE (1 << 8)
#define DISPSTAT_HBLANK (1 << 1)
#define BG0_SCREEN_BLOCK 31

#ifndef WRITE_DURING_HDRAW
#define WRITE_DURING_HDRAW 0
#endif

static void wait_for_line(uint16_t line)
{
    while (REG_VCOUNT != line)
    {
    }
}

static void wait_for_hblank(void)
{
    while ((REG_DISPSTAT & DISPSTAT_HBLANK) == 0)
    {
    }
}

int main(void)
{
    BG_PALETTE[0] = 0;
    BG_PALETTE[1] = 0x03E0;

    for (unsigned halfword = 0; halfword < 16; halfword++)
    {
        BG_VRAM[halfword] = 0x1111;
    }

    volatile uint16_t* screen = BG_VRAM + BG0_SCREEN_BLOCK * 1024;
    for (unsigned entry = 0; entry < 32 * 32; entry++)
    {
        screen[entry] = 0;
    }

    REG_BG0CNT = BG0_SCREEN_BLOCK << 8;
    REG_DISPCNT = 0;

    while (1)
    {
        wait_for_line(79);
#if !WRITE_DURING_HDRAW
        wait_for_hblank();
#endif
        REG_DISPCNT = DISPCNT_BG0_ENABLE;

        wait_for_line(119);
#if !WRITE_DURING_HDRAW
        wait_for_hblank();
#endif
        REG_DISPCNT = 0;

        wait_for_line(160);
    }
}
