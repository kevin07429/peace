#pragma once
class ZUC {
public:
    static void Init(unsigned char* _Key, unsigned char* _IV);
    static unsigned int* GenerateKeyArray(unsigned char* _Key, unsigned char* _IV, unsigned int* _RetKeyArray, unsigned int KeyArrayCount);
};
