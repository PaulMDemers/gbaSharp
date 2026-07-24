#include <stdint.h>

#define REG_DISPCNT (*(volatile uint16_t*)0x04000000)
#define OBJ_PALETTE ((volatile uint16_t*)0x05000200)
#define OBJ_VRAM ((volatile uint16_t*)0x06010000)
#define OAM ((volatile uint16_t*)0x07000000)

#define DISPCNT_HBLANK_FREE (1 << 5)
#define DISPCNT_OBJ_1D (1 << 6)
#define DISPCNT_OBJ_ENABLE (1 << 12)
#define ATTR0_DISABLED (2 << 8)
#define ATTR1_SIZE_64 (3 << 14)

#ifndef TARGET_INDEX
#define TARGET_INDEX 14
#endif

static uint16_t rgb5(unsigned red, unsigned green, unsigned blue)
{
    return (uint16_t)(red | (green << 5) | (blue << 10));
}

static void set_object(unsigned index, uint16_t attr0, uint16_t attr1, uint16_t attr2)
{
    volatile uint16_t* object = OAM + index * 4;
    object[0] = attr0;
    object[1] = attr1;
    object[2] = attr2;
    object[3] = 0;
}

int main(void)
{
    static const uint16_t colors[8] = {
        0x001F, 0x03E0, 0x7C00, 0x03FF,
        0x7C1F, 0x7FE0, 0x4210, 0x7FFF
    };

    OBJ_PALETTE[0] = 0;
    for (unsigned color = 0; color < 8; color++)
    {
        OBJ_PALETTE[color + 1] = colors[color];
    }

    for (unsigned halfword = 0; halfword < 64 * 16; halfword++)
    {
        OBJ_VRAM[halfword] = 0;
    }

    volatile uint16_t* target_tiles = OBJ_VRAM + 64 * 16;
    for (unsigned tile = 0; tile < 64; tile++)
    {
        unsigned color = tile % 8 + 1;
        uint16_t packed = (uint16_t)(color * 0x1111);
        for (unsigned halfword = 0; halfword < 16; halfword++)
        {
            target_tiles[tile * 16 + halfword] = packed;
        }
    }

    for (unsigned object = 0; object < 128; object++)
    {
        set_object(object, ATTR0_DISABLED, 0, 0);
    }

    for (unsigned object = 0; object < TARGET_INDEX; object++)
    {
        set_object(object, 64, ATTR1_SIZE_64, 0);
    }

    set_object(TARGET_INDEX, 64, ATTR1_SIZE_64 | 80, 64);
    REG_DISPCNT = DISPCNT_HBLANK_FREE | DISPCNT_OBJ_1D | DISPCNT_OBJ_ENABLE;

    while (1)
    {
        __asm__ volatile("nop");
    }
}
