#include <cstdio>
#include "ZUC.h"

int main() {
    unsigned char key[16], iv[16];
    for (int i=0;i<16;i++) { key[i]=0x37; iv[i]=0x45; }
    unsigned int keys[16];
    ZUC::GenerateKeyArray(key, iv, keys, 16);
    printf("ZUC Keys (0x37/0x45):\n");
    for (int i=0;i<16;i++) printf("  keys[%02d] = 0x%08X\n", i, keys[i]);
    return 0;
}
